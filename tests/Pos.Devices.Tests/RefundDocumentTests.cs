// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;
using Pos.Devices.EscPos;

namespace Pos.Devices.Tests;

/// <summary>
/// Refund document tests.
/// </summary>
/// <remarks>
/// A refund slip that could be mistaken for a receipt is a fraud tool: it can be presented
/// elsewhere as proof of purchase, or used to walk goods out of a shop. Most of what follows
/// pins the ways the document makes itself unmistakable.
/// </remarks>
public sealed class RefundDocumentTests
{
    private const string Zar = "ZAR";
    private static readonly TaxRate Vat15 = new("VAT", 0.15m);

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
    /// Builds a sale shaped exactly as <c>CheckoutService</c> would produce it.
    /// </summary>
    /// <remarks>
    /// In particular <c>TaxableAmount</c> is the <b>line</b> total, not the unit price. Getting
    /// that wrong here would make the refund arithmetic look broken when it is the fixture that
    /// is wrong.
    /// </remarks>
    private static Sale NewSale(decimal price = 115.00m, decimal quantity = 1m, string name = "Cola 500ml")
    {
        var storeId = StoreId.New();
        var lineTotal = CartLine.Round(price * quantity);
        var tax = CartLine.Round(lineTotal / 1.15m * 0.15m);

        return new Sale
        {
            Id = SaleId.New(),
            StoreId = storeId,
            Number = new SaleNumber("CT01", new DateOnly(2026, 3, 25), 7),
            CompletedAt = new DateTimeOffset(2026, 3, 25, 14, 30, 0, TimeSpan.FromHours(2)),
            BusinessDate = new DateOnly(2026, 3, 25),
            Currency = Zar,
            TaxMode = TaxMode.Inclusive,
            Lines =
            [
                new SaleLine(
                    ProductId.New(), "6001000000017", name, quantity,
                    new Money(price, Zar), Vat15, 0m, lineTotal, tax),
            ],
            Tenders = [new Tender(TenderType.Cash, new Money(lineTotal, Zar))],
            Tax = new TaxCalculation(TaxMode.Inclusive, lineTotal - tax, tax, lineTotal, []),
            Subtotal = lineTotal,
            TotalDiscount = 0m,
            Total = lineTotal,
        };
    }

    private static SalesReturn NewReturn(
        Sale sale,
        decimal quantity = 1m,
        ReturnReason reason = ReturnReason.Faulty,
        string? note = null)
    {
        var lines = RefundPolicy.BuildLines(sale, [(sale.Lines[0], quantity)], Zar);
        var total = lines.Sum(l => l.LineRefund);

        return new SalesReturn
        {
            Id = ReturnId.New(),
            StoreId = sale.StoreId,
            OriginalSaleId = sale.Id,
            OriginalSaleNumber = sale.Number,
            Number = "CT01-R-0003",
            CompletedAt = new DateTimeOffset(2026, 3, 25, 15, 0, 0, TimeSpan.FromHours(2)),
            BusinessDate = sale.BusinessDate,
            Currency = Zar,
            Lines = lines,
            Refunds = [new Tender(TenderType.Cash, new Money(total, Zar))],
            Reason = reason,
            Note = note,
        };
    }

    private static string Render(Sale sale, SalesReturn salesReturn, bool isCopy = false)
    {
        var bytes = ReceiptRenderer.RenderRefund(NewStore(), salesReturn, "Thandi", isCopy);
        return string.Join("\n", EscPosParser.Parse(bytes).Lines);
    }

