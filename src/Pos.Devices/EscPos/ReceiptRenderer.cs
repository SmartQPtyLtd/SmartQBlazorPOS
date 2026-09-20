// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Globalization;
using System.Text;
using Pos.Core.Domain;

namespace Pos.Devices.EscPos;

/// <summary>
/// Renders a completed sale into an ESC/POS document.
/// </summary>
/// <remarks>
/// <para>
/// Pure C# with no browser dependency, so receipts can be asserted byte for byte in
/// unit tests. Every receipt a customer receives passes through here.
/// </para>
/// <para>
/// All typography is deliberately restricted to what CP437 can represent: the rule
/// characters <c>=</c>, <c>-</c> and <c>.</c> are used instead of box-drawing glyphs
/// so a receipt can never render as garbled bytes on a printer configured for a
/// different code page.
/// </para>
/// </remarks>
public sealed class ReceiptRenderer
{
    /// <summary>Maximum characters of the product name shown on a line before it wraps.</summary>
    private const int MaxNameLength = 30;

    private readonly ReceiptOptions _options;

    public ReceiptRenderer(ReceiptOptions? options = null)
    {
        _options = options ?? new ReceiptOptions();
    }

    /// <summary>
    /// Renders the document to an ESC/POS byte stream.
    /// </summary>
    /// <param name="document">The sale and store configuration to render.</param>
    /// <param name="openDrawer">
    /// Whether to append a cash-drawer kick. Only meaningful for a cash receipt on a
    /// printer that has a drawer attached.
    /// </param>
    public byte[] Render(ReceiptDocument document, bool openDrawer = false)
    {
        // ReceiptDocument is a struct, so there is nothing to null-check here.
        // A kitchen ticket is a different document with different priorities, not a receipt
        // with prices hidden.
        if (document.Type == PosDocumentType.KitchenTicket)
        {
            return RenderKitchenTicket(document);
        }

        var store = document.Store;
        var sale = document.Sale;

        // The store owns the paper width; a 58mm printer cannot fit 48-column lines.
        var builder = new EscPosBuilder(Math.Max(24, store.ReceiptColumns));

        builder.Initialise();

        RenderHeader(builder, document);
        RenderSaleLines(builder, document);
        RenderTotals(builder, document);
        RenderTenders(builder, document);
        RenderTaxSummary(builder, document);
        RenderFooter(builder, document);

        if (openDrawer)
        {
            builder.OpenDrawer();
        }

        builder.Cut();

        return builder.ToArray();
    }

    /// <summary>
    /// Renders a kitchen or bar preparation ticket.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately a different document from a receipt, because it is read under pressure by
    /// someone assembling an order rather than by a customer checking a total:
    /// </para>
    /// <list type="bullet">
    /// <item>Quantities are enlarged — the single most misread element in a kitchen.</item>
    /// <item>Prices, tax, and payment are omitted. A cook does not need them, and printing
    /// them wastes paper and clutters the ticket.</item>
    /// <item>The time is prominent so an expediter can judge how long an order has been up.</item>
    /// <item>A partial cut is used, so the ticket stays attached to the roll for the pass.</item>
    /// </list>
    /// </remarks>
    public static byte[] RenderKitchenTicket(ReceiptDocument document, string? station = null)
    {
        var store = document.Store;
        var sale = document.Sale;

        // A ticket carries only the lines its station has to make.
        //
        // Routing is done by StationTicketRouter, but the filter is repeated here so that a caller
        // who hands the renderer a whole sale cannot print every station's work on every station's
        // printer. Printing the bar's drinks on the kitchen's ticket is worse than useless: it is
        // how a kitchen learns to read past what it is handed, and then misses a real item.
        var lines = FilterByStation(store, sale, document.StationId ?? station);

        // The station name comes from the store's own list, so a ticket says "BAR" rather than
        // "bar-01". An undeclared station prints its raw id, which makes the configuration gap
        // visible instead of hiding it behind a guess — and, importantly, is the same rule the
        // router used to decide which lines are here.
        var heading = document.StationId is { Length: > 0 } routed
            ? store.StationNameFor(routed)
            : station ?? "KITCHEN";

        var builder = new EscPosBuilder(Math.Max(24, store.ReceiptColumns));
        builder.Initialise();

        // Ordered time is what the kitchen works to, so it leads.
        builder.Align(TextAlignment.Centre);
        builder.Bold().TextSize(2, 2).Line("#" + sale.Number.Sequence.ToString("D4", CultureInfo.InvariantCulture)).TextSize().Bold(false);
        builder.TextSize(2, 2).Line(heading.ToUpperInvariant()).TextSize();

        builder.TextSize(1, 2);
        builder.Line(sale.CompletedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));
        builder.TextSize();

