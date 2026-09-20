// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;
using Pos.Core.Payments;
using Pos.Infrastructure.Checkout;
using Pos.Infrastructure.Storage;

namespace Pos.Infrastructure.Tests;

/// <summary>
/// Tests for taking payment and then recording the sale.
/// </summary>
/// <remarks>
/// The rule under test is the one that decides whether the shop actually gets paid: a sale must not
/// be recorded against a payment that was refused. Recording first and authorising afterwards
/// means the customer is already walking out with the goods when the card declines.
/// </remarks>
public sealed class SaleCompletionTests
{
    private const string Zar = "ZAR";
    private static readonly TaxRate Vat = new("VAT", 0.15m);

    private sealed class FixedTerminal(string id) : ITerminalIdentity
    {
        public string TerminalId { get; } = id;
    }

    private sealed class StubNumbers : ISaleNumberSource
    {
        private int _next;

        public Task<SaleNumber> NextAsync(
            StoreId storeId, string storeCode, DateOnly businessDate, CancellationToken ct = default) =>
            Task.FromResult(new SaleNumber(storeCode, businessDate, ++_next));
    }

    /// <summary>A provider that refuses everything, standing in for a real terminal.</summary>
    private sealed class DecliningProvider(string reason = "Insufficient funds") : IPaymentProvider
    {
        public string ProviderId => "declining";

        public string DisplayName => "Declining terminal";

        public bool CanDecline => true;

        public List<PaymentRequest> Requests { get; } = [];

        public Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);

