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
/// Refund policy tests.
/// </summary>
/// <remarks>
/// The refund is the most abused operation at a till, and every abuse has the same shape:
/// getting back more than was paid. These tests attack that from each direction — more units
/// than were sold, repeated partial returns that sum past the sale, refunding a voided sale,
/// and paying cash for a card sale.
/// </remarks>
public sealed class RefundPolicyTests
{
    private const string Zar = "ZAR";
    private static readonly TaxRate Vat15 = new("VAT", 0.15m);

    private static async Task<Sale> NewSaleAsync(
        (string Name, decimal Price, decimal Qty)[] items,
        Tender[]? tenders = null,
        Discount? lineDiscount = null)
    {
        var storeId = StoreId.New();
        var cart = new Cart(storeId, Zar, TaxMode.Inclusive);

        foreach (var (name, price, qty) in items)
        {
            var line = cart.Add(
                ProductId.New(), Barcode(name), name, Vat15, new Money(price, Zar), qty);

            if (lineDiscount is { } discount)
            {
                line.LineDiscount = discount;
            }
        }

        var total = cart.CalculateTotals().Total;

        var checkout = new CheckoutService(new SequentialNumbers(), TimeProvider.System);

        return await checkout.CompleteSaleAsync(
            cart,
            tenders ?? [new Tender(TenderType.Cash, new Money(total, Zar), new Money(total, Zar))],
            "CT01");
    }

    /// <summary>A stable barcode per product name, so lines can be matched in assertions.</summary>
    private static string Barcode(string name) => $"600{Math.Abs(name.GetHashCode(StringComparison.Ordinal)) % 1_000_000_000:D9}";

    private static SalesReturn NewReturn(
        Sale sale,
        IReadOnlyList<ReturnLine> lines,
        TenderType tenderType = TenderType.Cash,
        ReturnReason reason = ReturnReason.ChangedMind,
        decimal? tenderAmount = null)
    {
        var total = lines.Sum(l => l.LineRefund);

        return new SalesReturn
        {
            Id = ReturnId.New(),
            StoreId = sale.StoreId,
            OriginalSaleId = sale.Id,
            OriginalSaleNumber = sale.Number,
            Number = "CT01-R-0001",
            CompletedAt = DateTimeOffset.UtcNow,
            BusinessDate = sale.BusinessDate,
            Currency = sale.Currency,
            Lines = lines,
            Refunds = [new Tender(tenderType, new Money(tenderAmount ?? total, sale.Currency))],
            Reason = reason,
        };
    }

    /// <summary>A return whose recorded lines are deliberately wrong, to test tolerance.</summary>
    private static SalesReturn CorruptedReturn(Sale sale, IReadOnlyList<ReturnLine> lines) => new()
    {
        Id = ReturnId.New(),
        StoreId = sale.StoreId,
        OriginalSaleId = sale.Id,
        OriginalSaleNumber = sale.Number,
        Number = "CT01-R-BAD",
        CompletedAt = DateTimeOffset.UtcNow,
        BusinessDate = sale.BusinessDate,
        Currency = sale.Currency,
        Lines = lines,
        Refunds = [new Tender(TenderType.Cash, new Money(0m, sale.Currency))],
        Reason = ReturnReason.ChangedMind,
    };

    private sealed class SequentialNumbers : ISaleNumberSource
    {
        private int _next;

        public Task<SaleNumber> NextAsync(
            StoreId storeId, string storeCode, DateOnly businessDate, CancellationToken ct = default) =>
            Task.FromResult(new SaleNumber(storeCode, businessDate, ++_next));
    }

    // ------------------------------------------------------------------------ allowance

    [Fact]
    public async Task A_fresh_sale_is_fully_refundable()
    {
        var sale = await NewSaleAsync([("Cola", 15.00m, 3m)]);

        var state = RefundPolicy.GetRefundableState(sale);

        Assert.Equal(3m, state.RefundableQuantity(sale.Lines[0]));
        Assert.False(state.IsFullyReturned);
    }

