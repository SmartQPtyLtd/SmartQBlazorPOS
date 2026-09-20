// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

namespace Pos.Core.Reporting;

/// <summary>One day's figures within a period.</summary>
/// <param name="BusinessDate">Trading day.</param>
/// <param name="SaleCount">Completed sales.</param>
/// <param name="VoidCount">Sales voided. Counted, never netted off.</param>
/// <param name="Gross">Takings before refunds.</param>
/// <param name="Tax">Tax collected on those sales.</param>
/// <param name="RefundCount">Refunds processed.</param>
/// <param name="RefundTotal">Total handed back.</param>
public readonly record struct TradingDaySummary(
    DateOnly BusinessDate,
    int SaleCount,
    int VoidCount,
    decimal Gross,
    decimal Tax,
    int RefundCount,
    decimal RefundTotal)
{
    /// <summary>Takings after refunds.</summary>
    public decimal NetTakings => Gross - RefundTotal;

    /// <summary>True when nothing at all was recorded on this day.</summary>
    public bool IsIdle => SaleCount == 0 && VoidCount == 0 && RefundCount == 0;

    /// <summary>The day of the week, for a reader scanning down the column.</summary>
    public DayOfWeek DayOfWeek => BusinessDate.DayOfWeek;
}

/// <summary>
/// The same period a year or a month earlier, for comparison.
/// </summary>
/// <param name="From">First day of the comparison period.</param>
/// <param name="To">Last day of the comparison period.</param>
/// <param name="Gross">Takings in the comparison period.</param>
/// <param name="NetTakings">Takings after refunds in the comparison period.</param>
/// <param name="SaleCount">Completed sales in the comparison period.</param>
public readonly record struct PeriodComparison(
    DateOnly From,
    DateOnly To,
    decimal Gross,
    decimal NetTakings,
    int SaleCount)
{
    /// <summary>
    /// Change in net takings, as a fraction. Null when there is nothing to compare against.
    /// </summary>
    /// <remarks>
    /// Null rather than zero or infinity when the earlier period took nothing. "Up 100%" from a
    /// period with no trading is arithmetically defensible and completely meaningless — a shop that
    /// opened last week has not grown by any percentage a reader should act on.
    /// </remarks>
    public double? NetTakingsChangePercent(decimal currentNetTakings) =>
        NetTakings == 0m
            ? null
            : (double)((currentNetTakings - NetTakings) / Math.Abs(NetTakings)) * 100d;
}

/// <summary>
/// Trading across a range of days.
/// </summary>
/// <remarks>
/// <para>
/// What a shopkeeper actually asks for: last week, this month, the same week last year. A single
/// day answers "how did we do today", which is a question nobody asks on a Tuesday.
/// </para>
/// <para>
/// <b>Days with no trading are kept.</b> A seven-day report covering three trading days is a shop
/// that was shut for four of them, and compressing the list to three rows would make it look like a
/// shop that traded every day for less money. The gaps are the information.
/// </para>
/// </remarks>
/// <param name="StoreName">Store the period covers.</param>
/// <param name="From">First day, inclusive.</param>
/// <param name="To">Last day, inclusive.</param>
/// <param name="GeneratedAt">When the report was produced.</param>
/// <param name="Days">One entry per calendar day in the period, including idle ones.</param>
/// <param name="TopProducts">Best sellers across the whole period.</param>
/// <param name="Previous">The equivalent immediately preceding period, when there is one.</param>
public sealed record PeriodReport(
    string StoreName,
    DateOnly From,
    DateOnly To,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<TradingDaySummary> Days,
    IReadOnlyList<ProductSales> TopProducts,
    PeriodComparison? Previous = null)
{
    /// <summary>Completed sales across the period.</summary>
    public int SaleCount => Days.Sum(d => d.SaleCount);

    /// <summary>Voided sales across the period.</summary>
    public int VoidCount => Days.Sum(d => d.VoidCount);

    /// <summary>Refunds across the period.</summary>
    public int RefundCount => Days.Sum(d => d.RefundCount);

    /// <summary>Takings before refunds.</summary>
    public decimal Gross => Days.Sum(d => d.Gross);

    /// <summary>Tax collected across the period, owed onward.</summary>
    public decimal Tax => Days.Sum(d => d.Tax);

    /// <summary>Total handed back across the period.</summary>
    public decimal RefundTotal => Days.Sum(d => d.RefundTotal);

    /// <summary>Takings after refunds.</summary>
    public decimal NetTakings => Gross - RefundTotal;

    /// <summary>Days the period covers, trading or not.</summary>
    public int CalendarDayCount => To.DayNumber - From.DayNumber + 1;

    /// <summary>Days on which anything at all was recorded.</summary>
    public int TradingDayCount => Days.Count(d => !d.IsIdle);

    /// <summary>Days on which nothing was recorded.</summary>
    public IReadOnlyList<TradingDaySummary> IdleDays => [.. Days.Where(d => d.IsIdle)];

    /// <summary>
    /// Average takings per day the shop actually traded.
    /// </summary>
    /// <remarks>
    /// The figure to staff and order against. A shop closed on Sundays that averaged a week's
    /// takings over seven days would under-order every week.
    /// </remarks>
    public decimal AveragePerTradingDay =>
        TradingDayCount == 0 ? 0m : decimal.Round(NetTakings / TradingDayCount, 2);

    /// <summary>
    /// Average takings per calendar day in the period.
    /// </summary>
    /// <remarks>
    /// The figure to compare against rent and overheads, which accrue whether the shop is open or
    /// not. Deliberately a different number from <see cref="AveragePerTradingDay"/>, and both are
    /// offered because confusing them is how a shop talks itself into a bad lease.
    /// </remarks>
    public decimal AveragePerCalendarDay =>
        CalendarDayCount == 0 ? 0m : decimal.Round(NetTakings / CalendarDayCount, 2);

    /// <summary>Highest-trading day in the period, or null when nothing traded.</summary>
    public TradingDaySummary? BestDay =>
        Days.Where(d => !d.IsIdle).OrderByDescending(d => d.NetTakings).FirstOrDefault() is { IsIdle: false } best
            ? best
            : null;

    /// <summary>
    /// Change in net takings against the previous period, as a fraction. Null when there is none.
    /// </summary>
    public double? ChangePercent => Previous?.NetTakingsChangePercent(NetTakings);
}
