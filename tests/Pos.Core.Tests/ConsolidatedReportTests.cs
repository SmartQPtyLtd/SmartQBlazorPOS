// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Reporting;

namespace Pos.Core.Tests;

/// <summary>
/// Tests for the estate-wide consolidated report.
/// </summary>
/// <remarks>
/// This is the document a franchise owner reads, and it is the one place where a store's own
/// Z-report and the head-office total can disagree. The rules that keep them in step — voids never
/// netted into takings, refunds netted only from the revenue figure, idle stores kept — are what
/// these tests pin down.
/// </remarks>
public sealed class ConsolidatedReportTests
{
    private static readonly DateOnly Day = new(2026, 3, 25);
    private static readonly DateTimeOffset Generated = new(2026, 3, 25, 18, 0, 0, TimeSpan.Zero);

    private static readonly (string Id, string Code, string Name)[] Estate =
    [
        ("store-a", "CT01", "Cape Town"),
        ("store-b", "JN01", "Johannesburg"),
        ("store-c", "DB01", "Durban"),
    ];

    private static TradingFact Sale(string storeId, decimal total, decimal tax = 0m, bool voided = false,
        DateOnly? date = null) =>
        new(storeId, date ?? Day, TradingFactKind.Sale, voided, total, tax);

    private static TradingFact Refund(string storeId, decimal total, decimal tax = 0m, DateOnly? date = null) =>
        new(storeId, date ?? Day, TradingFactKind.Refund, false, total, tax);

    private static ConsolidatedReport Build(params TradingFact[] facts) =>
        ConsolidatedReportBuilder.Build(Day, Day, Estate, facts, Generated);

    [Fact]
    public void Takings_are_summed_across_every_store()
    {
        var report = Build(
            Sale("store-a", 115.00m, 15.00m),
            Sale("store-a", 230.00m, 30.00m),
            Sale("store-b", 500.00m, 65.22m));

        Assert.Equal(3, report.SaleCount);
        Assert.Equal(845.00m, report.Gross);
        Assert.Equal(110.22m, report.Tax);
    }

    [Fact]
    public void Each_store_gets_its_own_row()
    {
        var report = Build(
            Sale("store-a", 100.00m),
            Sale("store-b", 250.00m),
            Sale("store-b", 50.00m));

        var a = report.Stores.Single(s => s.StoreCode == "CT01");
        var b = report.Stores.Single(s => s.StoreCode == "JN01");

        Assert.Equal(1, a.SaleCount);
        Assert.Equal(100.00m, a.Gross);
        Assert.Equal(2, b.SaleCount);
        Assert.Equal(300.00m, b.Gross);
    }

    [Fact]
    public void A_voided_sale_is_counted_but_never_added_to_takings()
    {
        // A void was reversed before money changed hands. Counting its value as takings and again
        // as a void would report the same money twice, once as income.
        var report = Build(
            Sale("store-a", 115.00m, 15.00m),
            Sale("store-a", 999.00m, 130.30m, voided: true));

        Assert.Equal(1, report.SaleCount);
        Assert.Equal(115.00m, report.Gross);

        Assert.Equal(1, report.VoidCount);
        Assert.Equal(999.00m, report.Stores.Single(s => s.StoreCode == "CT01").VoidedValue);
    }

    [Fact]
    public void Voids_are_never_netted_off_the_takings()
    {
        // The rule a store's own Z-report follows too. A franchise total that disagreed with the
        // shop's own report would be worse than having no total at all.
        var report = Build(Sale("store-a", 100.00m), Sale("store-a", 40.00m, voided: true));

        Assert.Equal(100.00m, report.Gross);
        Assert.Equal(1, report.VoidCount);
    }

    [Fact]
    public void Refunds_reduce_net_takings_but_not_gross()
    {
        // Two figures, because they answer different questions: gross is what was rung up, net is
        // what the estate actually kept.
        var report = Build(Sale("store-a", 500.00m, 65.22m), Refund("store-a", 115.00m, 15.00m));

        Assert.Equal(500.00m, report.Gross);
        Assert.Equal(115.00m, report.RefundTotal);
        Assert.Equal(385.00m, report.NetTakings);
        Assert.Equal(65.22m, report.Tax);
    }

