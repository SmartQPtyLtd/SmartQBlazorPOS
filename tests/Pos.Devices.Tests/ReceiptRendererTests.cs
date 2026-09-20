// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Globalization;
using Pos.Core.Domain;
using Pos.Devices.EscPos;

namespace Pos.Devices.Tests;

/// <summary>
/// Receipt rendering tests. These assert what the customer actually reads on the
/// paper, including column alignment and the tax presentation rules that differ
/// between inclusive and exclusive stores.
/// </summary>
public sealed class ReceiptRendererTests
{
    private const string Zar = "ZAR";
    private static readonly TaxRate Vat15 = new("VAT", 0.15m);
    private static readonly TaxRate ZeroRated = TaxRate.Zero("VAT-Zero");

    private static Store NewStore(TaxMode mode = TaxMode.Inclusive, int columns = 48) => new()
    {
        Id = StoreId.New(),
        Name = "CORNER STORE",
        Code = "CT01",
        Currency = Zar,
        TaxMode = mode,
        DefaultTaxRate = Vat15,
        AddressLines = ["12 Main Road", "Cape Town"],
        TaxRegistrationNumber = "4123456789",
        Phone = "021 555 0100",
        ReceiptFooter = "Thank you for your business!\nReturns within 30 days",
        ReceiptColumns = columns,
    };

    private static async Task<Sale> NewSaleAsync(
        Store store,
        (string Name, decimal Price, decimal Qty)[] items,
        Tender[]? tenders = null,
        Discount? orderDiscount = null,
        string? cashier = null)
    {
        var cart = new Cart(store.Id, store.Currency, store.TaxMode);

        foreach (var (name, price, qty) in items)
        {
            cart.Add(ProductId.New(), "1234567890123", name, Vat15, new Money(price, store.Currency), qty);
        }

        cart.OrderDiscount = orderDiscount;

        var total = cart.CalculateTotals().Total;
        var checkout = new CheckoutService(new FixedSaleNumbers(), TimeProvider.System);

        return await checkout.CompleteSaleAsync(
            cart,
            tenders ?? [new Tender(TenderType.Cash, new Money(total, store.Currency), new Money(total, store.Currency))],
            store.Code,
            cashier);
    }

    private static EscPosParser Render(Store store, Sale sale, PosDocumentType type = PosDocumentType.SalesReceipt, bool openDrawer = false)
    {
        var renderer = new ReceiptRenderer();
        var bytes = renderer.Render(new ReceiptDocument(store, sale, type), openDrawer);
        return EscPosParser.Parse(bytes);
    }

    private sealed class FixedSaleNumbers : ISaleNumberSource
    {
        public Task<SaleNumber> NextAsync(StoreId storeId, string storeCode, DateOnly businessDate, CancellationToken ct = default) =>
            Task.FromResult(new SaleNumber(storeCode, businessDate, 42));
    }

    // ------------------------------------------------------------------ Structure

    [Fact]
    public async Task A_receipt_starts_by_initialising_the_printer_and_ends_with_a_cut()
    {
        // Initialise matters: without it a previous job that left the printer in
        // double-height mode would corrupt this receipt.
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Cola", 15.00m, 1m)]);

        var parsed = Render(store, sale);

        Assert.Equal("Initialise", parsed.Commands[0]);
        Assert.True(parsed.Cut);
    }

    [Fact]
    public async Task The_store_header_is_centred_and_enlarged()
    {
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Cola", 15.00m, 1m)]);

        var parsed = Render(store, sale);
        var text = string.Join("\n", parsed.Lines);

        Assert.Contains("CORNER STORE", text);
        Assert.Contains("12 Main Road", text);
        Assert.Contains("Cape Town", text);
        Assert.Contains("Tel: 021 555 0100", text);
        Assert.Contains("VAT No: 4123456789", text);

        // Centre alignment precedes the name, and double-size text is applied.
        Assert.Contains("Align1", parsed.Commands);
        Assert.Contains("TextSize11", parsed.Commands);
    }

    [Fact]
    public async Task The_sale_number_date_and_cashier_are_printed()
    {
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Cola", 15.00m, 1m)], cashier: "Thandi");

        var text = string.Join("\n", Render(store, sale).Lines);

        Assert.Contains("CT01-", text);
        Assert.Contains("Thandi", text);
    }