        builder.Align(TextAlignment.Left);
        builder.TwoColumns("Receipt", sale.Number.ToString());
        builder.Rule('=');

        foreach (var line in lines)
        {
            // Quantity first and large: it is the element most often misread when a ticket is
            // read at a glance on a busy pass.
            builder.TextSize(2, 2);
            builder.Line($"{FormatQuantity(line.Quantity)} x {line.Name}");
            builder.TextSize();

            if (!string.IsNullOrWhiteSpace(line.Note))
            {
                builder.Bold().Line($"   >> {line.Note}").Bold(false);
            }

            builder.Line();
        }

        builder.Rule('=');

        // The count is this station's, not the sale's. A cook counting items on the ticket against
        // a total that included the bar's drinks would think something was missing every time.
        builder.TwoColumns("Items", FormatQuantity(lines.Sum(l => l.Quantity)));

        if (!string.IsNullOrWhiteSpace(document.CashierName))
        {
            builder.TwoColumns("Taken by", document.CashierName);
        }

        if (document.IsCopy)
        {
            builder.Bold().Line("*** REPRINT ***").Bold(false);
        }

        builder.Feed(2);

        // Partial cut so the ticket stays on the roll at the pass rather than falling to the
        // floor, which is what a full cut would do to a kitchen printer mounted vertically.
        builder.Cut(PaperCut.Partial);

