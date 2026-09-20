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
using Pos.Core.Reporting;
using Pos.Infrastructure.Storage;

namespace Pos.Infrastructure.Reporting;

/// <summary>
/// Builds trading reports from recorded sales.
/// </summary>
/// <remarks>
/// <para>
/// Reads the stored sales rather than a running total, so a report can always be reproduced
/// and audited. A pre-aggregated counter would be faster and would eventually disagree with
/// the sales it claims to summarise.
/// </para>
/// <para>
/// Voided sales are reported <b>separately</b> rather than subtracted. A cash-up needs to show
/// both what was taken and what was reversed; a single netted figure hides voids, which is
/// exactly the number a dishonest till would want hidden.
/// </para>
/// </remarks>
public sealed class TradingReportBuilder(ILocalStore store)
{
    private readonly ILocalStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// Produces a report for one trading day.
    /// </summary>
    /// <param name="storeId">Store to report on.</param>
    /// <param name="storeName">Name to print on the report.</param>
    /// <param name="businessDate">Trading day.</param>
    /// <param name="isFinal">True for a day-closing Z-report.</param>
    /// <param name="topProductCount">How many products to list.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<TradingDayReport> BuildAsync(
        string storeId,
        string storeName,
        DateOnly businessDate,
        bool isFinal = false,
        int topProductCount = 10,
        CancellationToken ct = default)
    {
        var sales = await _store.GetSalesForDateAsync(storeId, businessDate, ct).ConfigureAwait(false);

        // Refunds are money that changed hands, and leaving them out produced a report that
        // disagreed with the estate total about the same day — with the shop's own signed figure
        // being the wrong one.
        var refunds = await _store.GetReturnsForDateAsync(storeId, businessDate, ct).ConfigureAwait(false);

        return Build(storeName, businessDate, sales, isFinal, topProductCount, refunds);
    }

    /// <summary>
    /// Produces a report for a range of days.
    /// </summary>
    /// <param name="storeId">Store to report on.</param>
    /// <param name="storeName">Name to print on the report.</param>
    /// <param name="from">First day, inclusive.</param>
    /// <param name="to">Last day, inclusive.</param>
    /// <param name="topProductCount">How many products to list across the period.</param>
    /// <param name="compareWithPrevious">
    /// True to load the equivalent preceding period for comparison. Reads twice the data, so it is
    /// opt-in rather than always on.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PeriodReport> BuildPeriodAsync(
        string storeId,
        string storeName,
        DateOnly from,
        DateOnly to,
        int topProductCount = 10,
        bool compareWithPrevious = true,
        CancellationToken ct = default)
    {
        if (to < from)
        {
            throw new InvalidOperationException(
                $"The period ends ({to:yyyy-MM-dd}) before it starts ({from:yyyy-MM-dd}).");
        }

        var sales = new List<StoredSale>();
        var refunds = new List<StoredReturn>();

        for (var day = from; day <= to; day = day.AddDays(1))
        {
            sales.AddRange(await _store.GetSalesForDateAsync(storeId, day, ct).ConfigureAwait(false));
            refunds.AddRange(await _store.GetReturnsForDateAsync(storeId, day, ct).ConfigureAwait(false));
        }

        PeriodComparison? previous = null;

        if (compareWithPrevious)
        {
            // The same number of days immediately before, so a 7-day period is compared against a
            // 7-day period. Comparing a week to a month would make every month look like growth.
            var length = to.DayNumber - from.DayNumber + 1;
            var previousTo = from.AddDays(-1);
            var previousFrom = previousTo.AddDays(-(length - 1));

            var previousSales = new List<StoredSale>();
            var previousRefunds = new List<StoredReturn>();

            for (var day = previousFrom; day <= previousTo; day = day.AddDays(1))
            {
                previousSales.AddRange(await _store.GetSalesForDateAsync(storeId, day, ct).ConfigureAwait(false));
                previousRefunds.AddRange(await _store.GetReturnsForDateAsync(storeId, day, ct).ConfigureAwait(false));
            }

            var earlier = BuildPeriod(storeName, previousFrom, previousTo, previousSales, previousRefunds, 0);

            previous = new PeriodComparison(
                previousFrom,
                previousTo,
                earlier.Gross,
                earlier.NetTakings,
                earlier.SaleCount);
        }

        return BuildPeriod(storeName, from, to, sales, refunds, topProductCount, previous);
    }

    /// <summary>
    /// Aggregates a set of sales into a report. Pure, so it is directly testable.
    /// </summary>
    public static TradingDayReport Build(
        string storeName,
        DateOnly businessDate,
        IReadOnlyList<StoredSale> sales,
        bool isFinal = false,
        int topProductCount = 10,
        IReadOnlyList<StoredReturn>? refunds = null)
    {
        ArgumentNullException.ThrowIfNull(sales);

        refunds ??= [];

        // Only completed sales count as takings. A voided sale took no money.
        var completed = sales
            .Where(s => string.Equals(s.Status, "Completed", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var voided = sales
            .Where(s => string.Equals(s.Status, "Voided", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var gross = completed.Sum(s => s.Total);
        var tax = completed.Sum(s => s.TaxTotal);

        var byTender = completed
            .SelectMany(s => s.Tenders)
            .GroupBy(t => t.Type, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Amount), StringComparer.Ordinal);

        return new TradingDayReport(
            StoreName: storeName,
            BusinessDate: businessDate,
            IsFinal: isFinal,
            GeneratedAt: DateTimeOffset.UtcNow,
            SaleCount: completed.Length,
            VoidCount: voided.Length,
            VoidedValue: voided.Sum(s => s.Total),
            Gross: gross,

            // Derived from gross rather than summed from the stored subtotal, because in
            // inclusive tax mode the subtotal is tax-inclusive too. Gross minus tax reconciles
            // in both modes.
            Net: gross - tax,
            Tax: tax,
            Discounts: completed.Sum(s => s.TotalDiscount),
            AverageSale: completed.Length == 0 ? 0m : Math.Round(gross / completed.Length, 2, MidpointRounding.ToEven),
            TenderedByType: byTender,
            TopProducts: AggregateProducts(completed, topProductCount),
            Hourly: AggregateHours(completed),
            RefundCount: refunds.Count,
            RefundTotal: refunds.Sum(r => r.TotalRefund),

            // Kept apart from tax collected: one is owed onward and the other is reclaimed, and a
            // net figure would hide both sides of a return a tax authority expects to see.
            RefundTax: refunds.Sum(r => r.TaxReversed),

            // Summed from the stored lines rather than recomputed from the catalogue: a sale's tax is
            // what was charged on the day, and repricing a completed sale to produce a report is the one
            // thing this system refuses to do anywhere else.
            TaxByRate: AggregateTax(completed),

            SalesByOperator: AggregateOperators(completed));
    }

    /// <summary>
    /// Tax per rate, summed from the lines the sales were actually written with.
    /// </summary>
    /// <remarks>
    /// Grouped by rate <em>and</em> name, because two rates that happen to be equal are still two lines
    /// on a return if they are labelled differently — and a shop that renames its tax mid-year would
    /// otherwise have the two periods silently merged.
    /// </remarks>
    private static TaxComponent[] AggregateTax(IReadOnlyList<StoredSale> sales) =>
        sales
            .SelectMany(s => s.Lines)
            .GroupBy(l => new TaxRate(l.TaxName, l.TaxRate))
            .Select(g => new TaxComponent(g.Key, g.Sum(l => l.TaxableAmount), g.Sum(l => l.TaxAmount)))
            .OrderBy(c => c.Rate.Rate)
            .ThenBy(c => c.Rate.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Takings per operator, largest first.</summary>
    private static OperatorSales[] AggregateOperators(IReadOnlyList<StoredSale> sales) =>
        sales
            .GroupBy(s => s.EmployeeId, StringComparer.Ordinal)
            .Select(g => new OperatorSales(
                g.Key,
                g.Count(),
                g.Sum(s => s.Total),
                g.Sum(s => s.TotalDiscount)))
            .OrderByDescending(o => o.Gross)
            .ThenBy(o => o.EmployeeId, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Aggregates a set of sales and refunds into a period report. Pure, so it is testable.
    /// </summary>
    /// <remarks>
    /// Every calendar day in the range gets an entry, including days with nothing on them. A
    /// seven-day report covering three trading days is a shop that was shut for four of them, and
    /// compressing the list would make it read as a shop that traded every day for less money.
    /// </remarks>
    public static PeriodReport BuildPeriod(
        string storeName,
        DateOnly from,
        DateOnly to,
        IReadOnlyList<StoredSale> sales,
        IReadOnlyList<StoredReturn>? refunds = null,
        int topProductCount = 10,
        PeriodComparison? previous = null)
    {
        ArgumentNullException.ThrowIfNull(sales);

        refunds ??= [];

        var salesByDay = sales
            .Where(s => string.Equals(s.Status, "Completed", StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => s.BusinessDate, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        var voidsByDay = sales
            .Where(s => string.Equals(s.Status, "Voided", StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => s.BusinessDate, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var refundsByDay = refunds
            .GroupBy(r => r.BusinessDate, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        var days = new List<TradingDaySummary>();

        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var daySales = salesByDay.TryGetValue(key, out var found) ? found : [];
            var dayRefunds = refundsByDay.TryGetValue(key, out var foundRefunds) ? foundRefunds : [];

            days.Add(new TradingDaySummary(
                BusinessDate: day,
                SaleCount: daySales.Length,
                VoidCount: voidsByDay.TryGetValue(key, out var voidCount) ? voidCount : 0,
                Gross: daySales.Sum(s => s.Total),
                Tax: daySales.Sum(s => s.TaxTotal),
                RefundCount: dayRefunds.Length,
                RefundTotal: dayRefunds.Sum(r => r.TotalRefund)));
        }

        // Products are aggregated across the whole period, not per day, because a best-seller list
        // is about what the shop sells rather than what it sold on any one Tuesday.
        var completedForProducts = sales
            .Where(s => string.Equals(s.Status, "Completed", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return new PeriodReport(
            StoreName: storeName,
            From: from,
            To: to,
            GeneratedAt: DateTimeOffset.UtcNow,
            Days: days,
            TopProducts: AggregateProducts(completedForProducts, topProductCount),
            Previous: previous);
    }

    /// <summary>
    /// Totals sold quantities and revenue per product.
    /// </summary>
    /// <remarks>
    /// Grouped on barcode rather than product id, so a re-labelled or re-created product still
    /// reports as the same item — which is what a shopkeeper expects to see.
    /// </remarks>
    private static List<ProductSales> AggregateProducts(IReadOnlyList<StoredSale> sales, int take)
    {
        if (take <= 0)
        {
            return [];
        }

        return
        [
            .. sales
                .SelectMany(s => s.Lines)
                .GroupBy(l => l.Barcode, StringComparer.Ordinal)
                .Select(group => new ProductSales(
                    Barcode: group.Key,
                    Name: group.First().Name,
                    QuantitySold: group.Sum(l => l.Quantity),
                    Gross: group.Sum(l => l.TaxableAmount),
                    Tax: group.Sum(l => l.TaxAmount)))
                .OrderByDescending(p => p.Gross)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Take(take),
        ];
    }

    /// <summary>
    /// Buckets takings by the hour recorded at the till, including hours with no trading.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses the hour stored on the sale rather than recomputing it from the timestamp. Deriving
    /// it here would place the same sale in different hours depending on the timezone of the
    /// machine running the report, so two readings of one day could disagree.
    /// </para>
    /// <para>
    /// Empty hours inside the trading window are included deliberately: a gap in the figures is
    /// information, and omitting the row would make a quiet hour indistinguishable from missing
    /// data. Hours outside the window are not printed, so a shop open 08:00-17:00 does not
    /// produce fifteen empty rows.
    /// </para>
    /// </remarks>
    private static List<HourlySales> AggregateHours(IReadOnlyList<StoredSale> sales)
    {
        var buckets = new Dictionary<int, (int Count, decimal Gross)>();

        foreach (var sale in sales)
        {
            var hour = sale.LocalHour;
            var existing = buckets.TryGetValue(hour, out var bucket) ? bucket : (Count: 0, Gross: 0m);
            buckets[hour] = (Count: existing.Count + 1, Gross: existing.Gross + sale.Total);
        }

        if (buckets.Count == 0)
        {
            return [];
        }

        var first = buckets.Keys.Min();
        var last = buckets.Keys.Max();

        return
        [
            .. Enumerable.Range(first, last - first + 1)
                .Select(hour => buckets.TryGetValue(hour, out var bucket)
                    ? new HourlySales(hour, bucket.Count, bucket.Gross)
                    : new HourlySales(hour, 0, 0m)),
        ];
    }
}