    [Fact]
    public async Task Every_printed_line_respects_the_paper_width()
    {
        // A line longer than the paper width wraps mid-number, which silently
        // corrupts the amount column on a real receipt.
        var store = NewStore(columns: 48);
        var sale = await NewSaleAsync(
            store,
            [("A very long product name that will not fit on one line", 19.99m, 2m)]);

        var parsed = Render(store, sale);

        foreach (var line in parsed.Lines)
        {
            Assert.True(
                line.Length <= 48,
                $"Line exceeds paper width ({line.Length} > 48): \"{line}\"");
        }
    }

    [Fact]
    public async Task A_58mm_printer_renders_within_32_columns()
    {
        var store = NewStore(columns: 32);
        var sale = await NewSaleAsync(store, [("Cola", 15.00m, 1m)]);

        var parsed = Render(store, sale);

        Assert.All(parsed.Lines, line => Assert.True(line.Length <= 32, $"Too wide: \"{line}\""));
    }

    // ----------------------------------------------------------------- Sale lines

    [Fact]
    public async Task A_single_unit_line_shows_quantity_name_and_amount()
    {
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Cola 500ml", 15.00m, 1m)]);

        var text = string.Join("\n", Render(store, sale).Lines);

        Assert.Contains("1 x Cola 500ml", text);
        Assert.Contains("15.00", text);
    }

    [Fact]
    public async Task A_multi_unit_line_shows_the_unit_price_on_its_own_line()
    {
        // Without the unit price a customer cannot verify 3 x 15.00 = 45.00.
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Cola", 15.00m, 3m)]);

        var text = string.Join("\n", Render(store, sale).Lines);

        Assert.Contains("3 x Cola", text);
        Assert.Contains("@ 15.00", text);
    }

    [Fact]
    public async Task A_weighted_line_prints_its_fractional_quantity()
    {
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Bananas", 129.99m, 0.734m)]);

        var text = string.Join("\n", Render(store, sale).Lines);

        Assert.Contains("0.734 x Bananas", text);
    }

    [Fact]
    public async Task A_discounted_line_shows_the_discount()
    {
        var store = NewStore();
        var cart = new Cart(store.Id, store.Currency, store.TaxMode);
        var line = cart.Add(ProductId.New(), "123", "Cola", Vat15, new Money(100.00m, Zar));
        line.LineDiscount = new Discount(DiscountKind.Percentage, 0.10m, "Staff");

        var checkout = new CheckoutService(new FixedSaleNumbers(), TimeProvider.System);
        var sale = await checkout.CompleteSaleAsync(
            cart,
            [new Tender(TenderType.Cash, new Money(90.00m, Zar), new Money(100.00m, Zar))],
            store.Code);

        var text = string.Join("\n", Render(store, sale).Lines);

        Assert.Contains("Discount -10.00", text);
        Assert.Contains("You saved", text);
    }

    // --------------------------------------------------------------------- Totals

    [Fact]
    public async Task An_inclusive_store_shows_one_total_with_tax_noted_as_included()
    {
        // Printing a separate "Subtotal" and then adding inclusive tax would
        // double-count and overstate what the customer paid.
        var store = NewStore(TaxMode.Inclusive);
        var sale = await NewSaleAsync(store, [("Cola", 115.00m, 1m)]);

        var text = string.Join("\n", Render(store, sale).Lines);

        Assert.Contains("TOTAL", text);
        Assert.Contains("115.00", text);
        Assert.Contains("includes 15.00 tax", text);
        Assert.DoesNotContain("Subtotal", text);
    }

    [Fact]
    public async Task An_exclusive_store_shows_subtotal_then_tax_then_total()
    {
        var store = NewStore(TaxMode.Exclusive);
        var sale = await NewSaleAsync(store, [("Widget", 100.00m, 1m)]);

        var text = string.Join("\n", Render(store, sale).Lines);

        Assert.Contains("Subtotal", text);
        Assert.Contains("VAT 15%", text);
        Assert.Contains("TOTAL", text);
        Assert.Contains("115.00", text);
    }

