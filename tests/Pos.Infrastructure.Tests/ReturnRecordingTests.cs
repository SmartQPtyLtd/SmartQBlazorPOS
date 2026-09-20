// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;
using Pos.Infrastructure.Checkout;
using Pos.Infrastructure.Storage;

namespace Pos.Infrastructure.Tests;

/// <summary>
/// Return recording tests.
/// </summary>
/// <remarks>
/// These exercise the whole refund path against real storage: policy enforcement, atomic
/// commit, sale numbering, and the stock ledger. The policy itself is unit-tested in
/// <c>Pos.Core.Tests</c>; what matters here is that the service cannot be talked into bypassing
/// it.
/// </remarks>
public sealed class ReturnRecordingTests
{
    private const string Zar = "ZAR";
    private static readonly TaxRate Vat15 = new("VAT", 0.15m);

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

    private static Store NewStore() => new()
    {
        Id = StoreId.New(),
        Name = "CORNER STORE",
        Code = "CT01",
        Currency = Zar,
        TaxMode = TaxMode.Inclusive,
        DefaultTaxRate = Vat15,
        ReceiptColumns = 48,
    };

    /// <summary>
    /// A terminal with one store, one storage instance, and both recording services.
    /// </summary>
    /// <remarks>
    /// A single shared store is essential: separate instances would mean a return cannot see the
    /// sale it refers to, which is exactly what a real terminal never does.
    /// </remarks>
    private sealed class Terminal
    {
        public InMemoryLocalStore Store { get; } = new();

        public Store Shop { get; } = NewStore();

        public CheckoutRecordingService Checkout { get; }

        public ReturnRecordingService Returns { get; }

        public Terminal()
        {
            var identity = new FixedTerminal("TILL-1");
            Checkout = new CheckoutRecordingService(Store, new StubNumbers(), identity);
            Returns = new ReturnRecordingService(Store, identity);
        }

        public async Task<StoredSale> SellAsync(params (string Name, decimal Price, decimal Qty)[] items)
        {
            var cart = new Cart(Shop.Id, Shop.Currency, Shop.TaxMode);

            foreach (var (name, price, qty) in items)
            {
                cart.Add(ProductId.New(), Barcode(name), name, Vat15, new Money(price, Zar), qty);
            }

            var total = cart.CalculateTotals().Total;

            var completed = await Checkout.RecordSaleAsync(
                cart,
                [new Tender(TenderType.Cash, new Money(total, Zar), new Money(total, Zar))],
                Shop);

            return completed.Stored;
        }
    }

    private static string Barcode(string name) =>
        $"600{Math.Abs(name.GetHashCode(StringComparison.Ordinal)) % 1_000_000_000:D9}";