    [Fact]
    public void A_refund_slip_is_prominently_marked_as_a_refund()
    {
        var sale = NewSale();
        var text = Render(sale, NewReturn(sale));

        Assert.Contains("REFUND", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refund_slip_states_that_it_is_not_a_receipt()
    {
        // If the slip is separated from the rest of the paperwork, this line is what stops it
        // being presented as proof of purchase.
        var sale = NewSale();
        var text = Render(sale, NewReturn(sale));

        Assert.Contains("NOT A RECEIPT", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_refund_banner_is_the_largest_element()
    {
        var sale = NewSale();
        var bytes = ReceiptRenderer.RenderRefund(NewStore(), NewReturn(sale));

        var parsed = EscPosParser.Parse(bytes);

        // Triple-size text is applied before the banner and reset after it.
        Assert.Contains("TextSize22", parsed.Commands);
    }

    [Fact]
    public void The_original_sale_is_referenced_so_the_return_can_be_traced()
    {
        var sale = NewSale();
        var text = Render(sale, NewReturn(sale));

        Assert.Contains("CT01-20260325-0007", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_refund_total_is_printed()
    {
        var sale = NewSale();
        var text = Render(sale, NewReturn(sale));

        Assert.Contains("REFUND TOTAL", text, StringComparison.Ordinal);
        Assert.Contains("115.00", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tax_being_reversed_is_shown()
    {
        // The reversal must appear, because it reduces what the shop owes onward and has to be
        // reconcilable against the tax return.
        var sale = NewSale();
        var text = Render(sale, NewReturn(sale));

        Assert.Contains("tax reversed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void How_the_money_went_back_is_printed()
    {
        var sale = NewSale();
        var text = Render(sale, NewReturn(sale));

        Assert.Contains("Cash", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_return_reason_is_printed_in_words()
    {
        // The reason is what makes a returns report actionable, so it has to be on the document.
        var sale = NewSale();
        var text = Render(sale, NewReturn(sale, reason: ReturnReason.Faulty));

        Assert.Contains("Faulty", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ReturnReason.ChangedMind, "Changed mind")]
    [InlineData(ReturnReason.WrongItem, "Wrong item")]
    [InlineData(ReturnReason.NotAsDescribed, "Not as described")]
    [InlineData(ReturnReason.Damaged, "Damaged")]
    [InlineData(ReturnReason.Expired, "Out of date")]
    [InlineData(ReturnReason.Goodwill, "Goodwill")]
    public void Every_reason_has_a_readable_description(ReturnReason reason, string expected)
    {
        var sale = NewSale();
        var text = Render(sale, NewReturn(sale, reason: reason));

        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_note_is_printed_when_present()
    {
        var sale = NewSale();
        var text = Render(sale, NewReturn(sale, note: "Seal was broken"));

        Assert.Contains("Seal was broken", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_signature_line_is_printed()
    {
        // The shop's evidence that the money was handed to a person.
        var sale = NewSale();
        var text = Render(sale, NewReturn(sale));

        Assert.Contains("Customer signature", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_copy_is_marked_as_a_duplicate()
    {
        // An unmarked duplicate could be presented twice for the same refund.
        var sale = NewSale();
        var text = Render(sale, NewReturn(sale), isCopy: true);

        Assert.Contains("*** COPY ***", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_partial_return_shows_only_what_came_back()
    {
        // Five units at 115.00 each is a 575.00 sale; returning two refunds 230.00, not the
        // whole sale.
        var sale = NewSale(price: 115.00m, quantity: 5m);
        var text = Render(sale, NewReturn(sale, quantity: 2m));

        Assert.Contains("2 x Cola 500ml", text, StringComparison.Ordinal);
        Assert.Contains("230.00", text, StringComparison.Ordinal);

        // The full sale value must not appear anywhere on a partial refund slip.
        Assert.DoesNotContain("575.00", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_refund_does_not_open_the_cash_drawer_unless_asked()
    {
        // The drawer kick is the caller's decision; a refund slip alone must not pop it.
        var sale = NewSale();
        var parsed = EscPosParser.Parse(ReceiptRenderer.RenderRefund(NewStore(), NewReturn(sale)));

        Assert.False(parsed.OpenedDrawer);
    }

    [Fact]
    public void A_refund_slip_is_fully_cut_so_it_can_be_handed_over()
    {
        var sale = NewSale();
        var bytes = ReceiptRenderer.RenderRefund(NewStore(), NewReturn(sale));

        for (var i = 0; i + 2 < bytes.Length; i++)
        {
            if (bytes[i] == 0x1D && bytes[i + 1] == 0x56)
            {
                Assert.Equal(0x00, bytes[i + 2]);
                return;
            }
        }

        Assert.Fail("No cut command was found.");
    }

    [Fact]
    public void A_long_product_name_does_not_push_the_amount_out_of_place()
    {
        var store = NewStore();

        var wide = NewSale(name: "A very long product name that will not fit on one line");

        var parsed = EscPosParser.Parse(ReceiptRenderer.RenderRefund(store, NewReturn(wide)));

        Assert.All(parsed.Lines, line => Assert.True(
            line.Length <= store.ReceiptColumns,
            $"Line exceeds paper width ({line.Length}): \"{line}\""));
    }

    [Fact]
    public void A_58mm_printer_renders_the_refund_within_32_columns()
    {
        var store = NewStore();
        store.ReceiptColumns = 32;

        var sale = NewSale();
        var parsed = EscPosParser.Parse(ReceiptRenderer.RenderRefund(store, NewReturn(sale)));

        Assert.All(parsed.Lines, line => Assert.True(
            line.Length <= 32,
            $"Too wide: \"{line}\""));
    }

    [Fact]
    public void The_cashier_who_processed_the_refund_is_named()
    {
        // Refunds are the most abuse-prone operation, so accountability is printed.
        var sale = NewSale();
        var text = Render(sale, NewReturn(sale));

        Assert.Contains("Thandi", text, StringComparison.Ordinal);
    }
}