        return builder.ToArray();
    }

    /// <summary>
    /// Narrows a sale to the lines one station prepares.
    /// </summary>
    /// <remarks>
    /// A ticket with no station filter carries every line, which is how a single-printer shop that
    /// sends everything to one machine behaves. Once a station is named, only its lines print.
    /// </remarks>
    private static IReadOnlyList<SaleLine> FilterByStation(Store store, Sale sale, string? stationId)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        {
            return sale.Lines;
        }

        var wanted = store.FindStation(stationId)?.Id ?? stationId;

        return
        [
            .. sale.Lines.Where(l =>
                !string.IsNullOrWhiteSpace(l.StationId) &&
                string.Equals(l.StationId, wanted, StringComparison.OrdinalIgnoreCase)),
        ];
    }

    /// <summary>
    /// Renders a refund document for a return.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately unmistakable. A refund slip that looks like a receipt is a fraud tool: it
    /// can be presented elsewhere as proof of purchase, or used to walk goods out of a shop.
    /// So the word REFUND is enlarged, the document states it is not a receipt, and the original
    /// sale is referenced so the return can be traced back.
    /// </para>
    /// <para>
    /// The customer's signature line is printed because a refund is the one transaction where a
    /// signature is genuinely worth having: it is the shop's evidence that the money was handed
    /// over to a person.
    /// </para>
    /// </remarks>
    public static byte[] RenderRefund(
        Store store,
        SalesReturn salesReturn,
        string? cashierName = null,
        bool isCopy = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(salesReturn);

        var builder = new EscPosBuilder(Math.Max(24, store.ReceiptColumns));
        builder.Initialise();

        // The banner is the first thing printed and the largest element on the page.
        builder.Align(TextAlignment.Centre);
        builder.Bold().TextSize(3, 3).Line("REFUND").TextSize().Bold(false);

        // Stated explicitly, so the slip cannot be mistaken for a receipt if it is separated
        // from the rest of the paperwork.
        builder.Bold().Line("*** NOT A RECEIPT ***").Bold(false);
        builder.Line(store.Name);

        if (isCopy)
        {
            builder.Bold().Line("*** COPY ***").Bold(false);
        }

        builder.Align(TextAlignment.Left);
        builder.Line();

        builder.TwoColumns("Refund", salesReturn.Number);
        builder.TwoColumns("Refunded", salesReturn.CompletedAt.ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        builder.TwoColumns("Original sale", salesReturn.OriginalSaleNumber.ToString());

        if (!string.IsNullOrWhiteSpace(cashierName))
        {
            builder.TwoColumns("Processed by", cashierName);
        }

        builder.TwoColumns("Reason", DescribeReason(salesReturn.Reason));

        if (!string.IsNullOrWhiteSpace(salesReturn.Note))
        {
            builder.Line($"Note: {salesReturn.Note}");
        }

        builder.Rule('=');

        foreach (var line in salesReturn.Lines)
        {
            var name = line.Name.Length > MaxNameLength ? line.Name[..MaxNameLength] : line.Name;

            builder.TwoColumns(
                $"{FormatQuantity(line.Quantity)} x {name}",
                FormatAmount(line.LineRefund, salesReturn.Currency));

            if (line.Quantity != 1m)
            {
                builder.Line($"    @ {FormatAmount(line.UnitRefund.Amount, salesReturn.Currency)}");
            }
        }

        builder.Rule('-');

        // The refund total is the figure the customer checks, so it is the boldest number.
        builder.Bold();
        builder.TwoColumns("REFUND TOTAL", FormatAmount(salesReturn.TotalRefund, salesReturn.Currency));
        builder.Bold(false);

        builder.TwoColumns("(tax reversed", FormatAmount(salesReturn.TaxReversed, salesReturn.Currency) + ")");

        builder.Line();

        // How the money went back, which is what reconciles against the drawer.
        foreach (var refund in salesReturn.Refunds)
        {
            builder.TwoColumns(TenderLabel(refund.Type), FormatAmount(refund.Amount.Amount, salesReturn.Currency));
        }

        builder.TwoColumns("Items returned", FormatQuantity(salesReturn.TotalQuantity));

        builder.Rule('=');
        builder.Align(TextAlignment.Centre);

        builder.Line("Customer signature:");
        builder.Line();
        builder.Line("___________________________");
        builder.Line();

        // Wrapped rather than printed as one fixed line: at 34 characters this overflows a 58mm
        // roll, where it would wrap mid-sentence and shift the layout.
        foreach (var line in WrapToColumns("Keep this slip as proof of refund.", store.ReceiptColumns))
        {
            builder.Line(line);
        }

        builder.Align(TextAlignment.Left);
        builder.Feed(2);

        // A full cut: the slip is handed to the customer, not left on the roll.
        builder.Cut(PaperCut.Full);

        return builder.ToArray();
    }

    /// <summary>
    /// Breaks text into lines that fit the paper width.
    /// </summary>
    /// <remarks>
    /// A fixed-length sentence printed on a narrower roll wraps where the printer chooses and
    /// pushes everything after it out of place. Wrapping deliberately keeps the layout intact.
    /// </remarks>
    internal static List<string> WrapToColumns(string text, int columns)
    {
        if (string.IsNullOrWhiteSpace(text) || columns <= 0 || text.Length <= columns)
        {
            return [text];
        }

        var lines = new List<string>();
        var current = new StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length == 0)
            {
                current.Append(word);
                continue;
            }

            if (current.Length + 1 + word.Length <= columns)
            {
                current.Append(' ').Append(word);
                continue;
            }

            lines.Add(current.ToString());
            current.Clear();
            current.Append(word);
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }

    /// <summary>Describes a return reason in words a customer will understand.</summary>
    internal static string DescribeReason(ReturnReason reason) => reason switch
    {
        ReturnReason.ChangedMind => "Changed mind",
        ReturnReason.Faulty => "Faulty",
        ReturnReason.WrongItem => "Wrong item",
        ReturnReason.NotAsDescribed => "Not as described",
        ReturnReason.Damaged => "Damaged",
        ReturnReason.Expired => "Out of date",
        ReturnReason.Goodwill => "Goodwill",
        _ => reason.ToString(),
    };

    // ------------------------------------------------------------------- Sections

    private static void RenderHeader(EscPosBuilder b, ReceiptDocument doc)
    {
        var store = doc.Store;

        b.Align(TextAlignment.Centre);

        if (doc.Type == PosDocumentType.KitchenTicket)
        {
            b.Bold().TextSize(2, 2).Line("KITCHEN").TextSize().Bold(false);
        }

        b.Bold().TextSize(2, 2).Line(store.Name).TextSize().Bold(false);

        foreach (var addressLine in store.AddressLines)
        {
            b.Line(addressLine);
        }

        if (!string.IsNullOrWhiteSpace(store.Phone))
        {
            b.Line($"Tel: {store.Phone}");
        }

        if (!string.IsNullOrWhiteSpace(store.TaxRegistrationNumber))
        {
            b.Line($"VAT No: {store.TaxRegistrationNumber}");
        }

        b.Align(TextAlignment.Left);
        b.Line();

        // A reprint must be visibly marked, otherwise a duplicate receipt could be
        // presented twice for the same refund.
        if (doc.IsCopy)
        {
            b.Align(TextAlignment.Centre);
            b.Bold().Line(doc.CopyIndex > 1 ? $"*** COPY {doc.CopyIndex} ***" : "*** COPY ***").Bold(false);
            b.Align(TextAlignment.Left);
        }

        b.TwoColumns("Receipt", doc.Sale.Number.ToString());
        b.TwoColumns("Date", doc.Sale.CompletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(doc.Sale.EmployeeId))
        {
            var label = string.IsNullOrWhiteSpace(doc.CashierName) ? doc.Sale.EmployeeId : doc.CashierName;
            b.TwoColumns("Cashier", label);
        }

        if (doc.Sale.IsVoided)
        {
            b.Bold().Line("*** VOIDED ***").Bold(false);
        }

        b.Rule('=');
    }

    private static void RenderSaleLines(EscPosBuilder b, ReceiptDocument doc)
    {
        foreach (var line in doc.Sale.Lines)
        {
            // The product name may exceed the space left by the amount. Truncating here
            // keeps the amount in the right-hand column on every line, which matters
            // more than showing the full name.
            var name = line.Name.Length > MaxNameLength
                ? line.Name[..MaxNameLength]
                : line.Name;

            var quantity = FormatQuantity(line.Quantity);

            b.TwoColumns($"{quantity} x {name}", FormatAmount(line.GrossAmount, doc.Sale.Currency));

            // Unit price only earns a line when it cannot be inferred, i.e. for
            // multi-unit or weighed lines.
            if (line.Quantity != 1m)
            {
                b.Line($"    @ {FormatAmount(line.UnitPrice.Amount, doc.Sale.Currency)}");
            }

            if (line.DiscountAmount != 0m)
            {
                b.Line($"    Discount -{FormatAmount(line.DiscountAmount, doc.Sale.Currency)}");
            }
        }

        b.Rule('-');
    }

    private static void RenderTotals(EscPosBuilder b, ReceiptDocument doc)
    {
        var sale = doc.Sale;
        var currency = sale.Currency;

        // In inclusive mode the subtotal already contains tax, so printing "Subtotal"
        // and then adding tax would double-count. Exclusive mode needs both lines.
        if (sale.TaxMode == TaxMode.Exclusive)
        {
            b.TwoColumns("Subtotal", FormatAmount(sale.Tax.Net, currency));
            RenderTaxLines(b, doc);
            b.Bold();
            b.TwoColumns("TOTAL", FormatAmount(sale.Total, currency));
            b.Bold(false);
        }
        else
        {
            b.Bold();
            b.TwoColumns("TOTAL", FormatAmount(sale.Total, currency));
            b.Bold(false);
            b.Line($"(includes {FormatAmount(sale.Tax.Tax, currency)} tax)");
        }

        if (sale.TotalDiscount != 0m)
        {
            b.TwoColumns("You saved", FormatAmount(sale.TotalDiscount, currency));
        }

        b.Rule('=');
    }

    private static void RenderTaxLines(EscPosBuilder b, ReceiptDocument doc)
    {
        foreach (var component in doc.Sale.Tax.Components.Where(c => c.IsReportable))
        {
            // Zero-rated lines are reported as "0%" rather than being hidden, because
            // the tax return needs to show they were accounted for at zero.
            var label = component.Rate.IsZero ? "Tax 0%" : FormatRate(component.Rate);
            b.TwoColumns(label, FormatAmount(component.Tax, doc.Sale.Currency));
        }
    }

    private static void RenderTenders(EscPosBuilder b, ReceiptDocument doc)
    {
        var sale = doc.Sale;

        foreach (var tender in sale.Tenders)
        {
            b.TwoColumns(TenderLabel(tender.Type), FormatAmount(tender.Amount.Amount, sale.Currency));
        }

        var change = sale.ChangeGiven;
        if (change != 0m)
        {
            b.Bold();
            b.TwoColumns("CHANGE", FormatAmount(change, sale.Currency));
            b.Bold(false);
        }

        var itemCount = sale.TotalQuantity;
        b.TwoColumns(
            itemCount == 1m ? "1 item" : $"{FormatQuantity(itemCount)} items",
            string.Empty);

        b.Line();
    }

    private static void RenderTaxSummary(EscPosBuilder b, ReceiptDocument doc)
    {
        if (doc.Sale.TaxMode != TaxMode.Inclusive)
        {
            return;
        }

        var reportable = doc.Sale.Tax.Components.Where(c => c.IsReportable).ToArray();
        if (reportable.Length <= 1)
        {
            // A single tax rate is already covered by the "(includes ... tax)" line.
            return;
        }

        b.Line("Tax summary");
        foreach (var component in reportable)
        {
            var label = component.Rate.IsZero ? "Zero rated" : FormatRate(component.Rate);
            b.TwoColumns(
                $"  {label} on {FormatAmount(component.Net, doc.Sale.Currency)}",
                FormatAmount(component.Tax, doc.Sale.Currency));
        }

        b.Line();
    }

    private void RenderFooter(EscPosBuilder b, ReceiptDocument doc)
    {
        b.Align(TextAlignment.Centre);

        if (_options.PrintReturnQrCode && doc.Type is PosDocumentType.SalesReceipt or PosDocumentType.SalesReceiptCopy)
        {
            var payload = BuildReturnPayload(doc);
            if (payload.Length > 0)
            {
                b.Line();
                b.QrCode(payload, _options.QrModuleSize);
            }
        }

        foreach (var line in doc.Store.ReceiptFooter.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            b.Line(line.Trim());
        }

        if (_options.PrintPoweredBy)
        {
            b.Line();
            b.Line("Powered by SmartQ Blazor POS");
        }

        b.Align(TextAlignment.Left);
        b.Feed(_options.FooterFeedLines);
    }

    // -------------------------------------------------------------------- Helpers

    /// <summary>
    /// Builds the QR payload printed on the receipt.
    /// </summary>
    /// <remarks>
    /// Carries the identifiers needed to look the sale up again, not the basket
    /// contents — a QR code has finite capacity and the server already holds the rest.
    /// </remarks>
    internal static string BuildReturnPayload(ReceiptDocument doc)
    {
        var sale = doc.Sale;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"POS|{doc.Store.Code}|{sale.Number}|{sale.Id.Value:N}|{sale.Total:F2}|{sale.CompletedAt.ToUnixTimeSeconds()}");
    }

    /// <summary>Formats an amount for the receipt's numeric column.</summary>
    internal static string FormatAmount(decimal amount, string currency)
    {
        _ = currency; // currency is already established by the store; no symbol is printed
        return amount.ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Formats a quantity, omitting a decimal point for whole numbers.
    /// </summary>
    /// <remarks>
    /// "2 x Cola" reads better than "2.0 x Cola", but weighed goods still need their
    /// fractional part, so the two cases are distinguished rather than formatted alike.
    /// </remarks>
    internal static string FormatQuantity(decimal quantity) =>
        quantity == decimal.Truncate(quantity)
            ? decimal.Truncate(quantity).ToString("0", CultureInfo.InvariantCulture)
            : quantity.ToString("0.###", CultureInfo.InvariantCulture);

    internal static string FormatRate(TaxRate rate) =>
        string.Create(CultureInfo.InvariantCulture, $"{rate.Name} {rate.Rate * 100m:0.##}%");

    internal static string TenderLabel(TenderType type) => type switch
    {
        TenderType.Cash => "Cash",
        TenderType.ExternalCard => "Card",
        TenderType.GiftCard => "Gift card",
        TenderType.Voucher => "Voucher",
        TenderType.StoreCredit => "Store credit",
        TenderType.LoyaltyPoints => "Loyalty points",
        _ => type.ToString(),
    };
}

/// <summary>Optional switches controlling what the receipt includes.</summary>
public sealed class ReceiptOptions
{
    /// <summary>Print a QR code that lets the sale be looked up for a return.</summary>
    public bool PrintReturnQrCode { get; set; } = true;

    /// <summary>QR module size in dots. Larger is more scannable but takes more paper.</summary>
    public int QrModuleSize { get; set; } = 6;

    /// <summary>Print a software attribution line in the footer.</summary>
    public bool PrintPoweredBy { get; set; }

    /// <summary>Blank lines before the cut, so the receipt clears the tear bar.</summary>
    public int FooterFeedLines { get; set; } = 2;
}
