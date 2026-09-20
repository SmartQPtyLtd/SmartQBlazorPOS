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
/// Sale and refund lookup tests.
/// </summary>
/// <remarks>
/// How a refund starts in practice. If the lookup is wrong the operator cannot find the sale at
/// all, so a correct refund becomes impossible at the counter — which is exactly when staff
/// improvise.
/// </remarks>
public sealed class SaleLookupTests
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

    private static async Task<(InMemoryLocalStore Store, Store Shop, List<StoredSale> Sales)> NewSalesAsync(
        params string[] productNames)
    {
        var local = new InMemoryLocalStore();
        var shop = NewStore();
        var checkout = new CheckoutRecordingService(local, new StubNumbers(), new FixedTerminal("TILL-1"));

        var sales = new List<StoredSale>();

        foreach (var name in productNames)
        {
            var cart = new Cart(shop.Id, shop.Currency, shop.TaxMode);
            cart.Add(ProductId.New(), $"600{Math.Abs(name.GetHashCode(StringComparison.Ordinal)) % 1_000_000_000:D9}",
                name, Vat15, new Money(15.00m, Zar), 1m);

            var completed = await checkout.RecordSaleAsync(
                cart,
                [new Tender(TenderType.Cash, new Money(15.00m, Zar), new Money(15.00m, Zar))],
                shop);

            sales.Add(completed.Stored);
        }

        return (local, shop, sales);
    }

    [Fact]
    public async Task A_sale_can_be_found_by_its_receipt_number()
    {
        var (local, _, sales) = await NewSalesAsync("Cola");

        var found = await local.FindSaleByNumberAsync(sales[0].Number);

        Assert.NotNull(found);
        Assert.Equal(sales[0].Id, found.Id);
    }

    [Fact]
    public async Task The_receipt_number_lookup_ignores_case_and_surrounding_space()
    {
        // The number is read off a printed slip and typed by hand, so an exact-match requirement
        // would cause needless failed lookups at the counter.
        var (local, _, sales) = await NewSalesAsync("Cola");

        Assert.NotNull(await local.FindSaleByNumberAsync(sales[0].Number.ToLowerInvariant()));
        Assert.NotNull(await local.FindSaleByNumberAsync($"  {sales[0].Number}  "));
    }

    [Fact]
    public async Task An_unknown_receipt_number_returns_nothing()
    {
        var (local, _, _) = await NewSalesAsync("Cola");

        Assert.Null(await local.FindSaleByNumberAsync("CT01-20260101-9999"));
    }

    [Fact]
    public async Task An_empty_receipt_number_returns_nothing_rather_than_matching()
    {
        // A blank lookup must not accidentally match the first sale in the store.
        var (local, _, _) = await NewSalesAsync("Cola", "Bread");

        Assert.Null(await local.FindSaleByNumberAsync(string.Empty));
        Assert.Null(await local.FindSaleByNumberAsync("   "));
    }

    [Fact]
    public async Task The_right_sale_is_found_among_several()
    {
        var (local, _, sales) = await NewSalesAsync("Cola", "Bread", "Milk");

        var found = await local.FindSaleByNumberAsync(sales[1].Number);

        Assert.NotNull(found);
        Assert.Equal("Bread", found.Lines[0].Name);
    }

    [Fact]
    public async Task Recent_sales_are_listed_most_recent_first()
    {
        // The fallback for a customer who has lost the receipt: the most likely sale to be
        // returned is the one just made.
        var (local, shop, sales) = await NewSalesAsync("Cola", "Bread", "Milk");

        var recent = await local.FindRecentSalesAsync(shop.Id.ToString(), StoredDate(sales[0]));

        Assert.Equal(3, recent.Count);
        Assert.Equal(sales[^1].Id, recent[0].Id);
    }

    [Fact]
    public async Task Recent_sales_are_capped()
    {
        var (local, shop, sales) = await NewSalesAsync("A", "B", "C", "D", "E");

        var recent = await local.FindRecentSalesAsync(shop.Id.ToString(), StoredDate(sales[0]), limit: 2);

        Assert.Equal(2, recent.Count);
    }

    [Fact]
    public async Task Recent_sales_are_scoped_to_the_store()
    {
        // A terminal must never offer another store's sales for refund.
        var (local, _, sales) = await NewSalesAsync("Cola");

        var other = await local.FindRecentSalesAsync(StoreId.New().ToString(), StoredDate(sales[0]));

        Assert.Empty(other);
    }

    /// <summary>Reads the business date back out of a stored sale.</summary>
    private static DateOnly StoredDate(StoredSale sale) =>
        DateOnly.ParseExact(sale.BusinessDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_refund_can_be_found_by_the_sale_it_refers_to()
    {
        // The refund screen needs this to work out what remains refundable before offering it.
        var (local, shop, sales) = await NewSalesAsync("Cola");

        var returns = new ReturnRecordingService(local, new FixedTerminal("TILL-1"));

        await returns.RecordReturnAsync(
            sales[0].Id, [(sales[0].Lines[0].Barcode, 1m)], ReturnReason.Faulty);

        var found = await local.GetReturnsForSaleAsync(sales[0].Id);

        Assert.Single(found);
        Assert.Equal(sales[0].Number, found[0].OriginalSaleNumber);
        Assert.Equal(shop.Id.ToString(), found[0].StoreId);
    }

    [Fact]
    public async Task A_partially_refunded_sale_still_looks_up_normally()
    {
        // The sale is untouched by a refund, so it must remain findable and reprintable.
        var (local, _, sales) = await NewSalesAsync("Cola");

        var returns = new ReturnRecordingService(local, new FixedTerminal("TILL-1"));
        await returns.RecordReturnAsync(sales[0].Id, [(sales[0].Lines[0].Barcode, 1m)], ReturnReason.Faulty);

        var found = await local.FindSaleByNumberAsync(sales[0].Number);

        Assert.NotNull(found);
        Assert.Equal(sales[0].Total, found.Total);
        Assert.Equal("Completed", found.Status);
    }
}