    [Fact]
    public async Task An_exclusive_receipt_total_equals_net_plus_tax()
    {
        // The printed figures must be internally consistent; this is the arithmetic
        // a customer or auditor can check by hand.
        var store = NewStore(TaxMode.Exclusive);
        var sale = await NewSaleAsync(store, [("Widget", 19.99m, 3m), ("Gadget", 4.50m, 7m)]);

        var text = string.Join("\n", Render(store, sale).Lines);

        Assert.Contains(sale.Tax.Net.ToString("0.00", CultureInfo.InvariantCulture), text);
        Assert.Contains(sale.Tax.Tax.ToString("0.00", CultureInfo.InvariantCulture), text);
        Assert.Contains(sale.Total.ToString("0.00", CultureInfo.InvariantCulture), text);
        Assert.Equal(sale.Tax.Gross, sale.Total);
    }

    [Fact]
    public async Task A_mixed_rate_basket_prints_a_tax_summary()
    {
        // A VAT return needs the zero-rated portion shown separately.
        var store = NewStore(TaxMode.Inclusive);
        var cart = new Cart(store.Id, store.Currency, store.TaxMode);
        cart.Add(ProductId.New(), "111", "Wine", Vat15, new Money(115.00m, Zar));
        cart.Add(ProductId.New(), "222", "Bread", ZeroRated, new Money(20.00m, Zar));

        var checkout = new CheckoutService(new FixedSaleNumbers(), TimeProvider.System);
        var sale = await checkout.CompleteSaleAsync(
            cart,
            [new Tender(TenderType.Cash, new Money(135.00m, Zar), new Money(150.00m, Zar))],
            store.Code);

        var text = string.Join("\n", Render(store, sale).Lines);

        Assert.Contains("Tax summary", text);
        Assert.Contains("Zero rated", text);
    }

    [Fact]
    public async Task A_single_rate_basket_omits_the_redundant_tax_summary()
    {
        var store = NewStore(TaxMode.Inclusive);
        var sale = await NewSaleAsync(store, [("Cola", 115.00m, 1m)]);

        var text = string.Join("\n", Render(store, sale).Lines);

        Assert.DoesNotContain("Tax summary", text);
    }

    // -------------------------------------------------------------------- Tenders

    [Fact]
    public async Task Cash_change_is_printed_when_the_customer_overpays()
    {
        var store = NewStore();
        var sale = await NewSaleAsync(
            store,
            [("Cola", 70.00m, 1m)],
            [new Tender(TenderType.Cash, new Money(70.00m, Zar), new Money(100.00m, Zar))]);

        var text = string.Join("\n", Render(store, sale).Lines);

        Assert.Contains("Cash", text);
        Assert.Contains("CHANGE", text);
        Assert.Contains("30.00", text);
    }

    [Fact]
    public async Task Exact_payment_prints_no_change_line()
    {
        var store = NewStore();
        var sale = await NewSaleAsync(
            store,
            [("Cola", 70.00m, 1m)],
            [new Tender(TenderType.ExternalCard, new Money(70.00m, Zar))]);

        var text = string.Join("\n", Render(store, sale).Lines);

        Assert.Contains("Card", text);
        Assert.DoesNotContain("CHANGE", text);
    }

    [Fact]
    public async Task A_split_tender_lists_every_payment()
    {
        var store = NewStore();
        var sale = await NewSaleAsync(
            store,
            [("Cola", 100.00m, 1m)],
            [
                new Tender(TenderType.Cash, new Money(30.00m, Zar), new Money(50.00m, Zar)),
                new Tender(TenderType.ExternalCard, new Money(70.00m, Zar)),
            ]);

        var text = string.Join("\n", Render(store, sale).Lines);

        Assert.Contains("Cash", text);
        Assert.Contains("Card", text);
        Assert.Contains("CHANGE", text); // 20.00 back from the 50.00 handed over
    }

    [Fact]
    public async Task The_item_count_is_printed()
    {
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Cola", 15.00m, 3m)]);

        var text = string.Join("\n", Render(store, sale).Lines);

        Assert.Contains("3 items", text);
    }

    [Fact]
    public async Task A_single_item_receipt_uses_the_singular_form()
    {
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Cola", 15.00m, 1m)]);

        var text = string.Join("\n", Render(store, sale).Lines);

        Assert.Contains("1 item", text);
        Assert.DoesNotContain("1 items", text);
    }

    // ------------------------------------------------------------ Drawer and copy