    [Fact]
    public void Tax_is_not_reduced_by_refunded_tax()
    {
        // Tax collected is owed onward, and tax reversed is reclaimed; reporting the net would
        // hide both sides of a return that a tax authority expects to see separately.
        var report = Build(Sale("store-a", 115.00m, 15.00m), Refund("store-a", 115.00m, 15.00m));

        var store = report.Stores.Single(s => s.StoreCode == "CT01");

        Assert.Equal(15.00m, store.Tax);
        Assert.Equal(15.00m, store.RefundTax);
    }

    [Fact]
    public void A_store_that_recorded_nothing_is_still_reported()
    {
        // An idle store is either closed or has stopped syncing, and those need completely
        // different responses. Dropping it from the report would hide both.
        var report = Build(Sale("store-a", 100.00m));

        Assert.Equal(3, report.Stores.Count);

        var durban = report.Stores.Single(s => s.StoreCode == "DB01");
        Assert.True(durban.IsIdle);
        Assert.Equal(0m, durban.Gross);
    }

    [Fact]
    public void Idle_stores_can_be_listed_without_reading_the_whole_report()
    {
        var report = Build(Sale("store-a", 100.00m), Sale("store-b", 50.00m));

        var idle = report.IdleStores;

        Assert.Single(idle);
        Assert.Equal("DB01", idle[0].StoreCode);
    }

    [Fact]
    public void Stores_are_ranked_by_takings()
    {
        var report = Build(
            Sale("store-a", 100.00m),
            Sale("store-b", 900.00m),
            Sale("store-c", 400.00m));

        Assert.Equal(
            ["JN01", "DB01", "CT01"],
            report.RankedByTakings.Select(s => s.StoreCode));
    }

    [Fact]
    public void Ties_are_broken_by_store_code_so_the_order_is_stable()
    {
        // A ranking that reshuffles equal stores between runs looks like the figures changed.
        var report = Build(
            Sale("store-c", 500.00m),
            Sale("store-b", 500.00m),
            Sale("store-a", 500.00m));

        Assert.Equal(
            ["CT01", "DB01", "JN01"],
            report.RankedByTakings.Select(s => s.StoreCode));
    }

    [Fact]
    public void Facts_outside_the_period_are_excluded()
    {
        var report = ConsolidatedReportBuilder.Build(
            Day,
            Day,
            Estate,
            [
                Sale("store-a", 100.00m, date: Day),
                Sale("store-a", 999.00m, date: Day.AddDays(-1)),
                Sale("store-a", 888.00m, date: Day.AddDays(1)),
            ],
            Generated);

        Assert.Equal(100.00m, report.Gross);
    }

    [Fact]
    public void A_fact_for_an_unknown_store_is_not_attributed_to_another_one()
    {
        // Facts arrive from whatever the hub holds. A store that is absent from the register has no
        // name to report against, and guessing would put one shop's takings on another's row.
        var report = Build(Sale("store-a", 100.00m), Sale("store-unknown", 500.00m));

        Assert.Equal(100.00m, report.Gross);
        Assert.Equal(3, report.Stores.Count);
    }

    [Fact]
    public void A_multi_day_period_reports_the_day_count()
    {
        var report = ConsolidatedReportBuilder.Build(
            Day,
            Day.AddDays(6),
            Estate,
            [Sale("store-a", 100.00m, date: Day.AddDays(3))],
            Generated);

        Assert.Equal(7, report.DayCount);
        Assert.Equal(100.00m, report.Gross);
    }

    [Fact]
    public void An_empty_estate_produces_an_empty_report_rather_than_throwing()
    {
        var report = ConsolidatedReportBuilder.Build(Day, Day, [], [], Generated);

        Assert.Empty(report.Stores);
        Assert.Equal(0m, report.Gross);
        Assert.Equal(0m, report.NetTakings);
    }

    [Fact]
    public void The_average_sale_is_zero_rather_than_a_division_by_zero()
    {
        var report = Build(Sale("store-a", 100.00m, voided: true));

        var store = report.Stores.Single(s => s.StoreCode == "CT01");

        Assert.Equal(0, store.SaleCount);
        Assert.Equal(0m, store.AverageSale);
    }

    [Fact]
    public void Net_is_gross_less_tax_for_each_store()
    {
        var report = Build(Sale("store-a", 115.00m, 15.00m));

        var store = report.Stores.Single(s => s.StoreCode == "CT01");

        Assert.Equal(100.00m, store.Net);
    }
}