    [Fact]
    public async Task A_refund_is_written_and_queued_atomically()
    {
        var terminal = new Terminal();
        var sale = await terminal.SellAsync(("Cola", 15.00m, 1m));

        var completed = await terminal.Returns.RecordReturnAsync(
            sale.Id,
            [(sale.Lines[0].Barcode, 1m)],
            ReturnReason.Faulty);

        var reloaded = await terminal.Store.GetReturnAsync(completed.Stored.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(15.00m, reloaded.TotalRefund);

        // The refund and its stock movement are both queued for the hub.
        var pending = await terminal.Store.PeekOutboxAsync();
        Assert.Contains(pending, e => e.EntityType == "salesReturn");
        Assert.Contains(pending, e => e.EntityType == "stockMovement");
    }

    [Fact]
    public async Task A_returned_item_goes_back_into_stock()
    {
        // Goods that come back are back on the shelf. Recording that as a compensating movement
        // is what keeps the ledger auditable.
        var terminal = new Terminal();
        var sale = await terminal.SellAsync(("Cola", 15.00m, 3m));

        await terminal.Returns.RecordReturnAsync(sale.Id, [(sale.Lines[0].Barcode, 2m)], ReturnReason.ChangedMind);

        var sold = terminal.Store.Movements.Where(m => m.Reason == "Sale").Sum(m => m.QtyDelta);
        var returned = terminal.Store.Movements.Where(m => m.Reason == "CustomerReturn").Sum(m => m.QtyDelta);

        Assert.Equal(-3m, sold);
        Assert.Equal(2m, returned);

        var level = SaleMapper.DeriveStockLevel(
            terminal.Store.Movements, terminal.Shop.Id.ToString(), sale.Lines[0].ProductId.ToString());

        Assert.Equal(-1m, level);
    }

    [Fact]
    public async Task A_return_movement_is_positive_unlike_a_sale_movement()
    {
        var terminal = new Terminal();
        var sale = await terminal.SellAsync(("Cola", 15.00m, 1m));

        await terminal.Returns.RecordReturnAsync(sale.Id, [(sale.Lines[0].Barcode, 1m)], ReturnReason.Faulty);

        var movement = terminal.Store.Movements.Single(m => m.Reason == "CustomerReturn");

        Assert.True(movement.QtyDelta > 0m);
        Assert.Equal(1m, movement.QtyDelta);
    }

    [Fact]
    public async Task A_return_movement_references_the_refund()
    {
        // Without the reference, a stock movement cannot be traced back to why it happened.
        var terminal = new Terminal();
        var sale = await terminal.SellAsync(("Cola", 15.00m, 1m));

        var completed = await terminal.Returns.RecordReturnAsync(
            sale.Id, [(sale.Lines[0].Barcode, 1m)], ReturnReason.Faulty);

        var movement = terminal.Store.Movements.Single(m => m.Reason == "CustomerReturn");

        Assert.Equal(completed.Stored.Id, movement.Reference);
    }

    [Fact]
    public async Task The_original_sale_is_left_completely_untouched()
    {
        // The sale is a historical fact. Rewriting it would destroy the audit trail.
        var terminal = new Terminal();
        var sale = await terminal.SellAsync(("Cola", 15.00m, 2m));

        await terminal.Returns.RecordReturnAsync(sale.Id, [(sale.Lines[0].Barcode, 2m)], ReturnReason.Faulty);

        var reloaded = await terminal.Store.GetSaleAsync(sale.Id);

        Assert.NotNull(reloaded);
        Assert.Equal(sale.Total, reloaded.Total);
        Assert.Equal("Completed", reloaded.Status);
        Assert.Equal(2m, reloaded.Lines[0].Quantity);
    }

    [Fact]
    public async Task Repeated_partial_returns_are_refused_once_the_sale_is_exhausted()
    {
        // The abuse this exists to stop: return two of five, three times over.
        var terminal = new Terminal();
        var sale = await terminal.SellAsync(("Cola", 15.00m, 5m));

        await terminal.Returns.RecordReturnAsync(sale.Id, [(sale.Lines[0].Barcode, 2m)], ReturnReason.ChangedMind);
        await terminal.Returns.RecordReturnAsync(sale.Id, [(sale.Lines[0].Barcode, 2m)], ReturnReason.ChangedMind);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Returns.RecordReturnAsync(sale.Id, [(sale.Lines[0].Barcode, 2m)], ReturnReason.ChangedMind));

        Assert.Contains("leaving 1", exception.Message);
    }