    [Fact]
    public async Task Returning_part_of_a_line_reduces_what_remains_refundable()
    {
        var sale = await NewSaleAsync([("Cola", 15.00m, 5m)]);

        var lines = RefundPolicy.BuildLines(sale, [(sale.Lines[0], 2m)], Zar);
        var previous = NewReturn(sale, lines);

        var state = RefundPolicy.GetRefundableState(sale, [previous]);

        Assert.Equal(3m, state.RefundableQuantity(sale.Lines[0]));
    }

    [Fact]
    public async Task A_fully_returned_sale_has_nothing_left()
    {
        var sale = await NewSaleAsync([("Cola", 15.00m, 2m)]);

        var lines = RefundPolicy.BuildLines(sale, [(sale.Lines[0], 2m)], Zar);
        var previous = NewReturn(sale, lines);

        var state = RefundPolicy.GetRefundableState(sale, [previous]);

        Assert.Equal(0m, state.RefundableQuantity(sale.Lines[0]));
        Assert.True(state.IsFullyReturned);
    }

    // ----------------------------------------------------------------------- rejection

    [Fact]
    public async Task Refunding_more_units_than_were_sold_is_rejected()
    {
        var sale = await NewSaleAsync([("Cola", 15.00m, 2m)]);

        var lines = RefundPolicy.BuildLines(sale, [(sale.Lines[0], 3m)], Zar);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RefundPolicy.Validate(sale, lines));

