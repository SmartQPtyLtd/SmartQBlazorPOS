// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Globalization;
using Pos.Core.Reporting;
using Pos.Devices.EscPos;

namespace Pos.Devices.Reports;

/// <summary>
/// Renders a trading report onto an ESC/POS receipt printer.
/// </summary>
/// <remarks>
/// <para>
/// A report is printed on the same 80mm roll as a receipt, because that is the only printer a
/// shop has at the till. That constrains the layout to 48 columns and monospaced text, so the
/// figures are arranged to be read down a column rather than as a table.
/// </para>
/// <para>
/// The report is designed to be <b>signed and filed</b>: it is the document a cashier and a
/// manager both check, so anything that could be disputed — voids, discounts, and the tender
/// split — is printed explicitly rather than summarised away.
/// </para>
/// </remarks>
public static class ReportRenderer
{
    /// <summary>Default paper width in characters for an 80mm roll.</summary>
    public const int DefaultColumns = 48;

    /// <summary>Renders the report as an ESC/POS document.</summary>
    public static byte[] Render(TradingDayReport report, int columns = DefaultColumns)
    {
        ArgumentNullException.ThrowIfNull(report);

        var width = Math.Max(24, columns);
        var builder = new EscPosBuilder(width);
        builder.Initialise();

        RenderHeader(builder, report);
        RenderTotals(builder, report);
        RenderTenders(builder, report);
        RenderTaxNote(builder, report);
        RenderProducts(builder, report);
        RenderHourly(builder, report);
        RenderFooter(builder, report, width);

        // A full cut: the report is filed, not torn off a continuous pass.
        builder.Cut(PaperCut.Full);

        return builder.ToArray();
    }

    private static void RenderHeader(EscPosBuilder b, TradingDayReport report)
    {
        b.Align(TextAlignment.Centre);
        b.Bold().TextSize(2, 2).Line(report.Title).TextSize().Bold(false);
        b.Line(report.StoreName);
        b.Line(report.BusinessDate.ToString("dddd, dd MMMM yyyy", CultureInfo.InvariantCulture));
        b.Align(TextAlignment.Left);

        b.TwoColumns(
            report.IsFinal ? "Closed at" : "Read at",
            report.GeneratedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));

