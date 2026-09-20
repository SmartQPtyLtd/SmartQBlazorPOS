// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

namespace Pos.Core.Reporting;

/// <summary>Whether a trading fact is money taken or money given back.</summary>
public enum TradingFactKind
{
    /// <summary>A completed sale.</summary>
    Sale = 0,

    /// <summary>A refund against an earlier sale.</summary>
    Refund = 1,
}

/// <summary>
/// One recorded trading event, reduced to what a consolidated report needs.
/// </summary>
/// <remarks>
/// A deliberately flat projection rather than the stored sale or the domain sale. The hub holds
/// payloads it does not own the shape of, and head-office reporting must keep working when a
/// terminal running an older build pushes a sale with fields the hub has never heard of. Reducing
/// each record to this at the edge is what keeps that promise.
/// </remarks>
/// <param name="StoreId">Store that recorded it.</param>
/// <param name="BusinessDate">Trading day it belongs to.</param>
/// <param name="Kind">Sale or refund.</param>
/// <param name="WasVoided">True when a sale was subsequently voided.</param>
/// <param name="Total">Gross amount, or the amount refunded.</param>
/// <param name="Tax">Tax collected, or tax reversed.</param>
public readonly record struct TradingFact(
    string StoreId,
    DateOnly BusinessDate,
    TradingFactKind Kind,
    bool WasVoided,
    decimal Total,
    decimal Tax);

/// <summary>One store's contribution to a consolidated report.</summary>
/// <param name="StoreId">Store identifier.</param>
/// <param name="StoreCode">Receipt code, e.g. <c>CT01</c>.</param>
/// <param name="StoreName">Store name.</param>
/// <param name="SaleCount">Completed sales, excluding voids.</param>
/// <param name="VoidCount">Sales voided. Counted, never netted off.</param>
/// <param name="VoidedValue">Value of those voids, reported separately.</param>
/// <param name="Gross">Total taken including tax.</param>
/// <param name="Tax">Tax collected on those sales, owed onward.</param>
/// <param name="RefundCount">Refunds processed.</param>
/// <param name="RefundTotal">Total refunded.</param>
/// <param name="RefundTax">Tax reversed by those refunds.</param>
public readonly record struct StoreTradingSummary(
    string StoreId,
    string StoreCode,
    string StoreName,
    int SaleCount,
    int VoidCount,
    decimal VoidedValue,
    decimal Gross,
    decimal Tax,
    int RefundCount,
    decimal RefundTotal,
    decimal RefundTax)
{
    /// <summary>Revenue excluding tax.</summary>
    public decimal Net => Gross - Tax;

    /// <summary>
    /// Takings after refunds.
    /// </summary>
    /// <remarks>
    /// The one place refunds are netted off, and only because this is a revenue figure rather
    /// than a cash-up. Voids are still excluded: a voided sale was reversed before any money
    /// changed hands, so netting it off would misstate both the takings and the void count.
    /// </remarks>
    public decimal NetTakings => Gross - RefundTotal;

    /// <summary>Average value of a completed sale.</summary>
    public decimal AverageSale => SaleCount == 0 ? 0m : decimal.Round(Gross / SaleCount, 2);

    /// <summary>True when the store recorded nothing in the period.</summary>
    public bool IsIdle => SaleCount == 0 && VoidCount == 0 && RefundCount == 0;
}