            return Task.FromResult(PaymentResult.Declined(request.Amount, reason));
        }
    }

    /// <summary>A provider that approves and issues an approval code.</summary>
    private sealed class ApprovingProvider : IPaymentProvider
    {
        public string ProviderId => "approving";

        public string DisplayName => "Approving terminal";

        public bool CanDecline => true;

        public List<PaymentRequest> Requests { get; } = [];

        public Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);

            return Task.FromResult(PaymentResult.ApprovedFor(request.Amount, $"AUTH-{Requests.Count:D4}"));
        }
    }

    private static Store NewStore() => new()
    {
        Id = StoreId.New(),
        Name = "CORNER STORE",
        Code = "CT01",
        Currency = Zar,
        TaxMode = TaxMode.Inclusive,
        DefaultTaxRate = Vat,
        ReceiptColumns = 48,
    };

    private static Cart NewCart(Store store, decimal price = 115.00m)
    {
        var cart = new Cart(store.Id, store.Currency, store.TaxMode);
        cart.Add(ProductId.New(), "6001000000017", "Cola 500ml", Vat, new Money(price, Zar));

        return cart;
    }

    private static (SaleCompletionService Completion, InMemoryLocalStore Local) NewTerminal(
        IPaymentProvider provider)
    {
        var local = new InMemoryLocalStore();

        var recorder = new CheckoutRecordingService(local, new StubNumbers(), new FixedTerminal("TILL-1"));

        return (new SaleCompletionService(recorder, provider), local);
    }

    // ------------------------------------------------------------------ the happy path

    [Fact]
    public async Task A_settled_basket_is_charged_and_then_recorded()
    {
        var store = NewStore();
        var cart = NewCart(store);
        var (completion, local) = NewTerminal(new ApprovingProvider());

        var session = new TenderSession(new Money(115.00m, Zar));
        session.AddExact(TenderType.ExternalCard, 115.00m);

        var completed = await completion.PayAndRecordAsync(cart, session, store);

        Assert.Equal(115.00m, completed.Sale.Total);
        Assert.NotNull(await local.GetSaleAsync(completed.Stored.Id));
    }

    [Fact]
    public async Task An_approval_code_from_the_terminal_is_recorded_on_the_tender()
    {
        // It is the code that reconciles against the terminal's own log, so it has to survive onto
        // the stored sale rather than only existing in the operator's head.
        var store = NewStore();
        var cart = NewCart(store);
        var (completion, _) = NewTerminal(new ApprovingProvider());

        var session = new TenderSession(new Money(115.00m, Zar));
        session.AddExact(TenderType.ExternalCard, 115.00m);

        var completed = await completion.PayAndRecordAsync(cart, session, store);

        Assert.Equal("AUTH-0001", Assert.Single(completed.Sale.Tenders).Reference);
    }

    [Fact]
    public async Task Cash_is_never_sent_to_a_payment_provider()
    {
        // The operator is holding the notes. A provider that could "decline" cash would let the
        // till refuse money it has already been handed.
        var store = NewStore();
        var cart = NewCart(store);
        var provider = new ApprovingProvider();
        var (completion, _) = NewTerminal(provider);

        var session = new TenderSession(new Money(115.00m, Zar));
        session.AddCash(120.00m);

        await completion.PayAndRecordAsync(cart, session, store);

        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task Cash_is_recognised_as_needing_no_instrument()
    {
        Assert.False(SaleCompletionService.RequiresInstrument(TenderType.Cash));
        Assert.True(SaleCompletionService.RequiresInstrument(TenderType.ExternalCard));
        Assert.True(SaleCompletionService.RequiresInstrument(TenderType.GiftCard));
        Assert.True(SaleCompletionService.RequiresInstrument(TenderType.Voucher));
        Assert.True(SaleCompletionService.RequiresInstrument(TenderType.LoyaltyPoints));
    }

    // ---------------------------------------------------------------------- declines

    [Fact]
    public async Task A_declined_payment_stops_the_sale_being_recorded()
    {
        // The whole reason the seam exists. If the sale were recorded first, the customer would be
        // walking out with the goods while the shop holds an uncollectable debt.
        var store = NewStore();
        var cart = NewCart(store);
        var (completion, local) = NewTerminal(new DecliningProvider());

        var session = new TenderSession(new Money(115.00m, Zar));
        session.AddExact(TenderType.ExternalCard, 115.00m);

        await Assert.ThrowsAsync<PaymentDeclinedException>(
            async () => await completion.PayAndRecordAsync(cart, session, store));

        Assert.Empty(await local.GetSalesForDateAsync(store.Id.ToString(), DateOnly.FromDateTime(DateTime.Now)));
        Assert.Empty(await local.PeekOutboxAsync());
    }

    [Fact]
    public async Task The_decline_reason_reaches_the_operator()
    {
        var store = NewStore();
        var cart = NewCart(store);
        var (completion, _) = NewTerminal(new DecliningProvider("Card expired"));

        var session = new TenderSession(new Money(115.00m, Zar));
        session.AddExact(TenderType.ExternalCard, 115.00m);

        var exception = await Assert.ThrowsAsync<PaymentDeclinedException>(
            async () => await completion.PayAndRecordAsync(cart, session, store));

        Assert.Contains("Card expired", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_decline_on_the_second_of_three_payments_records_nothing_at_all()
    {
        // A partially authorised sale is worse than none: the shop would hold money for a sale that
        // does not exist, and the customer would have no receipt for it.
        var store = NewStore();
        var cart = NewCart(store, price: 100.00m);
        var (completion, local) = NewTerminal(new DecliningProvider());

        var session = new TenderSession(new Money(100.00m, Zar));
        session.AddCash(30.00m);
        session.AddExact(TenderType.GiftCard, 40.00m);
        session.AddExact(TenderType.ExternalCard, 30.00m);

        await Assert.ThrowsAsync<PaymentDeclinedException>(
            async () => await completion.PayAndRecordAsync(cart, session, store));

        Assert.Empty(await local.PeekOutboxAsync());
    }

    [Fact]
    public async Task An_unsettled_basket_never_reaches_the_payment_provider()
    {
        // Asking a terminal for money the customer does not owe yet would be worse than useless.
        var store = NewStore();
        var cart = NewCart(store);
        var provider = new ApprovingProvider();
        var (completion, _) = NewTerminal(provider);

        var session = new TenderSession(new Money(115.00m, Zar));
        session.AddCash(50.00m);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await completion.PayAndRecordAsync(cart, session, store));

        Assert.Empty(provider.Requests);
    }

    // ------------------------------------------------------------------ record-only

    [Fact]
    public async Task The_record_only_provider_approves_and_issues_no_reference()
    {
        // It talked to nothing that could issue one, and inventing a code would put a figure in the
        // reconciliation that cannot be matched to anything.
        var provider = new RecordOnlyPaymentProvider();

        var result = await provider.ChargeAsync(
            new PaymentRequest(TenderType.ExternalCard, new Money(115.00m, Zar)));

        Assert.True(result.Approved);
        Assert.Null(result.Reference);
    }

    [Fact]
    public void The_record_only_provider_is_honest_that_it_cannot_refuse()
    {
        // A shop using a standalone card machine gets no verification from the POS, and the
        // interface says so rather than implying a guarantee that does not exist.
        Assert.False(new RecordOnlyPaymentProvider().CanDecline);
    }

    [Fact]
    public async Task The_record_only_provider_reads_a_settled_basket_end_to_end()
    {
        var store = NewStore();
        var cart = NewCart(store);
        var (completion, local) = NewTerminal(new RecordOnlyPaymentProvider());

        var session = new TenderSession(new Money(115.00m, Zar));
        session.AddCash(50.00m);
        session.AddExact(TenderType.ExternalCard, 65.00m);

        var completed = await completion.PayAndRecordAsync(cart, session, store);

        Assert.Equal(115.00m, completed.Sale.Total);
        Assert.Equal(2, completed.Sale.Tenders.Count);
        Assert.NotNull(await local.GetSaleAsync(completed.Stored.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task The_record_only_provider_refuses_a_structurally_impossible_amount(decimal amount)
    {
        // Not a decline — a programming error. Recording it would put a wrong figure in the day's
        // takings with nothing to flag it.
        var provider = new RecordOnlyPaymentProvider();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await provider.ChargeAsync(new PaymentRequest(TenderType.Cash, new Money(amount, Zar))));
    }

    [Fact]
    public async Task A_split_tender_sale_is_recorded_with_the_change_the_customer_received()
    {
        // The tendered amount is what the drawer has to account for, so it must survive onto the
        // stored sale rather than being flattened to the amount applied.
        var store = NewStore();
        var cart = NewCart(store, price: 70.00m);
        var (completion, local) = NewTerminal(new RecordOnlyPaymentProvider());

        var session = new TenderSession(new Money(70.00m, Zar));
        session.AddCash(100.00m);

        var completed = await completion.PayAndRecordAsync(cart, session, store);

        var stored = await local.GetSaleAsync(completed.Stored.Id);
        Assert.NotNull(stored);

        var cash = Assert.Single(stored.Tenders);
        Assert.Equal(70.00m, cash.Amount);
        Assert.Equal(100.00m, cash.Tendered);
    }
}
