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
using Pos.Infrastructure.Reporting;
using Pos.Infrastructure.Storage;

namespace Pos.Infrastructure.Tests;

/// <summary>
/// Tests for period reporting, and for refunds reaching the day report.
/// </summary>
/// <remarks>
/// A period report answers the question a shopkeeper actually asks — last week, this month — and
/// the rules that make it trustworthy are the ones a single-day report never has to face: days with
/// no trading, and what "up on last week" means when last week took nothing.
/// </remarks>
public sealed class PeriodReportTests
{
    private static readonly DateOnly Monday = new(2026, 3, 23);

    private static StoredSale Sale(
        DateOnly date,
        decimal total,
        decimal tax = 0m,
        string status = "Completed",
        params StoredSaleLine[] lines) => new()
        {
            Id = Guid.CreateVersion7().ToString("N"),
            StoreId = "store-1",
            Number = $"CT01-{date:yyyyMMdd}-0001",
            TerminalId = "TILL-1",
            TerminalSeq = 1,
            CompletedAt = $"{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}T10:00:00.0000000+00:00",
            BusinessDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            LocalHour = 10,
            Currency = "ZAR",
            TaxMode = "Inclusive",
            Status = status,
            Lines = lines,
            Tenders = [new StoredTender { Type = "Cash", Amount = total }],
            Subtotal = total,
            TotalDiscount = 0m,
            TaxTotal = tax,
            Total = total,
        };

    private static StoredReturn Refund(DateOnly date, decimal total, decimal tax = 0m) => new()
    {
        Id = Guid.CreateVersion7().ToString("N"),
        StoreId = "store-1",
        OriginalSaleId = Guid.CreateVersion7().ToString("N"),
        OriginalSaleNumber = "CT01-20260323-0001",
        Number = $"R-CT01-{date:yyyyMMdd}-0001",
        TerminalSeq = 1,
        TerminalId = "TILL-1",
        CompletedAt = $"{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}T11:00:00.0000000+00:00",
        BusinessDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        LocalHour = 11,
        Currency = "ZAR",
        Lines = [],
        Refunds = [new StoredTender { Type = "Cash", Amount = total }],
        Reason = "Faulty",
        TotalRefund = total,
        TaxReversed = tax,
    };

    private static StoredSaleLine Line(string barcode, string name, decimal quantity, decimal taxable) => new()
    {
        ProductId = Guid.CreateVersion7().ToString("N"),
        Barcode = barcode,
        Name = name,
        Quantity = quantity,
        UnitPrice = taxable / (quantity == 0m ? 1m : quantity),
        TaxName = "VAT",
        TaxRate = 0.15m,
        DiscountAmount = 0m,
        TaxableAmount = taxable,
        TaxAmount = 0m,
    };

    // ------------------------------------------------------- refunds in a day report

    [Fact]
    public void Refunds_reach_the_day_report()
    {
        // The defect this exists for: the day report read only sales, so a shop's own signed
        // Z-report showed takings that ignored money handed back — while the estate report
        // subtracted it. The two disagreed about the same day, and the shop's figure was the wrong
        // one.
        var sales = new[] { Sale(Monday, 500.00m, tax: 65.22m) };
        var refunds = new[] { Refund(Monday, 115.00m, tax: 15.00m) };

        var report = TradingReportBuilder.Build("Corner Store", Monday, sales, refunds: refunds);

        Assert.Equal(1, report.RefundCount);
        Assert.Equal(115.00m, report.RefundTotal);
        Assert.Equal(15.00m, report.RefundTax);
    }

    [Fact]
    public void Net_takings_subtract_refunds_while_gross_does_not()
    {
        // A shopkeeper's "what we took" is net; gross is what was rung up. Same rule as the estate
        // report, so the two agree.
        var sales = new[] { Sale(Monday, 500.00m, tax: 65.22m) };
        var refunds = new[] { Refund(Monday, 115.00m, tax: 15.00m) };

        var report = TradingReportBuilder.Build("Corner Store", Monday, sales, refunds: refunds);

        Assert.Equal(500.00m, report.Gross);
        Assert.Equal(385.00m, report.NetTakings);
    }

    [Fact]
    public void Refunded_tax_is_kept_apart_from_tax_collected()
    {
        // One is owed onward; the other is reclaimed. A net figure hides both sides of a return a
        // tax authority expects to see separately.
        var sales = new[] { Sale(Monday, 115.00m, tax: 15.00m) };
        var refunds = new[] { Refund(Monday, 115.00m, tax: 15.00m) };

        var report = TradingReportBuilder.Build("Corner Store", Monday, sales, refunds: refunds);

        Assert.Equal(15.00m, report.Tax);
        Assert.Equal(15.00m, report.RefundTax);
    }