/// <summary>
/// Takings across every store for a period.
/// </summary>
/// <remarks>
/// <para>
/// The document a franchise owner actually reads: which shops traded, how much, and how much of
/// it is owed onward as tax. Built from the facts the hub holds rather than from each store's own
/// report, so it cannot be affected by a till that never printed its Z-report.
/// </para>
/// <para>
/// <b>Idle stores are kept, not dropped.</b> A store that recorded nothing is a fact worth
/// seeing — it is either closed, or its terminals have stopped syncing, and those need completely
/// different responses from whoever is looking at this. A report that silently omitted it would
/// hide both.
/// </para>
/// </remarks>
/// <param name="From">First trading day covered, inclusive.</param>
/// <param name="To">Last trading day covered, inclusive.</param>
/// <param name="GeneratedAt">When the report was produced.</param>
/// <param name="Stores">One summary per store in the estate, trading or not.</param>
public sealed record ConsolidatedReport(
    DateOnly From,
    DateOnly To,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<StoreTradingSummary> Stores)
{
    /// <summary>Total completed sales across the estate.</summary>
    public int SaleCount => Stores.Sum(s => s.SaleCount);

    /// <summary>Total voided sales across the estate.</summary>
    public int VoidCount => Stores.Sum(s => s.VoidCount);

    /// <summary>Gross takings across the estate.</summary>
    public decimal Gross => Stores.Sum(s => s.Gross);

    /// <summary>Tax collected across the estate, owed onward.</summary>
    public decimal Tax => Stores.Sum(s => s.Tax);

    /// <summary>Total refunded across the estate.</summary>
    public decimal RefundTotal => Stores.Sum(s => s.RefundTotal);

    /// <summary>Takings after refunds.</summary>
    public decimal NetTakings => Gross - RefundTotal;

    /// <summary>Number of days the period covers.</summary>
    public int DayCount => To.DayNumber - From.DayNumber + 1;

    /// <summary>Stores that recorded nothing in the period.</summary>
    public IReadOnlyList<StoreTradingSummary> IdleStores => [.. Stores.Where(s => s.IsIdle)];

    /// <summary>
    /// Stores ranked by takings, highest first.
    /// </summary>
    /// <remarks>
    /// Ties are broken by store code so the same report always comes out in the same order. A
    /// ranking that reshuffles equal stores between runs looks like the figures changed.
    /// </remarks>
    public IReadOnlyList<StoreTradingSummary> RankedByTakings =>
    [
        .. Stores
            .OrderByDescending(s => s.NetTakings)
            .ThenBy(s => s.StoreCode, StringComparer.Ordinal),
    ];
}

/// <summary>
/// Reduces recorded trading facts into a consolidated report.
/// </summary>
/// <remarks>
/// <para>
/// Pure and store-agnostic, so the arithmetic is testable without a hub, a database, or a network.
/// The server's job is only to produce <see cref="TradingFact"/> rows; every decision about what
/// they mean lives here.
/// </para>
/// <para>
/// Every store in the estate gets a row whether or not it traded, so the report answers "which
/// shops are quiet" as readily as "which shops are busy".
/// </para>
/// </remarks>
public static class ConsolidatedReportBuilder
{
    /// <summary>
    /// Builds a report from trading facts.
    /// </summary>
    /// <param name="from">First trading day, inclusive.</param>
    /// <param name="to">Last trading day, inclusive.</param>
    /// <param name="stores">
    /// Every store to report on, as <c>(Id, Code, Name)</c>. Stores absent from this list are not
    /// reported even if facts reference them, because a fact from an unknown store cannot be
    /// attributed to a name.
    /// </param>
    /// <param name="facts">Recorded trading events.</param>
    /// <param name="generatedAt">When the report was produced.</param>
    public static ConsolidatedReport Build(
        DateOnly from,
        DateOnly to,
        IEnumerable<(string Id, string Code, string Name)> stores,
        IEnumerable<TradingFact> facts,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(facts);

        var byStore = facts
            .Where(f => f.BusinessDate >= from && f.BusinessDate <= to)
            .GroupBy(f => f.StoreId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        var summaries = new List<StoreTradingSummary>();

        foreach (var (id, code, name) in stores)
        {
            byStore.TryGetValue(id, out var own);
            own ??= [];

            // A voided sale never took money, so it contributes to the void count and to nothing
            // else. Counting its value as takings and again as a void would double-report it.
            var completed = own.Where(f => f.Kind == TradingFactKind.Sale && !f.WasVoided).ToArray();
            var voided = own.Where(f => f.Kind == TradingFactKind.Sale && f.WasVoided).ToArray();
            var refunds = own.Where(f => f.Kind == TradingFactKind.Refund).ToArray();

            summaries.Add(new StoreTradingSummary(
                StoreId: id,
                StoreCode: code,
                StoreName: name,
                SaleCount: completed.Length,
                VoidCount: voided.Length,
                VoidedValue: voided.Sum(f => f.Total),
                Gross: completed.Sum(f => f.Total),
                Tax: completed.Sum(f => f.Tax),
                RefundCount: refunds.Length,
                RefundTotal: refunds.Sum(f => f.Total),
                RefundTax: refunds.Sum(f => f.Tax)));
        }

        return new ConsolidatedReport(from, to, generatedAt, summaries);
    }
}
