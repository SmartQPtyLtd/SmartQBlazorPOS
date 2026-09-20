// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Globalization;
using System.Net;
using System.Text;
using Pos.Core.Domain;

namespace Pos.Devices.EscPos;

/// <summary>
/// Renders a printable document as HTML, for the transports that cannot carry ESC/POS.
/// </summary>
/// <remarks>
/// <para>
/// This is the other half of the fallback that works in every browser. WebUSB and Web Serial send
/// control bytes to a printer that understands them; Safari and Firefox have neither API, so the
/// only route to paper is the browser's own print dialog, which takes HTML.
/// </para>
/// <para>
/// It was missing. The transport existed and refused raw bytes with a message telling the caller to
/// use <c>PrintHtmlAsync</c>, and nothing anywhere produced the HTML or called it — so the fallback
/// the brief asks for in every other browser was selected, then threw, and the customer got no
/// receipt. The till reported a printer fault and the sale completed, which is exactly the sort of
/// quiet gap that only a whole-sale test finds.
/// </para>
/// <para>
/// <b>The markup vocabulary is a contract with the print frame.</b> The body produced here is written
/// into an iframe by <c>device-bridge.js</c>, which supplies the stylesheet — <c>.c</c> centred,
/// <c>.r</c> right, <c>.b</c> bold, <c>.big</c>, <c>.rule</c>, and a table with <c>td.amt</c> for
/// amounts. Emitting anything else produces an unstyled receipt: readable, but with every price
/// jammed against the item name and no alignment at all. The two files have to agree, and neither
/// can be changed without the other.
/// </para>
/// <para>
/// Nothing here can cut paper or open a drawer, and it does not pretend to.
/// </para>
/// </remarks>
public sealed class ReceiptHtmlRenderer
{
    private readonly CultureInfo _culture;
    private readonly string _currency;

    /// <summary>Creates a renderer for one store's currency.</summary>
    public ReceiptHtmlRenderer(string currency = "ZAR", CultureInfo? culture = null)
    {
        _currency = currency ?? string.Empty;
        _culture = culture ?? CultureInfo.CurrentCulture;
    }

    /// <summary>
    /// Renders a document to an HTML fragment suitable for a print dialog.
    /// </summary>
    /// <param name="document">The sale, store, and document type to render.</param>
    public string Render(ReceiptDocument document) =>
        document.Type == PosDocumentType.KitchenTicket
            ? RenderKitchenTicket(document)
            : RenderReceipt(document);

    /// <summary>The job name the print dialog shows.</summary>
    public static string TitleFor(ReceiptDocument document) => document.Type switch
    {
        PosDocumentType.KitchenTicket => $"Order {document.Sale.Number}",
        PosDocumentType.RefundReceipt => $"Refund {document.Sale.Number}",
        PosDocumentType.Report => $"Report {document.Sale.BusinessDate:yyyy-MM-dd}",
        _ => $"Receipt {document.Sale.Number}",
    };