    [Fact]
    public void A_day_with_only_a_refund_is_not_an_empty_day()
    {
        // Money left the drawer. Reporting it as "no trading recorded" would hide the one
        // transaction on the day worth looking at — the same mistake the shift report made with
        // no-sale openings.
        var report = TradingReportBuilder.Build("Corner Store", Monday, [], refunds: [Refund(Monday, 50.00m)]);

        Assert.False(report.IsEmpty);
        Assert.Equal(0, report.SaleCount);
        Assert.Equal(50.00m, report.RefundTotal);
    }

    [Fact]
    public void A_day_report_with_no_refunds_is_unchanged()
    {
        var report = TradingReportBuilder.Build("Corner Store", Monday, [Sale(Monday, 115.00m, tax: 15.00m)]);

        Assert.Equal(0, report.RefundCount);
        Assert.Equal(0m, report.RefundTotal);
        Assert.Equal(115.00m, report.NetTakings);
    }

    [Fact]
    public async Task The_builder_loads_refunds_rather_than_only_sales()
    {
        // The defect itself was in the *loading*, not the aggregation: BuildAsync read only
        // GetSalesForDateAsync, so refunds never reached the report however the aggregation was
        // written. This exercises the same path the reports screen uses.
        var store = new InMemoryLocalStore();
        var salesReturn = Refund(Monday, 115.00m, tax: 15.00m);

        await store.CommitReturnAsync(salesReturn, "{}", [], [], "TILL-1");

        var builder = new TradingReportBuilder(store);

        var report = await builder.BuildAsync("store-1", "Corner Store", Monday);

        Assert.Equal(1, report.RefundCount);
        Assert.Equal(115.00m, report.RefundTotal);
        Assert.Equal(15.00m, report.RefundTax);
    }

    [Fact]
    public async Task The_period_builder_loads_refunds_for_every_day_in_the_range()
    {
        var store = new InMemoryLocalStore();

        await store.CommitReturnAsync(Refund(Monday, 50.00m), "{}", [], [], "TILL-1");
        await store.CommitReturnAsync(Refund(Monday.AddDays(3), 70.00m), "{}", [], [], "TILL-1");

        var builder = new TradingReportBuilder(store);

        var report = await builder.BuildPeriodAsync(
            "store-1", "Corner Store", Monday, Monday.AddDays(6), compareWithPrevious: false);

        Assert.Equal(120.00m, report.RefundTotal);
        Assert.Equal(50.00m, report.Days[0].RefundTotal);
        Assert.Equal(70.00m, report.Days[3].RefundTotal);
        Assert.Equal(0m, report.Days[5].RefundTotal);
    }

    // -------------------------------------------------------------- period totals

    [Fact]
    public void A_period_sums_its_days()
    {
        var sales = new[]
        {
            Sale(Monday, 100.00m, tax: 13.04m),
            Sale(Monday.AddDays(1), 200.00m, tax: 26.09m),
            Sale(Monday.AddDays(2), 300.00m, tax: 39.13m),
        };

        var report = TradingReportBuilder.BuildPeriod(
            "Corner Store", Monday, Monday.AddDays(2), sales);

        Assert.Equal(600.00m, report.Gross);
        Assert.Equal(3, report.SaleCount);
        Assert.Equal(3, report.CalendarDayCount);
        Assert.Equal(3, report.TradingDayCount);
    }

    [Fact]
    public void Every_calendar_day_appears_even_when_nothing_traded()
    {
        // A seven-day report covering three trading days is a shop that was shut for four of them.
        // Compressing the list would make it read as a shop that traded every day for less money.
        var sales = new[] { Sale(Monday, 100.00m), Sale(Monday.AddDays(6), 100.00m) };

        var report = TradingReportBuilder.BuildPeriod(
            "Corner Store", Monday, Monday.AddDays(6), sales);

        Assert.Equal(7, report.Days.Count);
        Assert.Equal(2, report.TradingDayCount);
        Assert.Equal(5, report.IdleDays.Count);
        Assert.True(report.Days[1].IsIdle);
    }

    [Fact]
    public void All_idle_days_are_kept_when_nothing_traded_at_all()
    {
        var report = TradingReportBuilder.BuildPeriod(
            "Corner Store", Monday, Monday.AddDays(3), []);

        Assert.Equal(4, report.Days.Count);
        Assert.All(report.Days, d => Assert.True(d.IsIdle));
        Assert.Equal(0m, report.NetTakings);
        Assert.Null(report.BestDay);
    }

    [Fact]
    public void Period_refunds_reduce_net_takings()
    {
        var sales = new[] { Sale(Monday, 500.00m), Sale(Monday.AddDays(1), 300.00m) };
        var refunds = new[] { Refund(Monday.AddDays(1), 80.00m) };

        var report = TradingReportBuilder.BuildPeriod(
            "Corner Store", Monday, Monday.AddDays(1), sales, refunds);

        Assert.Equal(800.00m, report.Gross);
        Assert.Equal(80.00m, report.RefundTotal);
        Assert.Equal(720.00m, report.NetTakings);
    }

