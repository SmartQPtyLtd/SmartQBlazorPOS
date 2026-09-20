// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;

namespace Pos.Core.Tests;

/// <summary>
/// Tender validation is the last line of defence before a balance is written to the
/// ledger. An unvalidated tender is how a till ends up short at close.
/// </summary>
public sealed class SaleTests
{
    private const string Zar = "ZAR";

    private static Tender Cash(decimal amount, decimal? tendered = null, string currency = Zar) =>
        new(TenderType.Cash, new Money(amount, currency), tendered is { } t ? new Money(t, currency) : null);

    private static Tender Card(decimal amount, string currency = Zar) =>
        new(TenderType.ExternalCard, new Money(amount, currency));

    private sealed class SequentialSaleNumbers : ISaleNumberSource
    {
        private int _next = 1;

        public Task<SaleNumber> NextAsync(
            StoreId storeId, string storeCode, DateOnly businessDate, CancellationToken ct = default) =>
            Task.FromResult(new SaleNumber(storeCode, businessDate, _next++));
    }

    private static CheckoutService NewCheckout() => new(new SequentialSaleNumbers());

    private static Cart CartWith(params (decimal Price, decimal Qty)[] items)
    {
        var cart = new Cart(StoreId.New(), Zar, TaxMode.Inclusive);
        foreach (var (price, qty) in items)
        {
            cart.Add(ProductId.New(), "1234567890123", $"Item {price}", new TaxRate("VAT", 0.15m), new Money(price, Zar), qty);
        }

        return cart;
    }

    [Fact]
    public void Exact_cash_settles_the_sale()
    {
        Sale.ValidateTenders([Cash(100.00m, 100.00m)], 100.00m, Zar);
    }

    [Fact]
    public void Over_tendering_cash_is_allowed_because_change_is_returned()
    {
        Sale.ValidateTenders([Cash(100.00m, 150.00m)], 100.00m, Zar);
    }