    [Fact]
    public async Task A_cash_sale_opens_the_drawer_when_requested()
    {
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Cola", 15.00m, 1m)]);

        Assert.True(Render(store, sale, openDrawer: true).OpenedDrawer);
    }

    [Fact]
    public async Task The_drawer_is_not_opened_unless_requested()
    {
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Cola", 15.00m, 1m)]);

        Assert.False(Render(store, sale).OpenedDrawer);
    }

    [Fact]
    public async Task A_kitchen_ticket_never_opens_the_drawer()
    {
        // A kitchen printer has no drawer attached; the pulse would be meaningless and
        // on some hardware produces a spurious error.
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Cola", 15.00m, 1m)]);

        var parsed = Render(store, sale, PosDocumentType.KitchenTicket, openDrawer: true);

        Assert.False(parsed.OpenedDrawer);
        Assert.Contains("KITCHEN", string.Join("\n", parsed.Lines));
    }

    [Fact]
    public async Task A_copy_is_clearly_marked_as_a_duplicate()
    {
        // An unmarked duplicate could be presented twice for the same refund.
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Cola", 15.00m, 1m)]);

        var renderer = new ReceiptRenderer();
        var bytes = renderer.Render(new ReceiptDocument(store, sale, PosDocumentType.SalesReceiptCopy));
        var text = string.Join("\n", EscPosParser.Parse(bytes).Lines);

        Assert.Contains("*** COPY ***", text);
    }

    [Fact]
    public async Task A_voided_sale_is_marked_void()
    {
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Cola", 15.00m, 1m)]);
        sale.Status = SaleStatus.Voided;

        var text = string.Join("\n", Render(store, sale).Lines);

        Assert.Contains("*** VOIDED ***", text);
    }

    // ------------------------------------------------------------------------ QR

    [Fact]
    public async Task The_receipt_carries_a_scannable_QR_code_for_returns()
    {
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Cola", 15.00m, 1m)]);

        var parsed = Render(store, sale);

        var payload = Assert.Single(parsed.QrPayloads);
        Assert.Contains("POS|CT01|", payload);
        Assert.Contains(sale.Id.Value.ToString("N"), payload);
    }

    [Fact]
    public async Task The_QR_code_is_omitted_when_disabled()
    {
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Cola", 15.00m, 1m)]);

        var renderer = new ReceiptRenderer(new ReceiptOptions { PrintReturnQrCode = false });
        var parsed = EscPosParser.Parse(renderer.Render(new ReceiptDocument(store, sale)));

        Assert.Empty(parsed.QrPayloads);
    }

    [Fact]
    public async Task A_kitchen_ticket_carries_no_return_QR_code()
    {
        // The return code belongs on the customer's copy, not in the kitchen.
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Cola", 15.00m, 1m)]);

        var parsed = Render(store, sale, PosDocumentType.KitchenTicket);

        Assert.Empty(parsed.QrPayloads);
    }

    [Fact]
    public async Task A_kitchen_ticket_omits_prices_tax_and_payment()
    {
        // A cook does not need them, and printing them wastes paper and clutters the ticket.
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Cola", 15.00m, 2m)]);

        var text = string.Join("\n", Render(store, sale, PosDocumentType.KitchenTicket).Lines);

        Assert.DoesNotContain("TOTAL", text);
        Assert.DoesNotContain("VAT", text);
        Assert.DoesNotContain("Cash", text);
        Assert.DoesNotContain("CHANGE", text);
        Assert.DoesNotContain("15.00", text);
    }

    [Fact]
    public async Task A_kitchen_ticket_shows_the_quantity_and_the_item()
    {
        // Quantity is the element most often misread at a glance, so it must be present.
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Cola", 15.00m, 3m)]);

        var text = string.Join("\n", Render(store, sale, PosDocumentType.KitchenTicket).Lines);

        Assert.Contains("3 x Cola", text);
        Assert.Contains("KITCHEN", text);
    }

    [Fact]
    public async Task A_kitchen_ticket_uses_a_partial_cut_so_it_stays_on_the_roll()
    {
        // A kitchen printer is mounted vertically; a full cut would drop the ticket on the floor.
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Cola", 15.00m, 1m)]);

        var renderer = new ReceiptRenderer();
        var bytes = renderer.Render(new ReceiptDocument(store, sale, PosDocumentType.KitchenTicket));

        Assert.Equal(1, FindCutMode(bytes));
    }

