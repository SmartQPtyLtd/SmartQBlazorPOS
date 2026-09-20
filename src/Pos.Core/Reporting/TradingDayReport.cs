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

/// <summary>One product's contribution over a reporting period.</summary>
/// <param name="Barcode">Product code.</param>
/// <param name="Name">Product name as it was sold.</param>
/// <param name="QuantitySold">Units sold, including fractional weights.</param>
/// <param name="Gross">Revenue including tax.</param>
/// <param name="Tax">Tax collected on those sales.</param>
public readonly record struct ProductSales(
    string Barcode,
    string Name,
    decimal QuantitySold,
    decimal Gross,
    decimal Tax)
{
    /// <summary>Revenue excluding tax.</summary>
    public decimal Net => Gross - Tax;
}

/// <summary>Takings for one hour of the trading day.</summary>
/// <param name="Hour">Hour of the local day, 0 to 23.</param>
/// <param name="SaleCount">Sales completed in that hour.</param>
/// <param name="Gross">Takings in that hour.</param>
public readonly record struct HourlySales(int Hour, int SaleCount, decimal Gross);

/// <summary>
/// A full report for one trading day.
/// </summary>
/// <remarks>
/// Lives in the domain rather than in infrastructure, because it is what a report <em>is</em>
/// — the shape a cashier signs — and because both the reporting service and the printer need
/// to know it without either depending on the other.
/// </remarks>
/// <param name="StoreName">Store the report covers.</param>
/// <param name="BusinessDate">Trading day reported.</param>
/// <param name="IsFinal">
/// True for a Z-report, which closes the day. An X-report is a mid-shift reading and leaves
/// the day open.
/// </param>
/// <param name="GeneratedAt">When the report was produced.</param>
/// <param name="SaleCount">Completed sales.</param>
/// <param name="VoidCount">Sales voided during the day.</param>
/// <param name="VoidedValue">Total value of those voids, reported separately rather than netted off.</param>
/// <param name="Gross">Total taken including tax.</param>
/// <param name="Net">Total excluding tax.</param>
/// <param name="Tax">Tax collected, which is owed onward.</param>
/// <param name="Discounts">Total discount given.</param>
/// <param name="AverageSale">Average value of a completed sale.</param>
/// <param name="TenderedByType">Takings split by payment method, for drawer reconciliation.</param>
/// <param name="TopProducts">Best-selling products, by revenue.</param>
/// <param name="Hourly">Takings by hour of day.</param>
/// <param name="RefundCount">Refunds processed against that day's trading.</param>
/// <param name="RefundTotal">Total handed back to customers.</param>
/// <param name="RefundTax">Tax reversed by those refunds.</param>
public sealed record TradingDayReport(
    string StoreName,
    DateOnly BusinessDate,
    bool IsFinal,
    DateTimeOffset GeneratedAt,
    int SaleCount,
    int VoidCount,
    decimal VoidedValue,
    decimal Gross,
    decimal Net,
    decimal Tax,
    decimal Discounts,
    decimal AverageSale,
    IReadOnlyDictionary<string, decimal> TenderedByType,
    IReadOnlyList<ProductSales> TopProducts,
    IReadOnlyList<HourlySales> Hourly,
    int RefundCount = 0,
    decimal RefundTotal = 0m,
    decimal RefundTax = 0m,
    IReadOnlyList<TaxComponent>? TaxByRate = null,
    IReadOnlyList<OperatorSales>? SalesByOperator = null)
{
    /// <summary>Name of the report as a cashier knows it.</summary>
    public string Title => IsFinal ? "Z-REPORT (day close)" : "X-REPORT (mid-shift)";

    /// <summary>
    /// True when no trading was recorded.
    /// </summary>
    /// <remarks>
    /// A day with only refunds is emphatically not empty — money left the drawer, and a report that
    /// described it as "no trading" would be hiding the one transaction worth looking at.
    /// </remarks>
    public bool IsEmpty => SaleCount == 0 && VoidCount == 0 && RefundCount == 0;

    /// <summary>Total of all tenders, which is what a cashier counts against the drawer.</summary>
    public decimal TenderedTotal => TenderedByType.Values.Sum();

    /// <summary>
    /// Takings after refunds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The figure a shopkeeper means by "what we took today", and the one the estate report uses.
    /// <see cref="Gross"/> is what was rung up; this is what the shop kept.
    /// </para>
    /// <para>
    /// Reported here because its absence was a real inconsistency: the consolidated estate report
    /// subtracted refunds and a shop's own report did not, so the two disagreed about the same
    /// day — and the shop's own number was the one a cashier signs.
    /// </para>
    /// </remarks>
    public decimal NetTakings => Gross - RefundTotal;

    /// <summary>Number of trading days covered. Always one, for a day report.</summary>
    public int TradingDayCount => SaleCount > 0 || VoidCount > 0 || RefundCount > 0 ? 1 : 0;

    /// <summary>
    /// Tax for the day broken down by rate.
    /// </summary>
    /// <remarks>
    /// A single tax total is enough to reconcile the drawer and not enough to fill in a return: a
    /// return wants net and tax per rate, and a shop selling both standard-rated and zero-rated goods
    /// cannot recover the split from the gross figure. Empty rather than null when nothing was sold, so
    /// a caller never has to ask which kind of nothing it is holding.
    /// </remarks>
    public IReadOnlyList<TaxComponent> TaxBreakdown => TaxByRate ?? [];

    /// <summary>
    /// Takings per operator.
    /// </summary>
    /// <remarks>
    /// Each sale records the operator who rang it, because a shift report answers "who was on the
    /// till" and an owner reconciling a shortage wants to know who was standing there. Ordered by
    /// takings, so the shape of the day is visible without reading every row.
    /// </remarks>
    public IReadOnlyList<OperatorSales> ByOperator => SalesByOperator ?? [];
}

/// <summary>
/// One operator's takings for a period.
/// </summary>
/// <param name="EmployeeId">Operator the sales are attributed to, or null when none was signed in.</param>
/// <param name="SaleCount">Completed sales rung by that operator.</param>
/// <param name="Gross">Value of those sales, including tax.</param>
/// <param name="Discounts">Discount that operator gave, which is worth a look on its own.</param>
public readonly record struct OperatorSales(
    string? EmployeeId,
    int SaleCount,
    decimal Gross,
    decimal Discounts);