    [Fact]
    public void Under_tendering_is_rejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Sale.ValidateTenders([Cash(60.00m, 60.00m)], 100.00m, Zar));

        Assert.Contains("but the sale total is", exception.Message);
    }

    [Fact]
    public void Cash_handed_over_must_cover_the_amount_applied()
    {
        // Guards the "R50 received against a R70 payment" mistake, which would show
        // as a phantom shortfall at the drawer count.
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Sale.ValidateTenders([Cash(100.00m, 50.00m)], 100.00m, Zar));

        Assert.Contains("less than the amount applied", exception.Message);
    }

    [Fact]
    public void Split_tender_that_balances_is_accepted()
    {
        Sale.ValidateTenders([Cash(30.00m, 50.00m), Card(70.00m)], 100.00m, Zar);
    }

    [Fact]
    public void Split_tender_that_does_not_balance_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Sale.ValidateTenders([Cash(30.00m, 30.00m), Card(60.00m)], 100.00m, Zar));
    }

    [Fact]
    public void A_sale_with_no_tender_is_rejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Sale.ValidateTenders([], 100.00m, Zar));

        Assert.Contains("at least one tender", exception.Message);
    }

    [Fact]
    public void A_negative_tender_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Sale.ValidateTenders([Card(-100.00m)], -100.00m, Zar));
    }

    [Fact]
    public void Change_is_derived_from_the_tendered_amount()
    {
        var tender = Cash(70.00m, 100.00m);

        Assert.Equal(30.00m, tender.ChangeDue(Zar).Amount);
    }

    [Fact]
    public void Change_is_zero_when_the_customer_tenders_exactly()
    {
        Assert.Equal(0m, Cash(70.00m, 70.00m).ChangeDue(Zar).Amount);
    }

    [Fact]
    public void Non_cash_tenders_never_produce_change()
    {
        // Handing money back against a card tender is a refund, not change.
        Assert.Equal(0m, Card(70.00m).ChangeDue(Zar).Amount);
    }

    [Fact]
    public async Task Completing_a_sale_freezes_the_prices_at_the_time_of_sale()
    {
        // A later catalogue edit must not alter a historic receipt.
        var checkout = NewCheckout();
        var cart = CartWith((100.00m, 1m));

        var sale = await checkout.CompleteSaleAsync(cart, [Cash(100.00m, 100.00m)], "CT01");

        Assert.Equal(100.00m, sale.Total);
        Assert.Equal(100.00m, sale.Lines[0].UnitPrice.Amount);
        Assert.Equal("CT01", sale.Number.StoreCode);
        Assert.Equal(1, sale.Number.Sequence);
    }

    [Fact]
    public async Task Sale_numbers_increment_per_store_and_date()
    {
        var checkout = NewCheckout();

        var first = await checkout.CompleteSaleAsync(CartWith((10.00m, 1m)), [Cash(10.00m)], "CT01");
        var second = await checkout.CompleteSaleAsync(CartWith((10.00m, 1m)), [Cash(10.00m)], "CT01");

        Assert.Equal(1, first.Number.Sequence);
        Assert.Equal(2, second.Number.Sequence);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Completing_an_empty_cart_is_rejected()
    {
        var checkout = NewCheckout();
        var cart = new Cart(StoreId.New(), Zar, TaxMode.Inclusive);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            checkout.CompleteSaleAsync(cart, [Cash(0m)], "CT01"));
    }

    [Fact]
    public async Task A_sale_records_the_tenders_and_reports_change()
    {
        var checkout = NewCheckout();
        var cart = CartWith((70.00m, 1m));

        var sale = await checkout.CompleteSaleAsync(cart, [Cash(70.00m, 100.00m)], "CT01");

        Assert.Equal(70.00m, sale.AmountTendered);
        Assert.Equal(30.00m, sale.ChangeGiven);
    }

    [Fact]
    public async Task Sale_line_discounts_include_the_allocated_order_discount()
    {
        // The receipt must show what the customer actually saved on each line, which
        // means the line-level and order-level discounts have to be combined.
        var checkout = NewCheckout();
        var cart = CartWith((100.00m, 1m));
        cart.OrderDiscount = new Discount(DiscountKind.FixedAmount, 20.00m);

        var sale = await checkout.CompleteSaleAsync(cart, [Cash(80.00m)], "CT01");

        Assert.Equal(20.00m, sale.Lines[0].DiscountAmount);
        Assert.Equal(80.00m, sale.Lines[0].TaxableAmount);
        Assert.Equal(80.00m, sale.Total);
    }

    [Fact]
    public async Task A_completed_sale_keeps_per_line_tax_consistent_with_the_total()
    {
        var checkout = NewCheckout();
        var cart = CartWith((19.99m, 3m), (4.50m, 7m), (123.45m, 1m));
        cart.OrderDiscount = new Discount(DiscountKind.Percentage, 0.05m);

        var owed = cart.CalculateTotals().Total;
        var sale = await checkout.CompleteSaleAsync(cart, [Card(owed)], "CT01");

        Assert.Equal(sale.Tax.Tax, sale.Lines.Sum(l => l.TaxAmount));
        Assert.Equal(sale.Total, sale.Lines.Sum(l => l.GrossAmount));
    }

    [Fact]
    public void Voiding_keeps_the_sale_on_record()
    {
        // Deleting a sale destroys the audit trail; a void is a state change, not a removal.
        var sale = new Sale
        {
            Id = SaleId.New(),
            StoreId = StoreId.New(),
            Number = new SaleNumber("CT01", new DateOnly(2026, 3, 25), 1),
            CompletedAt = DateTimeOffset.UtcNow,
            BusinessDate = new DateOnly(2026, 3, 25),
            Currency = Zar,
            TaxMode = TaxMode.Inclusive,
            Lines = [],
            Tenders = [],
            Tax = new TaxCalculation(TaxMode.Inclusive, 0m, 0m, 0m, []),
            Subtotal = 0m,
            TotalDiscount = 0m,
            Total = 0m,
        };

        sale.Status = SaleStatus.Voided;
        sale.VoidReason = "Customer changed their mind";

        Assert.True(sale.IsVoided);
        Assert.Equal("Customer changed their mind", sale.VoidReason);
    }

    [Fact]
    public void Sale_ids_sort_chronologically()
    {
        // UUIDv7 is time-ordered, so receipts and reports come out in the order
        // things actually happened without a separate sequence column.
        var first = SaleId.New().Value;
        Thread.Sleep(5);
        var second = SaleId.New().Value;

        Assert.True(first.CompareTo(second) < 0);
    }
}