    [Fact]
    public async Task A_split_return_of_the_whole_sale_is_allowed()
    {
        // Two visits, one unit each, is legitimate and must work.
        var terminal = new Terminal();
        var sale = await terminal.SellAsync(("Cola", 15.00m, 2m));

        var first = await terminal.Returns.RecordReturnAsync(sale.Id, [(sale.Lines[0].Barcode, 1m)], ReturnReason.Faulty);
        var second = await terminal.Returns.RecordReturnAsync(sale.Id, [(sale.Lines[0].Barcode, 1m)], ReturnReason.Faulty);

        Assert.Equal(15.00m, first.Stored.TotalRefund);
        Assert.Equal(15.00m, second.Stored.TotalRefund);

        // GetTradingDayReturnsAsync takes the strongly-typed id and a DateOnly, while the stored
        // sale carries both in their persisted string forms.
        var all = await terminal.Returns.GetTradingDayReturnsAsync(
            new StoreId(Guid.Parse(sale.StoreId)),
            DateOnly.ParseExact(
                sale.BusinessDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Preparing_a_refund_reports_what_remains_refundable()
    {
        var terminal = new Terminal();
        var sale = await terminal.SellAsync(("Cola", 15.00m, 4m));

        await terminal.Returns.RecordReturnAsync(sale.Id, [(sale.Lines[0].Barcode, 1m)], ReturnReason.Faulty);

        var prepared = await terminal.Returns.PrepareRefundAsync(sale.Id);

        Assert.NotNull(prepared);
        Assert.Equal(3m, prepared.Value.State.RefundableQuantity(prepared.Value.State.Sale.Lines[0]));
        Assert.Single(prepared.Value.PreviousReturns);
    }

    [Fact]
    public async Task Preparing_a_refund_for_an_unknown_sale_returns_nothing()
    {
        var terminal = new Terminal();
        _ = await terminal.SellAsync(("Cola", 15.00m, 1m));

        Assert.Null(await terminal.Returns.PrepareRefundAsync("no-such-sale"));
    }

    [Fact]
    public async Task Refunding_a_barcode_that_is_not_on_the_sale_is_refused()
    {
        var terminal = new Terminal();
        var sale = await terminal.SellAsync(("Cola", 15.00m, 1m));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Returns.RecordReturnAsync(sale.Id, [("9999999999999", 1m)], ReturnReason.Faulty));

        Assert.Contains("is not on that sale", exception.Message);
    }

    [Fact]
    public async Task Refunding_a_voided_sale_is_refused()
    {
        var terminal = new Terminal();
        var sale = await terminal.SellAsync(("Cola", 15.00m, 1m));

        // Mark it voided directly, as a void operation would.
        var voided = sale with { Status = "Voided" };
        await terminal.Store.CommitSaleAsync(voided, "{}", [], [], "TILL-1");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Returns.RecordReturnAsync(sale.Id, [(sale.Lines[0].Barcode, 1m)], ReturnReason.Faulty));

        Assert.Contains("voided", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refund_numbers_are_separate_from_receipt_numbers()
    {
        // A refund number mistaken for a receipt number would be dangerous on a document that
        // is explicitly not a receipt.
        var terminal = new Terminal();
        var sale = await terminal.SellAsync(("Cola", 15.00m, 1m));

        var completed = await terminal.Returns.RecordReturnAsync(
            sale.Id, [(sale.Lines[0].Barcode, 1m)], ReturnReason.Faulty);

        Assert.StartsWith("R-", completed.Stored.Number, StringComparison.Ordinal);
        Assert.NotEqual(sale.Number, completed.Stored.Number);
    }

    [Fact]
    public async Task Refund_numbers_increment_within_a_day()
    {
        var terminal = new Terminal();
        var first = await terminal.SellAsync(("Cola", 15.00m, 1m));
        var second = await terminal.SellAsync(("Bread", 20.00m, 1m));

        var a = await terminal.Returns.RecordReturnAsync(first.Id, [(first.Lines[0].Barcode, 1m)], ReturnReason.Faulty);
        var b = await terminal.Returns.RecordReturnAsync(second.Id, [(second.Lines[0].Barcode, 1m)], ReturnReason.Faulty);

        Assert.EndsWith("0001", a.Stored.Number, StringComparison.Ordinal);
        Assert.EndsWith("0002", b.Stored.Number, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refunding_cash_for_a_card_sale_is_reported_as_a_warning()
    {
        // A classic fraud route. Allowed for legitimate reasons, but never silently.
        var local = new InMemoryLocalStore();
        var shop = NewStore();
        var checkout = new CheckoutRecordingService(local, new StubNumbers(), new FixedTerminal("TILL-1"));
        var returns = new ReturnRecordingService(local, new FixedTerminal("TILL-1"));

        var cart = new Cart(shop.Id, shop.Currency, shop.TaxMode);
        cart.Add(ProductId.New(), "6001000000017", "Cola", Vat15, new Money(15.00m, Zar), 1m);

        var completed = await checkout.RecordSaleAsync(
            cart, [new Tender(TenderType.ExternalCard, new Money(15.00m, Zar))], shop);

        var refund = await returns.RecordReturnAsync(
            completed.Stored.Id,
            [(completed.Stored.Lines[0].Barcode, 1m)],
            ReturnReason.Faulty,
            refundTenderType: TenderType.Cash);

        Assert.Single(refund.TenderWarnings);
        Assert.Contains("not paid that way", refund.TenderWarnings[0]);
    }

    [Fact]
    public async Task Refunding_by_the_original_method_produces_no_warning()
    {
        var local = new InMemoryLocalStore();
        var shop = NewStore();
        var checkout = new CheckoutRecordingService(local, new StubNumbers(), new FixedTerminal("TILL-1"));
        var returns = new ReturnRecordingService(local, new FixedTerminal("TILL-1"));

        var cart = new Cart(shop.Id, shop.Currency, shop.TaxMode);
        cart.Add(ProductId.New(), "6001000000017", "Cola", Vat15, new Money(15.00m, Zar), 1m);

        var completed = await checkout.RecordSaleAsync(
            cart, [new Tender(TenderType.ExternalCard, new Money(15.00m, Zar))], shop);

        var refund = await returns.RecordReturnAsync(
            completed.Stored.Id,
            [(completed.Stored.Lines[0].Barcode, 1m)],
            ReturnReason.Faulty,
            refundTenderType: TenderType.ExternalCard);

        Assert.Empty(refund.TenderWarnings);
    }

    [Fact]
    public async Task The_reversed_tax_matches_what_was_charged()
    {
        // The reversal has to reconcile against the tax return.
        var terminal = new Terminal();
        var sale = await terminal.SellAsync(("Cola", 115.00m, 1m));

        var completed = await terminal.Returns.RecordReturnAsync(
            sale.Id, [(sale.Lines[0].Barcode, 1m)], ReturnReason.Faulty);

        Assert.Equal(sale.TaxTotal, completed.Stored.TaxReversed);
        Assert.Equal(sale.Total, completed.Stored.TotalRefund);
    }

    [Fact]
    public async Task A_refund_records_the_reason_and_the_operator()
    {
        // Refunds are the most abuse-prone operation, so accountability is recorded.
        var terminal = new Terminal();
        var sale = await terminal.SellAsync(("Cola", 15.00m, 1m));

        var completed = await terminal.Returns.RecordReturnAsync(
            sale.Id,
            [(sale.Lines[0].Barcode, 1m)],
            ReturnReason.Damaged,
            employeeId: "EMP-7",
            note: "Crushed in transit");

        Assert.Equal("Damaged", completed.Stored.Reason);
        Assert.Equal("EMP-7", completed.Stored.EmployeeId);
        Assert.Equal("Crushed in transit", completed.Stored.Note);
    }

    [Fact]
    public async Task A_refund_can_be_attributed_to_an_earlier_trading_day()
    {
        // A till trading after midnight must still be able to refund against the right day.
        var terminal = new Terminal();
        var sale = await terminal.SellAsync(("Cola", 15.00m, 1m));
        var yesterday = DateOnly.FromDateTime(DateTime.Now).AddDays(-1);

        var completed = await terminal.Returns.RecordReturnAsync(
            sale.Id,
            [(sale.Lines[0].Barcode, 1m)],
            ReturnReason.Faulty,
            businessDate: yesterday);

        Assert.Equal(
            yesterday.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            completed.Stored.BusinessDate);

        var forDay = await terminal.Returns.GetTradingDayReturnsAsync(terminal.Shop.Id, yesterday);
        Assert.Single(forDay);
    }

    [Fact]
    public async Task Returns_are_scoped_to_their_sale()
    {
        // A return against one sale must never reduce what is refundable on another.
        var terminal = new Terminal();
        var first = await terminal.SellAsync(("Cola", 15.00m, 2m));
        var second = await terminal.SellAsync(("Cola", 15.00m, 2m));

        await terminal.Returns.RecordReturnAsync(first.Id, [(first.Lines[0].Barcode, 2m)], ReturnReason.Faulty);

        var prepared = await terminal.Returns.PrepareRefundAsync(second.Id);

        Assert.NotNull(prepared);
        Assert.Equal(2m, prepared.Value.State.RefundableQuantity(prepared.Value.State.Sale.Lines[0]));
    }

    [Fact]
    public async Task A_round_trip_through_storage_preserves_the_refund()
    {
        // Reprinting a slip must reproduce exactly what was refunded.
        var terminal = new Terminal();
        var sale = await terminal.SellAsync(("Cola", 115.00m, 2m));

        var completed = await terminal.Returns.RecordReturnAsync(
            sale.Id, [(sale.Lines[0].Barcode, 1m)], ReturnReason.Faulty, employeeId: "EMP-7");

        var stored = await terminal.Store.GetReturnAsync(completed.Stored.Id);
        Assert.NotNull(stored);

        var domain = SaleMapper.ToDomain(stored);

        Assert.Equal(completed.Return.TotalRefund, domain.TotalRefund);
        Assert.Equal(completed.Return.TaxReversed, domain.TaxReversed);
        Assert.Equal(ReturnReason.Faulty, domain.Reason);
        Assert.Equal("EMP-7", domain.EmployeeId);
        Assert.Equal(completed.Return.OriginalSaleNumber, domain.OriginalSaleNumber);
    }

    [Fact]
    public async Task A_failed_commit_leaves_no_refund_and_no_queue_entry()
    {
        var terminal = new Terminal();
        var sale = await terminal.SellAsync(("Cola", 15.00m, 1m));

        var before = await terminal.Store.GetOutboxSummaryAsync();

        terminal.Store.FailNextCommit = new IOException("Disk full.");

        await Assert.ThrowsAsync<IOException>(async () =>
            await terminal.Returns.RecordReturnAsync(sale.Id, [(sale.Lines[0].Barcode, 1m)], ReturnReason.Faulty));

        var after = await terminal.Store.GetOutboxSummaryAsync();

        Assert.Equal(before.Total, after.Total);
        Assert.Null(await terminal.Store.GetReturnAsync("anything"));
        Assert.Empty(await terminal.Store.GetReturnsForSaleAsync(sale.Id));
    }

    // ------------------------------------------------------------- refund authority

    private static Employee Operator(string name, EmployeePermissions permissions)
    {
        var id = EmployeeId.New();

        return new Employee
        {
            Id = id,
            StoreId = StoreId.New(),
            Name = name,
            PinHash = "not-used-in-these-tests",
            Permissions = permissions,
        };
    }

    [Fact]
    public async Task An_operator_without_refund_authority_cannot_process_one()
    {
        // Refunds are the highest-risk operation at a till: cash leaves the drawer and the goods
        // come back, which makes them the classic route for taking money out of a shop. A cashier
        // who can ring a sale must not thereby be able to hand the money back.
        var terminal = new Terminal();
        var sale = await terminal.SellAsync(("Cola", 115.00m, 1m));

        var cashier = Operator("Thandi", EmployeePermissions.Sell | EmployeePermissions.OpenDrawer);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Returns.RecordReturnAsync(
                sale.Id,
                [(sale.Lines[0].Barcode, 1m)],
                ReturnReason.Faulty,
                employeeId: cashier.Id.ToString(),
                authorisedBy: cashier));

        Assert.Contains("not authorised", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_refused_refund_leaves_nothing_behind()
    {
        // A refusal must be a refusal, not a partial write that shows up later as an unexplained
        // stock movement.
        var terminal = new Terminal();
        var sale = await terminal.SellAsync(("Cola", 115.00m, 1m));
        var cashier = Operator("Thandi", EmployeePermissions.Sell);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Returns.RecordReturnAsync(
                sale.Id,
                [(sale.Lines[0].Barcode, 1m)],
                ReturnReason.Faulty,
                authorisedBy: cashier));

        Assert.Empty(await terminal.Store.GetReturnsForSaleAsync(sale.Id));

        // The sale's own decrement is legitimately there; what a refused refund must not leave is
        // goods coming back into stock, which would show up later as an unexplained surplus.
        Assert.DoesNotContain(
            terminal.Store.Movements,
            m => m.Reason == StockMovementReason.CustomerReturn.ToString());
    }

    [Fact]
    public async Task A_supervisor_can_process_a_refund()
    {
        // The check must let the right person through, or it just stops refunds happening.
        var terminal = new Terminal();
        var sale = await terminal.SellAsync(("Cola", 115.00m, 1m));

        var supervisor = Operator("Sipho", EmployeePermissions.Sell | EmployeePermissions.Refund);

        var completed = await terminal.Returns.RecordReturnAsync(
            sale.Id,
            [(sale.Lines[0].Barcode, 1m)],
            ReturnReason.Faulty,
            employeeId: supervisor.Id.ToString(),
            authorisedBy: supervisor);

        Assert.Equal(115.00m, completed.Return.TotalRefund);
        Assert.Equal(supervisor.Id.ToString(), completed.Return.EmployeeId);
    }

    [Fact]
    public async Task A_refund_pulled_from_another_terminal_needs_no_local_operator()
    {
        // Sync brings refunds in with an employee id and no local employee record to check against.
        // Requiring one would strand every refund taken at a sister store.
        var terminal = new Terminal();
        var sale = await terminal.SellAsync(("Cola", 115.00m, 1m));

        var completed = await terminal.Returns.RecordReturnAsync(
            sale.Id, [(sale.Lines[0].Barcode, 1m)], ReturnReason.Faulty, employeeId: "EMP-7");

        Assert.Equal("EMP-7", completed.Return.EmployeeId);
    }
}