    private string RenderReceipt(ReceiptDocument document)
    {
        var store = document.Store;
        var sale = document.Sale;
        var html = new StringBuilder();

        html.Append("<div class=\"c b\">").Append(Escape(store.Name)).Append("</div>");

        foreach (var line in store.AddressLines)
        {
            html.Append("<div class=\"c s\">").Append(Escape(line)).Append("</div>");
        }

        if (!string.IsNullOrWhiteSpace(store.Phone))
        {
            html.Append("<div class=\"c s\">Tel ").Append(Escape(store.Phone)).Append("</div>");
        }

        if (!string.IsNullOrWhiteSpace(store.TaxRegistrationNumber))
        {
            html.Append("<div class=\"c s\">VAT ")
                .Append(Escape(store.TaxRegistrationNumber))
                .Append("</div>");
        }

        html.Append(Rule());

        if (document.Type == PosDocumentType.RefundReceipt)
        {
            html.Append("<div class=\"c b big\">REFUND</div>");
        }
        else if (document.IsCopy)
        {
            // Marked so a duplicate cannot be mistaken for the original and refunded twice.
            html.Append("<div class=\"c b\">COPY ")
                .Append((document.CopyIndex + 1).ToString(_culture))
                .Append("</div>");
        }

        html.Append(Row("Receipt", Escape(sale.Number.ToString())));
        html.Append(Row(
            "Date",
            Escape(sale.CompletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", _culture))));

        if (!string.IsNullOrWhiteSpace(document.CashierName))
        {
            html.Append(Row("Served by", Escape(document.CashierName)));
        }

        html.Append(Rule());

        foreach (var line in sale.Lines)
        {
            var total = CartLine.Round(line.Quantity * line.UnitPrice.Amount) - line.DiscountAmount;

            html.Append(Row(Escape(line.Name), Money(total)));

            var detail = $"{Quantity(line.Quantity)} x {Money(line.UnitPrice.Amount)}";
            var rate = line.TaxRate.Rate > 0m
                ? $"{Escape(line.TaxRate.Name)} {Percent(line.TaxRate.Rate)}"
                : "No tax";

            html.Append(Row($"<span class=\"s\">{detail}</span>", $"<span class=\"s\">{rate}</span>"));

            if (line.DiscountAmount > 0m)
            {
                html.Append(Row("<span class=\"s\">Discount</span>", $"-{Money(line.DiscountAmount)}"));
            }

            if (!string.IsNullOrWhiteSpace(line.Note))
            {
                html.Append("<div class=\"s\">").Append(Escape(line.Note)).Append("</div>");
            }
        }

        html.Append(Rule());

        html.Append(Row("Subtotal", Money(sale.Subtotal)));

        if (sale.TotalDiscount > 0m)
        {
            html.Append(Row("Discounts", $"-{Money(sale.TotalDiscount)}"));
        }

        // Tax is shown the way the store is configured: a shop pricing tax-inclusively must not
        // print a total that appears to have tax added on top of it.
        //
        // This deliberately mirrors the thermal receipt rather than inventing its own presentation,
        // because the two are the same document on different paper. A single rate is stated on the
        // total; only a multi-rate sale gets a summary, since one line cannot honestly describe two
        // rates. A fallback that printed a visibly different receipt would be a second document to
        // keep correct.
        var reportable = sale.Tax.Components.Where(c => c.IsReportable).ToArray();

        if (sale.TaxMode == TaxMode.Inclusive && reportable.Length > 1)
        {
            html.Append("<div class=\"s\">Tax summary</div>");

            foreach (var component in reportable)
            {
                html.Append(Row(
                    $"<span class=\"s\">{Escape(component.Rate.IsZero ? "Zero rated" : component.Rate.Name)}"
                    + $" on {Money(component.Net)}</span>",
                    $"<span class=\"s\">{Money(component.Tax)}</span>"));
            }
        }

        html.Append("<table><tr><td class=\"b\">TOTAL</td><td class=\"amt b\">")
            .Append(Money(sale.Total))
            .Append("</td></tr></table>");

        if (sale.TaxMode == TaxMode.Inclusive && reportable.Length <= 1 && sale.Tax.Tax != 0m)
        {
            html.Append("<div class=\"c s\">(includes ")
                .Append(Money(sale.Tax.Tax))
                .Append(" tax)</div>");
        }

        html.Append(Rule());

        foreach (var tender in sale.Tenders)
        {
            html.Append(Row(Escape(ReceiptRenderer.TenderLabel(tender.Type)), Money(tender.Amount.Amount)));

            if (tender.Tendered is { } handed && handed.Amount > tender.Amount.Amount)
            {
                html.Append(Row("<span class=\"s\">Change</span>", Money(handed.Amount - tender.Amount.Amount)));
            }

            if (!string.IsNullOrWhiteSpace(tender.Reference))
            {
                html.Append("<div class=\"s\">").Append(Escape(tender.Reference)).Append("</div>");
            }
        }

        html.Append(Rule());

        if (!string.IsNullOrWhiteSpace(store.ReceiptFooter))
        {
            foreach (var line in store.ReceiptFooter.Split(
                '\n', StringSplitOptions.RemoveEmptyEntries))
            {
                html.Append("<div class=\"c\">").Append(Escape(line.Trim())).Append("</div>");
            }
        }

        return html.ToString();
    }

    /// <summary>
    /// Renders a preparation ticket.
    /// </summary>
    /// <remarks>
    /// A different document, not a receipt with prices hidden: quantities are large because they are
    /// the most misread element, the time is prominent because an expediter judges the order by it,
    /// and money is absent entirely because a cook has no use for it.
    /// </remarks>
    private string RenderKitchenTicket(ReceiptDocument document)
    {
        var store = document.Store;
        var sale = document.Sale;

        var lines = document.StationId is { Length: > 0 } station
            ? sale.Lines.Where(l => string.Equals(l.StationId, station, StringComparison.Ordinal)).ToArray()
            : [.. sale.Lines];

        var html = new StringBuilder();

        html.Append(Row(
            $"<span class=\"b\">{Escape(store.StationNameFor(document.StationId) ?? "KITCHEN")}</span>",
            $"<span class=\"b\">{Escape(sale.CompletedAt.ToLocalTime().ToString("HH:mm", _culture))}</span>"));

        html.Append(Row("Order", Escape(sale.Number.ToString())));

        if (!string.IsNullOrWhiteSpace(document.CashierName))
        {
            html.Append(Row("<span class=\"s\">Server</span>", $"<span class=\"s\">{Escape(document.CashierName)}</span>"));
        }

        html.Append(Rule());

        foreach (var line in lines)
        {
            html.Append("<table><tr><td class=\"big\">")
                .Append(Quantity(line.Quantity))
                .Append("x</td><td class=\"big\">")
                .Append(Escape(line.Name))
                .Append("</td></tr></table>");

            if (!string.IsNullOrWhiteSpace(line.Note))
            {
                html.Append("<div class=\"b\">&gt; ").Append(Escape(line.Note)).Append("</div>");
            }
        }

        html.Append(Rule());
        html.Append("<div class=\"c s\">")
            .Append(lines.Length.ToString(_culture))
            .Append(lines.Length == 1 ? " item" : " items")
            .Append("</div>");

        return html.ToString();
    }

    /// <summary>A two-column line, amount right-aligned. The print stylesheet's <c>td.amt</c> does the alignment.</summary>
    private static string Row(string left, string right) =>
        $"<table><tr><td>{left}</td><td class=\"amt\">{right}</td></tr></table>";

    private static string Rule() => "<div class=\"rule\"></div>";

    private string Money(decimal amount) =>
        amount.ToString("0.00", _culture) + (string.IsNullOrEmpty(_currency) ? string.Empty : " " + _currency);

    private string Quantity(decimal quantity) => quantity.ToString("0.###", _culture);

    private string Percent(decimal rate) => (rate * 100m).ToString("0.##", _culture) + "%";

    /// <summary>
    /// Escapes text for HTML.
    /// </summary>
    /// <remarks>
    /// Not optional. Product names, notes, and the store's own footer come from head office and from
    /// whatever an operator typed, and this string is handed to a browser to render — so a name
    /// containing markup would otherwise be injected into the document that gets printed.
    /// </remarks>
    private static string Escape(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
}