    [Fact]
    public void Voids_are_counted_and_never_netted_off()
    {
        var sales = new[]
        {
            Sale(Monday, 100.00m),
            Sale(Monday, 999.00m, status: "Voided"),
        };

        var report = TradingReportBuilder.BuildPeriod("Corner Store", Monday, Monday, sales);

        Assert.Equal(100.00m, report.Gross);
        Assert.Equal(1, report.VoidCount);
    }

    // --------------------------------------------------------------- averages

    [Fact]
    public void The_two_averages_answer_different_questions()
    {
        // Per trading day is what to staff and order against. Per calendar day is what to compare
        // against rent, which accrues whether the shop is open or not. Confusing them is how a shop
        // talks itself into a bad lease.
        var sales = new[] { Sale(Monday, 700.00m) };

        var report = TradingReportBuilder.BuildPeriod(
            "Corner Store", Monday, Monday.AddDays(6), sales);

        Assert.Equal(700.00m, report.AveragePerTradingDay);
        Assert.Equal(100.00m, report.AveragePerCalendarDay);
    }

    [Fact]
    public void The_averages_are_zero_rather_than_a_division_by_zero()
    {
        var report = TradingReportBuilder.BuildPeriod("Corner Store", Monday, Monday, []);

        Assert.Equal(0m, report.AveragePerTradingDay);
        Assert.Equal(0m, report.AveragePerCalendarDay);
    }

    [Fact]
    public void The_best_day_is_the_best_trading_day()
    {
        var sales = new[]
        {
            Sale(Monday, 100.00m),
            Sale(Monday.AddDays(2), 900.00m),
            Sale(Monday.AddDays(4), 400.00m),
        };

        var report = TradingReportBuilder.BuildPeriod(
            "Corner Store", Monday, Monday.AddDays(4), sales);

        Assert.NotNull(report.BestDay);
        Assert.Equal(Monday.AddDays(2), report.BestDay!.Value.BusinessDate);
    }

    // -------------------------------------------------------------- comparison

    [Fact]
    public void A_comparison_reports_the_change_against_the_equivalent_earlier_period()
    {
        var comparison = new PeriodComparison(
            From: Monday.AddDays(-7),
            To: Monday.AddDays(-1),
            Gross: 500.00m,
            NetTakings: 500.00m,
            SaleCount: 5);

        Assert.Equal(20d, comparison.NetTakingsChangePercent(600.00m));
        Assert.Equal(-10d, comparison.NetTakingsChangePercent(450.00m));
    }

    [Fact]
    public void A_change_against_a_period_that_took_nothing_is_not_a_percentage()
    {
        // "Up 100%" from a period with no trading is arithmetically defensible and completely
        // meaningless. A shop that opened last week has not grown by a number anyone should act on.
        var comparison = new PeriodComparison(Monday.AddDays(-7), Monday.AddDays(-1), 0m, 0m, 0);

        Assert.Null(comparison.NetTakingsChangePercent(600.00m));
    }

    [Fact]
    public void A_period_carries_its_comparison()
    {
        var comparison = new PeriodComparison(Monday.AddDays(-7), Monday.AddDays(-1), 100.00m, 100.00m, 1);

        var report = TradingReportBuilder.BuildPeriod(
            "Corner Store", Monday, Monday, [Sale(Monday, 150.00m)], previous: comparison);

        Assert.NotNull(report.Previous);
        Assert.Equal(50d, report.ChangePercent);
    }

    [Fact]
    public void A_period_with_no_comparison_reports_no_change()
    {
        var report = TradingReportBuilder.BuildPeriod("Corner Store", Monday, Monday, [Sale(Monday, 150.00m)]);

        Assert.Null(report.Previous);
        Assert.Null(report.ChangePercent);
    }

    // ------------------------------------------------------------- top products

    [Fact]
    public void Top_products_are_aggregated_across_the_whole_period()
    {
        // A best-seller list is about what the shop sells, not what it sold on one Tuesday.
        var sales = new[]
        {
            Sale(Monday, 30.00m, lines: Line("6001", "Cola", 2m, 30.00m)),
            Sale(Monday.AddDays(1), 45.00m, lines: Line("6001", "Cola", 3m, 45.00m)),
            Sale(Monday.AddDays(2), 40.00m, lines: Line("6002", "Bread", 2m, 40.00m)),
        };

        var report = TradingReportBuilder.BuildPeriod(
            "Corner Store", Monday, Monday.AddDays(2), sales);

        var top = report.TopProducts[0];

        Assert.Equal("Cola", top.Name);
        Assert.Equal(5m, top.QuantitySold);
        Assert.Equal(75.00m, top.Gross);
    }

    [Fact]
    public async Task A_backwards_period_is_refused_with_an_explanation()
    {
        // Silently producing an empty report would look like a shop that took nothing, which is a
        // very different thing from a mistyped date.
        var builder = new TradingReportBuilder(new InMemoryLocalStore());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await builder.BuildPeriodAsync("store-1", "Corner Store", Monday, Monday.AddDays(-1)));

        Assert.Contains("before it starts", exception.Message, StringComparison.Ordinal);
    }
}