        Assert.Contains("Cannot refund 3", exception.Message);
        Assert.Contains("leaving 2", exception.Message);
    }

    [Fact]
    public async Task Repeated_partial_returns_cannot_sum_past_the_sale()
    {
        // The classic abuse: return two of five, three times over.
        var sale = await NewSaleAsync([("Cola", 15.00m, 5m)]);

        var first = NewReturn(sale, RefundPolicy.BuildLines(sale, [(sale.Lines[0], 2m)], Zar));
        var second = NewReturn(sale, RefundPolicy.BuildLines(sale, [(sale.Lines[0], 2m)], Zar));

        var third = RefundPolicy.BuildLines(sale, [(sale.Lines[0], 2m)], Zar);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RefundPolicy.Validate(sale, third, [first, second]));

        Assert.Contains("4", exception.Message);
        Assert.Contains("leaving 1", exception.Message);
    }

    [Fact]
    public async Task Two_proposed_lines_of_the_same_product_are_checked_together()
    {
        // Checked individually each would pass; their sum exceeds what was sold.
        var sale = await NewSaleAsync([("Cola", 15.00m, 3m)]);

        var split = RefundPolicy.BuildLines(sale, [(sale.Lines[0], 2m)], Zar);
        var proposal = new List<ReturnLine> { split[0], split[0] };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RefundPolicy.Validate(sale, proposal));

        Assert.Contains("Cannot refund 4", exception.Message);
    }

    [Fact]
    public async Task Refunding_a_voided_sale_is_rejected()
    {
        // A voided sale took no money, so refunding it would be pure loss.
        var sale = await NewSaleAsync([("Cola", 15.00m, 1m)]);
        sale.Status = SaleStatus.Voided;

        var lines = RefundPolicy.BuildLines(sale, [(sale.Lines[0], 1m)], Zar);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RefundPolicy.Validate(sale, lines));

        Assert.Contains("voided", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_empty_return_is_rejected()
    {
        var sale = await NewSaleAsync([("Cola", 15.00m, 1m)]);

        Assert.Throws<InvalidOperationException>(() => RefundPolicy.Validate(sale, []));
    }

    [Fact]
    public async Task A_zero_or_negative_quantity_is_rejected()
    {
        var sale = await NewSaleAsync([("Cola", 15.00m, 5m)]);

        var zero = new List<ReturnLine> { RefundPolicy.BuildLines(sale, [(sale.Lines[0], 1m)], Zar)[0] with { Quantity = 0m } };

        Assert.Throws<InvalidOperationException>(() => RefundPolicy.Validate(sale, zero));
    }

    [Fact]
    public async Task Refunds_cannot_total_more_than_the_sale_took()
    {
        var sale = await NewSaleAsync([("Cola", 115.00m, 1m)]);

        // A line whose unit refund is inflated beyond what was paid.
        var inflated = new List<ReturnLine>
        {
            new(sale.Lines[0].ProductId, sale.Lines[0].Barcode, sale.Lines[0].Name,
                1m, new Money(500.00m, Zar), Vat15, 65.22m),
        };

        var proposed = NewReturn(sale, inflated);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RefundPolicy.ValidateRefundAmount(sale, proposed));

        Assert.Contains("only 115.00 was paid", exception.Message);
    }

    [Fact]
    public async Task Refund_tenders_must_match_the_return_total()
    {
        var sale = await NewSaleAsync([("Cola", 15.00m, 1m)]);
        var lines = RefundPolicy.BuildLines(sale, [(sale.Lines[0], 1m)], Zar);

        var mismatched = NewReturn(sale, lines, tenderAmount: 99.99m);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RefundPolicy.ValidateRefundAmount(sale, mismatched));

        Assert.Contains("but the return is", exception.Message);
    }

    // ------------------------------------------------------------------------- pricing

    [Fact]
    public async Task A_line_is_refunded_at_the_price_actually_charged()
    {
        // Refunding the shelf price after a discount would let a customer profit by buying in
        // a sale and returning later.
        var sale = await NewSaleAsync(
            [("Cola", 100.00m, 1m)],
            lineDiscount: new Discount(DiscountKind.Percentage, 0.10m));

        var lines = RefundPolicy.BuildLines(sale, [(sale.Lines[0], 1m)], Zar);

        Assert.Equal(90.00m, lines[0].UnitRefund.Amount);
        Assert.Equal(90.00m, lines[0].LineRefund);
    }

    [Fact]
    public async Task Tax_is_apportioned_across_a_partial_return()
    {
        // The tax reversal must match what was declared, or the tax return will not reconcile.
        var sale = await NewSaleAsync([("Cola", 115.00m, 5m)]);

        var lines = RefundPolicy.BuildLines(sale, [(sale.Lines[0], 2m)], Zar);

        // Two of five units: two fifths of the tax charged on the line.
        var expectedTax = CartLine.Round(sale.Lines[0].TaxAmount / 5m * 2m);
        Assert.Equal(expectedTax, lines[0].TaxAmount);
    }

    [Fact]
    public async Task Refunding_every_line_returns_exactly_what_was_paid()
    {
        var sale = await NewSaleAsync([("Cola", 19.99m, 3m), ("Bread", 18.99m, 1m)]);
        var lines = RefundPolicy.BuildLines(sale, [(sale.Lines[0], 3m), (sale.Lines[1], 1m)], Zar);

        var total = lines.Sum(l => l.LineRefund);

        Assert.Equal(sale.Total, total);
    }

    [Fact]
    public async Task Net_and_tax_are_consistent_on_a_full_return()
    {
        var sale = await NewSaleAsync([("Cola", 115.00m, 1m)]);
        var lines = RefundPolicy.BuildLines(sale, [(sale.Lines[0], 1m)], Zar);

        var proposed = NewReturn(sale, lines);

        Assert.Equal(sale.Total, proposed.TotalRefund);
        Assert.Equal(sale.Tax.Tax, proposed.TaxReversed);
        Assert.Equal(sale.Total - sale.Tax.Tax, proposed.NetRefund);
    }

    [Fact]
    public async Task Weighted_goods_can_be_returned_by_fraction()
    {
        var sale = await NewSaleAsync([("Bananas", 22.99m, 1.5m)]);

        var lines = RefundPolicy.BuildLines(sale, [(sale.Lines[0], 0.5m)], Zar);

        RefundPolicy.Validate(sale, lines);
        Assert.Equal(0.5m, lines[0].Quantity);
    }

    // -------------------------------------------------------------------- tender warning

    [Fact]
    public async Task Refunding_cash_for_a_card_sale_is_flagged()
    {
        // A classic fraud route: pay by card, take the refund in cash. It must never happen
        // silently, even when it is allowed for a legitimate reason.
        var sale = await NewSaleAsync(
            [("Cola", 15.00m, 1m)],
            [new Tender(TenderType.ExternalCard, new Money(15.00m, Zar))]);

        var lines = RefundPolicy.BuildLines(sale, [(sale.Lines[0], 1m)], Zar);
        var proposed = NewReturn(sale, lines, TenderType.Cash);

        var warnings = RefundPolicy.FindTenderMismatches(sale, proposed);

        Assert.Single(warnings);
        Assert.Contains("not paid that way", warnings[0]);
    }

    [Fact]
    public async Task Refunding_by_the_original_method_produces_no_warning()
    {
        var sale = await NewSaleAsync(
            [("Cola", 15.00m, 1m)],
            [new Tender(TenderType.ExternalCard, new Money(15.00m, Zar))]);

        var lines = RefundPolicy.BuildLines(sale, [(sale.Lines[0], 1m)], Zar);
        var proposed = NewReturn(sale, lines, TenderType.ExternalCard);

        Assert.Empty(RefundPolicy.FindTenderMismatches(sale, proposed));
    }

    // ------------------------------------------------------------------ sale untouched

    [Fact]
    public async Task A_return_does_not_modify_the_original_sale()
    {
        // The sale is a historical fact. Rewriting it would destroy the audit trail and
        // unbalance the day's takings.
        var sale = await NewSaleAsync([("Cola", 15.00m, 2m)]);

        var beforeTotal = sale.Total;
        var beforeStatus = sale.Status;

        var lines = RefundPolicy.BuildLines(sale, [(sale.Lines[0], 2m)], Zar);
        _ = NewReturn(sale, lines);

        Assert.Equal(beforeTotal, sale.Total);
        Assert.Equal(beforeStatus, sale.Status);
        Assert.Equal(2m, sale.Lines[0].Quantity);
    }

    [Fact]
    public async Task A_return_records_the_original_sale_it_refers_to()
    {
        var sale = await NewSaleAsync([("Cola", 15.00m, 1m)]);
        var lines = RefundPolicy.BuildLines(sale, [(sale.Lines[0], 1m)], Zar);

        var proposed = NewReturn(sale, lines, reason: ReturnReason.Faulty);

        Assert.Equal(sale.Id, proposed.OriginalSaleId);
        Assert.Equal(sale.Number, proposed.OriginalSaleNumber);
        Assert.Equal(ReturnReason.Faulty, proposed.Reason);
    }

    [Fact]
    public async Task A_full_return_is_recognised_as_such()
    {
        var sale = await NewSaleAsync([("Cola", 15.00m, 3m)]);
        var lines = RefundPolicy.BuildLines(sale, [(sale.Lines[0], 3m)], Zar);

        var proposed = NewReturn(sale, lines);

        Assert.True(proposed.IsFullReturn(sale.TotalQuantity));
        Assert.Equal(3m, proposed.TotalQuantity);
    }

    [Fact]
    public async Task A_partial_return_is_not_a_full_return()
    {
        var sale = await NewSaleAsync([("Cola", 15.00m, 3m)]);
        var lines = RefundPolicy.BuildLines(sale, [(sale.Lines[0], 1m)], Zar);

        var proposed = NewReturn(sale, lines);

        Assert.False(proposed.IsFullReturn(sale.TotalQuantity));
    }

    [Fact]
    public async Task The_remainder_never_goes_negative_even_with_corrupt_history()
    {
        // A data anomaly must not produce a negative allowance, which would authorise an
        // arbitrary refund.
        var sale = await NewSaleAsync([("Cola", 15.00m, 2m)]);

        var impossible = NewReturn(sale, RefundPolicy.BuildLines(sale, [(sale.Lines[0], 2m)], Zar));

        // A recorded return claiming more units than the sale contained.
        var corrupted = CorruptedReturn(
            sale,
            [impossible.Lines[0] with { Quantity = 99m }]);

        var state = RefundPolicy.GetRefundableState(sale, [corrupted]);

        Assert.Equal(0m, state.RefundableQuantity(sale.Lines[0]));
    }
}
