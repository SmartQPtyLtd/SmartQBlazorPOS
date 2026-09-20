// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;

namespace Pos.Core.Reporting;

/// <summary>
/// A cash-up for one shift: what the drawer should hold, and what was counted in it.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a <see cref="TradingDayReport"/>. A trading-day report is about a store and a
/// date; this is about <em>a person and a drawer</em>. They answer different questions, and
/// merging them would produce a document that is signed by the wrong person for the wrong period.
/// </para>
/// <para>
/// <b>The expected figure is nullable, and that is the point.</b> A mid-shift reading (an
/// X-report) must not reveal what the drawer is expected to hold: the cashier is about to count
/// it, and a count made with the answer in view is not a count, it is a copy. Making the field
/// absent for a non-final report encodes that rule in the type, so it cannot be defeated by a UI
/// that forgets to hide it.
/// </para>
/// </remarks>
/// <param name="StoreName">Store the drawer belongs to.</param>
/// <param name="EmployeeName">Operator accountable for the cash.</param>
/// <param name="TerminalId">Till the drawer sits in.</param>
/// <param name="IsFinal">
/// True for a Z-report, which closes the shift. False for a mid-shift X reading, which leaves it
/// open and withholds the expectation.
/// </param>
/// <param name="OpenedAt">When the drawer was opened.</param>
/// <param name="ClosedAt">When it was closed, for a final report.</param>
/// <param name="GeneratedAt">When this document was produced.</param>
/// <param name="SaleCount">Completed sales during the shift.</param>
/// <param name="VoidCount">Sales voided during the shift, never netted off.</param>
/// <param name="GrossTakings">Total taken including tax.</param>
/// <param name="TaxCollected">Tax collected, owed onward.</param>
/// <param name="CashTakings">Cash taken, which is what entered the drawer.</param>
/// <param name="NonCashTakings">Card and other tenders, which did not.</param>
/// <param name="RefundTotal">Total refunded.</param>
/// <param name="CashRefunds">Refunds paid out in cash, which left the drawer.</param>
/// <param name="OpeningFloat">Float counted in at open.</param>
/// <param name="CashIn">Cash added during the shift.</param>
/// <param name="CashOut">Cash removed during the shift.</param>
/// <param name="NoSaleCount">
/// Drawer openings with no sale. Always printed, because an unexplained opening is the classic
/// cover for a small theft and a count that is never shown is a count nobody can question.
/// </param>
/// <param name="ExpectedCash">
/// What should be in the drawer, or <b>null</b> for a mid-shift reading, where revealing it would
/// corrupt the count that has not happened yet.
/// </param>
/// <param name="ClosingCount">Cash actually counted, or null while the shift is open.</param>
/// <param name="Variance">
/// Counted minus expected, or null while the shift is open. Negative is a shortage.
/// </param>
public sealed record ShiftReport(
    string StoreName,
    string EmployeeName,
    string TerminalId,
    bool IsFinal,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset GeneratedAt,
    int SaleCount,
    int VoidCount,
    decimal GrossTakings,
    decimal TaxCollected,
    decimal CashTakings,
    decimal NonCashTakings,
    decimal RefundTotal,
    decimal CashRefunds,
    decimal OpeningFloat,
    decimal CashIn,
    decimal CashOut,
    int NoSaleCount,
    decimal? ExpectedCash,
    decimal? ClosingCount,
    decimal? Variance)
{
    /// <summary>Name of the document as a cashier knows it.</summary>
    public string Title => IsFinal ? "Z-REPORT (shift close)" : "X-REPORT (mid-shift)";

    /// <summary>True when nothing was recorded against the shift.</summary>
    public bool IsEmpty => SaleCount == 0 && VoidCount == 0 && NoSaleCount == 0;

    /// <summary>True when the drawer balanced to the cent.</summary>
    public bool Balanced => Variance is { } variance && variance == 0m;

    /// <summary>True when the drawer is short, which is the case worth investigating.</summary>
    public bool IsShort => Variance is { } variance && variance < 0m;

    /// <summary>True when the drawer is over.</summary>
    public bool IsOver => Variance is { } variance && variance > 0m;

    /// <summary>
    /// Builds the document from a shift's derived totals.
    /// </summary>
    /// <param name="totals">Derived cash-up figures, never accumulated counters.</param>
    /// <param name="storeName">Store the drawer belongs to.</param>
    /// <param name="terminalId">Till the drawer sits in.</param>
    /// <param name="isFinal">True to close the shift, false for a mid-shift reading.</param>
    /// <param name="generatedAt">When the document was produced.</param>
    /// <remarks>
    /// A non-final report has its expectation, count, and variance stripped. This is the single
    /// place that rule lives, so every caller — the screen, the printer, and any future export —
    /// inherits it rather than each having to remember it.
    /// </remarks>
    public static ShiftReport From(
        ShiftTotals totals,
        string storeName,
        string terminalId,
        bool isFinal,
        DateTimeOffset generatedAt) => new(
            StoreName: storeName,
            EmployeeName: totals.EmployeeName,
            TerminalId: terminalId,
            IsFinal: isFinal,
            OpenedAt: totals.OpenedAt,
            ClosedAt: totals.ClosedAt,
            GeneratedAt: generatedAt,
            SaleCount: totals.SaleCount,
            VoidCount: totals.VoidCount,
            GrossTakings: totals.GrossTakings,
            TaxCollected: totals.TaxCollected,
            CashTakings: totals.CashTakings,
            NonCashTakings: totals.NonCashTakings,
            RefundTotal: totals.RefundTotal,
            CashRefunds: totals.CashRefunds,
            OpeningFloat: totals.OpeningFloat,
            CashIn: totals.CashIn,
            CashOut: totals.CashOut,
            NoSaleCount: totals.NoSaleCount,

            // Withheld until the shift is closed. See the type remarks.
            ExpectedCash: isFinal ? totals.ExpectedCash : null,
            ClosingCount: isFinal ? totals.ClosingCount : null,
            Variance: isFinal ? totals.Variance : null);
}