        b.Rule('=');
    }

    private static void RenderTotals(EscPosBuilder b, TradingDayReport report)
    {
        b.Bold().Line("TAKINGS").Bold(false);

        b.TwoColumns("Sales", report.SaleCount.ToString(CultureInfo.InvariantCulture));
        b.TwoColumns("Gross", Amount(report.Gross));
        b.TwoColumns("Net of tax", Amount(report.Net));
        b.TwoColumns("Tax collected", Amount(report.Tax));
        b.TwoColumns("Discounts given", Amount(report.Discounts));
        b.TwoColumns("Average sale", Amount(report.AverageSale));

        // Voids are printed separately and never netted off. A single netted figure would hide
        // them, and voids are precisely the number a dishonest till would want hidden.
        if (report.VoidCount > 0)
        {
            b.Line();
            b.Bold().Line("VOIDS").Bold(false);
            b.TwoColumns("Count", report.VoidCount.ToString(CultureInfo.InvariantCulture));
            b.TwoColumns("Value", Amount(report.VoidedValue));
        }

        // Refunds are money out of the drawer, and leaving them off the signed slip was a real
        // inconsistency: the estate report subtracted them and this one did not, so the two
        // disagreed about the same day — with the shop's own figure being the wrong one.
        if (report.RefundCount > 0)
        {
            b.Line();
            b.Bold().Line("REFUNDS").Bold(false);
            b.TwoColumns("Count", report.RefundCount.ToString(CultureInfo.InvariantCulture));
            b.TwoColumns("Value", Amount(report.RefundTotal));

            // Stated separately from tax collected: one is owed onward, the other reclaimed.
            b.TwoColumns("Tax reversed", Amount(report.RefundTax));
        }

        b.Rule('-');

        // The figure a shopkeeper means by "what we took today". Printed last in the takings block
        // so it reads as the bottom line rather than as one more line among the others.
        b.Bold().TwoColumns("NET TAKINGS", Amount(report.NetTakings)).Bold(false);
        b.Rule('-');
    }

    private static void RenderTenders(EscPosBuilder b, TradingDayReport report)
    {
        b.Bold().Line("TENDERED").Bold(false);

        if (report.TenderedByType.Count == 0)
        {
            b.Line("  (none)");
        }
        else
        {
            foreach (var (type, amount) in report.TenderedByType.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                b.TwoColumns($"  {type}", Amount(amount));
            }

            // The sum a cashier counts against the drawer. Stating it saves the arithmetic and
            // makes a discrepancy obvious at a glance.
            b.TwoColumns("  total", Amount(report.TenderedTotal));
        }

        b.Rule('-');
    }

    private static void RenderTaxNote(EscPosBuilder b, TradingDayReport report)
    {
        if (report.Tax == 0m)
        {
            return;
        }

        b.Line($"Tax of {Amount(report.Tax)} is owed onward.");
        b.Rule('-');
    }

    private static void RenderProducts(EscPosBuilder b, TradingDayReport report)
    {
        if (report.TopProducts.Count == 0)
        {
            return;
        }

        b.Bold().Line("TOP SELLERS").Bold(false);

        foreach (var product in report.TopProducts)
        {
            // Quantity and revenue on the label side, revenue on the right; the product name is
            // what gets truncated when space is short.
            b.TwoColumns(
                $"  {FormatQuantity(product.QuantitySold)} x {product.Name}",
                Amount(product.Gross));
        }

        b.Rule('-');
    }

    private static void RenderHourly(EscPosBuilder b, TradingDayReport report)
    {
        if (report.Hourly.Count == 0)
        {
            return;
        }

        b.Bold().Line("BY HOUR").Bold(false);

        foreach (var hour in report.Hourly)
        {
            var label = string.Create(
                CultureInfo.InvariantCulture,
                $"  {hour.Hour:D2}:00 ({hour.SaleCount})");

            b.TwoColumns(label, Amount(hour.Gross));
        }

        b.Rule('=');
    }

    private static void RenderFooter(EscPosBuilder b, TradingDayReport report, int columns)
    {
        b.Align(TextAlignment.Centre);

        if (report.IsEmpty)
        {
            b.Bold().Line("NO TRADING RECORDED").Bold(false);
        }
        else if (report.IsFinal)
        {
            WriteWrapped(b, "Day closed. File this report.", columns);
        }
        else
        {
            // An X-report must not be mistaken for a day close, or the day would be reconciled
            // twice from two different readings.
            WriteWrapped(b, "Mid-shift reading. Day remains open.", columns);
        }

        // Signature lines: this is the document the cashier and the manager both sign.
        b.Line();
        b.Line();
        b.Align(TextAlignment.Left);
        WriteWrapped(b, "Cashier  ______________", columns);
        WriteWrapped(b, "Manager  ______________", columns);
        b.Align(TextAlignment.Centre);
        b.Feed(2);
    }

    /// <summary>
    /// Writes text, breaking it across lines so it fits the paper.
    /// </summary>
    /// <remarks>
    /// Wrapped rather than truncated. A footer that reads "Mid-shift reading. Day rem" is worse
    /// than useless on a document whose whole purpose is to say whether the day is still open,
    /// and a 58mm roll is only 32 columns wide.
    /// </remarks>
    private static void WriteWrapped(EscPosBuilder b, string text, int columns)
    {
        if (text.Length <= columns)
        {
            b.Line(text);
            return;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = new System.Text.StringBuilder();

        foreach (var word in words)
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

            b.Line(current.ToString());
            current.Clear();
            current.Append(word);
        }

        if (current.Length > 0)
        {
            b.Line(current.ToString());
        }
    }

    private static string Amount(decimal amount) =>
        amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatQuantity(decimal quantity) =>
        quantity == decimal.Truncate(quantity)
            ? decimal.Truncate(quantity).ToString("0", CultureInfo.InvariantCulture)
            : quantity.ToString("0.###", CultureInfo.InvariantCulture);

    // -------------------------------------------------------------------- shift cash-up

    /// <summary>
    /// Renders a shift cash-up: the slip the operator and a supervisor both sign.
    /// </summary>
    /// <remarks>
    /// The drawer section is printed <b>only</b> when the shift is closed, and it reads its
    /// presence from <see cref="ShiftReport.ExpectedCash"/> rather than from
    /// <see cref="ShiftReport.IsFinal"/>. That way a mid-shift reading cannot print the expected
    /// figure even if a caller builds it wrongly: the value is simply not there, because a count
    /// taken with the answer in view is not a count.
    /// </remarks>
    public static byte[] RenderShift(ShiftReport report, int columns = DefaultColumns)
    {
        ArgumentNullException.ThrowIfNull(report);

        var width = Math.Max(24, columns);
        var builder = new EscPosBuilder(width);
        builder.Initialise();

        RenderShiftHeader(builder, report);
        RenderShiftActivity(builder, report);
        RenderShiftDrawer(builder, report, width);
        RenderShiftFooter(builder, report, width);

        builder.Cut(PaperCut.Full);

        return builder.ToArray();
    }

    private static void RenderShiftHeader(EscPosBuilder b, ShiftReport report)
    {
        b.Align(TextAlignment.Centre);
        b.Bold().TextSize(2, 2).Line(report.Title).TextSize().Bold(false);
        b.Line(report.StoreName);
        b.Align(TextAlignment.Left);

        b.Rule('=');

        // The operator is on the header, not buried in a footer: this document exists to put a
        // name against a drawer.
        b.TwoColumns("Operator", report.EmployeeName);
        b.TwoColumns("Till", report.TerminalId);
        b.TwoColumns("Opened", Local(report.OpenedAt, "dd MMM HH:mm"));

        if (report.ClosedAt is { } closed)
        {
            b.TwoColumns("Closed", Local(closed, "dd MMM HH:mm"));
        }

        b.TwoColumns(report.IsFinal ? "Closed at" : "Read at", Local(report.GeneratedAt, "HH:mm:ss"));
        b.Rule('-');
    }

    private static void RenderShiftActivity(EscPosBuilder b, ShiftReport report)
    {
        b.Bold().Line("ACTIVITY").Bold(false);

        b.TwoColumns("Sales", report.SaleCount.ToString(CultureInfo.InvariantCulture));
        b.TwoColumns("Gross takings", Amount(report.GrossTakings));
        b.TwoColumns("Tax collected", Amount(report.TaxCollected));

        // Cash and card are printed as separate lines rather than as one total. Card money never
        // entered the drawer, so a single figure would make every cash-up look over by the card
        // takings — and a number that is always wrong in the same direction is one everyone
        // learns to ignore.
        b.TwoColumns("  of which cash", Amount(report.CashTakings));
        b.TwoColumns("  of which card", Amount(report.NonCashTakings));

        if (report.RefundTotal != 0m)
        {
            b.TwoColumns("Refunds", Amount(report.RefundTotal));
            b.TwoColumns("  paid in cash", Amount(report.CashRefunds));
        }

        // Printed even when zero. "No voids" is information; an absent line is ambiguous.
        b.TwoColumns("Voids", report.VoidCount.ToString(CultureInfo.InvariantCulture));

        // Always printed, and never reset. Every no-sale opening is the classic cover for a small
        // theft, so the count being visible is the whole reason for recording it.
        b.TwoColumns("No-sale openings", report.NoSaleCount.ToString(CultureInfo.InvariantCulture));

        b.Rule('-');
    }

    private static void RenderShiftDrawer(EscPosBuilder b, ShiftReport report, int columns)
    {
        if (report.ExpectedCash is not { } expected)
        {
            // A mid-shift reading. Saying why is better than an unexplained omission, which would
            // read as a fault in the report.
            b.Align(TextAlignment.Centre);
            WriteWrapped(b, "Drawer total withheld until the count is entered.", columns);
            b.Align(TextAlignment.Left);
            b.Rule('=');

            return;
        }

        b.Bold().Line("DRAWER").Bold(false);

        b.TwoColumns("Opening float", Amount(report.OpeningFloat));

        if (report.CashIn != 0m)
        {
            b.TwoColumns("Cash in", Amount(report.CashIn));
        }

        if (report.CashOut != 0m)
        {
            b.TwoColumns("Cash out", Amount(report.CashOut));
        }

        if (report.CashRefunds != 0m)
        {
            b.TwoColumns("Cash refunds", $"-{Amount(report.CashRefunds)}");
        }

        b.Rule('-');
        b.Bold().TwoColumns("Expected", Amount(expected)).Bold(false);
        b.Bold().TwoColumns("Counted", Amount(report.ClosingCount ?? 0m)).Bold(false);

        if (report.Variance is { } variance)
        {
            // Labelled rather than left as a signed number. "SHORT 12.50" is read correctly by
            // everyone; "-12.50" invites the reader to work out which direction is bad.
            var label = variance switch
            {
                < 0m => "SHORT",
                > 0m => "OVER",
                _ => "BALANCED",
            };

            b.Bold();
            b.TwoColumns(label, variance == 0m ? Amount(0m) : Amount(Math.Abs(variance)));
            b.Bold(false);

            if (variance == 0m)
            {
                b.Align(TextAlignment.Centre);
                b.Line("Drawer balances.");
                b.Align(TextAlignment.Left);
            }
        }

        b.Rule('=');
    }

    private static void RenderShiftFooter(EscPosBuilder b, ShiftReport report, int columns)
    {
        b.Align(TextAlignment.Centre);

        if (report.IsEmpty)
        {
            b.Bold().Line("NO ACTIVITY RECORDED").Bold(false);
        }
        else if (report.IsFinal)
        {
            WriteWrapped(b, "Shift closed. File this slip.", columns);
        }
        else
        {
            // An X reading must not be mistaken for a close, or the drawer would be reconciled
            // twice from two different readings and the second would look like a discrepancy.
            WriteWrapped(b, "Mid-shift reading. Shift remains open.", columns);
        }

        b.Line();
        b.Line();
        b.Align(TextAlignment.Left);
        WriteWrapped(b, "Operator  ______________", columns);
        WriteWrapped(b, "Witnessed ______________", columns);
        b.Align(TextAlignment.Centre);
        b.Feed(2);
    }

    private static string Local(DateTimeOffset moment, string format) =>
        moment.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);
}