    [Fact]
    public async Task A_receipt_uses_a_full_cut()
    {
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Cola", 15.00m, 1m)]);

        var renderer = new ReceiptRenderer();
        var bytes = renderer.Render(new ReceiptDocument(store, sale));

        Assert.Equal(0, FindCutMode(bytes));
    }

    /// <summary>
    /// Finds the mode byte of the paper-cut command, <c>GS V m</c>.
    /// </summary>
    /// <remarks>
    /// Searching for a bare 0x56 byte would match the ASCII letter 'V' in any product or
    /// store name, so the whole three-byte command must be located.
    /// </remarks>
    private static int FindCutMode(byte[] bytes)
    {
        for (var i = 0; i + 2 < bytes.Length; i++)
        {
            if (bytes[i] == 0x1D && bytes[i + 1] == 0x56)
            {
                return bytes[i + 2];
            }
        }

        throw new InvalidOperationException("No paper-cut command was found in the stream.");
    }

    [Fact]
    public async Task A_kitchen_ticket_can_be_routed_to_a_named_station()
    {
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Steak", 120.00m, 1m)]);

        var bytes = ReceiptRenderer.RenderKitchenTicket(
            new ReceiptDocument(store, sale, PosDocumentType.KitchenTicket),
            station: "GRILL");

        var text = string.Join("\n", EscPosParser.Parse(bytes).Lines);
        Assert.Contains("GRILL", text);
    }

    [Fact]
    public async Task A_kitchen_ticket_prints_line_notes_so_modifiers_are_not_lost()
    {
        // "No onions" is the difference between the right dish and a complaint.
        var store = NewStore();
        var cart = new Cart(store.Id, store.Currency, store.TaxMode);
        cart.Add(ProductId.New(), "123", "Burger", Vat15, new Money(85.00m, Zar), 1m, note: "No onions");

        var checkout = new CheckoutService(new FixedSaleNumbers(), TimeProvider.System);
        var sale = await checkout.CompleteSaleAsync(
            cart,
            [new Tender(TenderType.Cash, new Money(85.00m, Zar), new Money(85.00m, Zar))],
            store.Code);

        var text = string.Join("\n", Render(store, sale, PosDocumentType.KitchenTicket).Lines);

        Assert.Contains("No onions", text);
    }

    [Fact]
    public async Task A_reprinted_kitchen_ticket_is_marked_as_a_reprint()
    {
        // Otherwise the kitchen cooks the order twice.
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Cola", 15.00m, 1m)]);

        var renderer = new ReceiptRenderer();
        var bytes = renderer.Render(new ReceiptDocument(
            store, sale, PosDocumentType.KitchenTicket, CopyIndex: 1));

        var text = string.Join("\n", EscPosParser.Parse(bytes).Lines);
        Assert.Contains("REPRINT", text);
    }

    // -------------------------------------------------------------------- Footer

    [Fact]
    public async Task The_footer_prints_multiline_text()
    {
        var store = NewStore();
        var sale = await NewSaleAsync(store, [("Cola", 15.00m, 1m)]);

        var text = string.Join("\n", Render(store, sale).Lines);

        Assert.Contains("Thank you for your business!", text);
        Assert.Contains("Returns within 30 days", text);
    }

    [Fact]
    public async Task A_receipt_of_a_large_basket_renders_without_error()
    {
        // Volume sanity check: a big basket must not exceed any printer buffer or
        // produce a malformed command stream.
        var store = NewStore();
        var items = Enumerable.Range(1, 60)
            .Select(i => ($"Product {i}", 1.00m + i, (decimal)(i % 5 + 1)))
            .ToArray();

        var sale = await NewSaleAsync(store, items);
        var parsed = Render(store, sale);

        Assert.True(parsed.Cut);
        Assert.Contains(parsed.Lines, l => l.Contains("TOTAL", StringComparison.Ordinal));

        // 60 distinct products must survive as 60 lines, not be merged or dropped.
        var lineCount = parsed.Lines.Count(l => l.Contains(" x Product ", StringComparison.Ordinal));
        Assert.Equal(60, lineCount);
    }
}
