// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Infrastructure.Reporting;
using Pos.Infrastructure.Storage;

namespace Pos.Infrastructure.Tests;

/// <summary>
/// Trading report tests.
/// </summary>
/// <remarks>
/// A trading report is the document a cashier and a manager both sign, so these pin the
/// properties that make it trustworthy: it reconciles, it does not hide voids, and it is
/// reproducible from the underlying sales.
/// </remarks>
public sealed class TradingReportTests
{
    private static readonly DateOnly Today = new(2026, 3, 25);

    private static StoredSale Sale(
        decimal total,
        decimal tax = 0m,
        string status = "Completed",
        int localHour = 10,
        string tenderType = "Cash",
        decimal? tenderAmount = null,
        string? employeeId = null,
        decimal discount = 0m,
        params StoredSaleLine[] lines) => new()
        {
            Id = Guid.CreateVersion7().ToString("N"),
            StoreId = "store-1",
            Number = "CT01-20260325-0001",
            TerminalId = "TILL-1",
            TerminalSeq = 1,
            CompletedAt = "2026-03-25T10:00:00.0000000+00:00",
            BusinessDate = "2026-03-25",
            LocalHour = localHour,
            Currency = "ZAR",
            TaxMode = "Inclusive",
            Status = status,
            Lines = lines,
            Tenders = [new StoredTender { Type = tenderType, Amount = tenderAmount ?? total }],
            Subtotal = total,
            TotalDiscount = discount,
            TaxTotal = tax,
            Total = total,
            EmployeeId = employeeId,
        };

    private static StoredSaleLine Line(
        string barcode,
        string name,
        decimal quantity,
        decimal taxable,
        decimal tax,
        decimal taxRate = 0.15m,
        string taxName = "VAT") => new()
        {
            ProductId = Guid.CreateVersion7().ToString("N"),
            Barcode = barcode,
            Name = name,
            Quantity = quantity,
            UnitPrice = taxable / (quantity == 0m ? 1m : quantity),
            TaxName = taxName,
            TaxRate = taxRate,
            DiscountAmount = 0m,
            TaxableAmount = taxable,
            TaxAmount = tax,
        };

    [Fact]
    public void A_day_with_no_sales_produces_an_empty_report()
    {
        var report = TradingReportBuilder.Build("Corner Store", Today, []);

        Assert.True(report.IsEmpty);
        Assert.Equal(0, report.SaleCount);
        Assert.Equal(0m, report.Gross);
        Assert.Equal(0m, report.AverageSale);
        Assert.Empty(report.TopProducts);
        Assert.Empty(report.Hourly);
    }

    [Fact]
    public void Takings_are_summed_across_completed_sales()
    {
        var sales = new[]
        {
            Sale(115.00m, tax: 15.00m),
            Sale(20.00m, tax: 2.61m),
        };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        Assert.Equal(2, report.SaleCount);
        Assert.Equal(135.00m, report.Gross);
        Assert.Equal(17.61m, report.Tax);
    }

    [Fact]
    public void Net_is_derived_from_gross_and_tax_so_it_reconciles()
    {
        // In inclusive tax mode the stored subtotal is tax-inclusive too, so net must be
        // derived rather than summed from it.
        var sales = new[] { Sale(115.00m, tax: 15.00m), Sale(20.00m, tax: 2.61m) };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        Assert.Equal(report.Gross - report.Tax, report.Net);
        Assert.Equal(117.39m, report.Net);
    }

    [Fact]
    public void Voided_sales_are_reported_separately_and_not_counted_as_takings()
    {
        // A single netted figure would hide voids, which is precisely the number a dishonest
        // till would want hidden.
        var sales = new[]
        {
            Sale(100.00m, tax: 13.04m),
            Sale(50.00m, tax: 6.52m, status: "Voided"),
        };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        Assert.Equal(1, report.SaleCount);
        Assert.Equal(100.00m, report.Gross);
        Assert.Equal(1, report.VoidCount);
        Assert.Equal(50.00m, report.VoidedValue);
    }

    [Fact]
    public void Voids_do_not_reduce_the_reported_takings()
    {
        // The money was never taken, so subtracting it would understate the day.
        var sales = new[]
        {
            Sale(100.00m, tax: 13.04m),
            Sale(999.00m, status: "Voided"),
        };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        Assert.Equal(100.00m, report.Gross);
    }

    [Fact]
    public void The_average_sale_is_derived_from_completed_sales_only()
    {
        var sales = new[]
        {
            Sale(100.00m, tax: 13.04m),
            Sale(50.00m, tax: 6.52m),
            Sale(999.00m, status: "Voided"),
        };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        Assert.Equal(75.00m, report.AverageSale);
    }

    [Fact]
    public void Tenders_are_split_by_payment_method_for_drawer_reconciliation()
    {
        var sales = new[]
        {
            Sale(30.00m, tenderType: "Cash"),
            Sale(70.00m, tenderType: "ExternalCard"),
        };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        Assert.Equal(30.00m, report.TenderedByType["Cash"]);
        Assert.Equal(70.00m, report.TenderedByType["ExternalCard"]);
        Assert.Equal(100.00m, report.TenderedTotal);
    }

    [Fact]
    public void The_tender_total_matches_the_gross()
    {
        // If these disagree, either a tender was mis-keyed or a sale is unreconciled.
        var sales = new[]
        {
            Sale(115.00m, tax: 15.00m, tenderType: "Cash"),
            Sale(20.00m, tax: 2.61m, tenderType: "ExternalCard"),
        };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        Assert.Equal(report.Gross, report.TenderedTotal);
    }

    [Fact]
    public void Products_are_ranked_by_revenue()
    {
        var sales = new[]
        {
            Sale(100.00m, tax: 13.04m, lines: [Line("111", "Cheap item", 10m, 100.00m, 13.04m)]),
            Sale(300.00m, tax: 39.13m, lines: [Line("222", "Expensive item", 1m, 300.00m, 39.13m)]),
        };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        Assert.Equal("Expensive item", report.TopProducts[0].Name);
        Assert.Equal("Cheap item", report.TopProducts[1].Name);
    }

    [Fact]
    public void The_same_product_sold_on_several_sales_is_aggregated()
    {
        var sales = new[]
        {
            Sale(30.00m, tax: 3.91m, lines: [Line("111", "Cola", 2m, 30.00m, 3.91m)]),
            Sale(45.00m, tax: 5.87m, lines: [Line("111", "Cola", 3m, 45.00m, 5.87m)]),
        };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        var cola = Assert.Single(report.TopProducts);
        Assert.Equal(5m, cola.QuantitySold);
        Assert.Equal(75.00m, cola.Gross);
        Assert.Equal(9.78m, cola.Tax);
    }

    [Fact]
    public void Weighted_goods_keep_their_fractional_quantities_in_the_totals()
    {
        var sales = new[]
        {
            Sale(16.88m, tax: 2.20m, lines: [Line("333", "Bananas", 0.734m, 16.88m, 2.20m)]),
        };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        Assert.Equal(0.734m, report.TopProducts[0].QuantitySold);
    }

    [Fact]
    public void The_top_product_list_is_capped()
    {
        var lines = Enumerable.Range(1, 20)
            .Select(i => Line($"{i:D3}", $"Product {i}", 1m, i * 1.00m, 0m))
            .ToArray();

        var report = TradingReportBuilder.Build("Corner Store", Today, [Sale(210m, lines: lines)], topProductCount: 5);

        Assert.Equal(5, report.TopProducts.Count);
    }

    [Fact]
    public void Takings_are_bucketed_by_the_hour_recorded_at_the_till()
    {
        var sales = new[]
        {
            Sale(10.00m, localHour: 8),
            Sale(20.00m, localHour: 8),
            Sale(30.00m, localHour: 9),
        };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        Assert.Equal(2, report.Hourly.Count);
        Assert.Equal(8, report.Hourly[0].Hour);
        Assert.Equal(2, report.Hourly[0].SaleCount);
        Assert.Equal(30.00m, report.Hourly[0].Gross);
        Assert.Equal(1, report.Hourly[1].SaleCount);
    }

    [Fact]
    public void Quiet_hours_within_the_trading_window_are_reported_as_zero()
    {
        // A gap is information. Omitting the row would make a quiet hour indistinguishable
        // from missing data.
        var sales = new[]
        {
            Sale(10.00m, localHour: 8),
            Sale(30.00m, localHour: 11),
        };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        Assert.Equal(4, report.Hourly.Count); // 08, 09, 10, 11
        Assert.Equal(0m, report.Hourly[1].Gross);
        Assert.Equal(0, report.Hourly[2].SaleCount);
    }

    [Fact]
    public void Hours_outside_the_trading_window_are_not_printed()
    {
        // A shop open 08:00-11:00 should not print thirteen empty rows for the rest of the day.
        var sales = new[]
        {
            Sale(10.00m, localHour: 8),
            Sale(30.00m, localHour: 11),
        };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        Assert.All(report.Hourly, h => Assert.InRange(h.Hour, 8, 11));
    }

    [Fact]
    public void The_hourly_report_does_not_depend_on_the_reader_timezone()
    {
        // The hour is recorded at the till, so the same data must bucket identically no matter
        // where the report is later produced. Deriving the hour from the UTC timestamp would
        // place the same sale in different hours on different machines.
        var sales = new[] { Sale(10.00m, localHour: 3), Sale(20.00m, localHour: 4) };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        Assert.Equal([3, 4], report.Hourly.Select(h => h.Hour));
        Assert.Equal([10.00m, 20.00m], report.Hourly.Select(h => h.Gross));
    }

    [Fact]
    public void Every_recorded_sale_lands_in_an_hour_bucket()
    {
        // Replaces the previous "unparseable timestamp" case: the hour is now recorded rather
        // than parsed, so there is nothing to fail at report time.
        var sales = new[]
        {
            Sale(10.00m, localHour: 0),
            Sale(20.00m, localHour: 23),
        };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        Assert.Equal(30.00m, report.Gross);
        Assert.Equal(24, report.Hourly.Count); // 00 through 23 inclusive
        Assert.Equal(10.00m, report.Hourly[0].Gross);
        Assert.Equal(20.00m, report.Hourly[23].Gross);
    }

    [Fact]
    public void A_mid_shift_reading_is_labelled_as_an_x_report()
    {
        var report = TradingReportBuilder.Build("Corner Store", Today, [Sale(10.00m)]);

        Assert.False(report.IsFinal);
        Assert.Contains("X-REPORT", report.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void A_day_close_is_labelled_as_a_z_report()
    {
        var report = TradingReportBuilder.Build("Corner Store", Today, [Sale(10.00m)], isFinal: true);

        Assert.True(report.IsFinal);
        Assert.Contains("Z-REPORT", report.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_is_reproducible_from_the_same_sales()
    {
        // A report that could differ between two readings of the same data could not be
        // audited, which is the whole point of keeping the sales.
        var sales = new[] { Sale(115.00m, tax: 15.00m), Sale(20.00m, tax: 2.61m) };

        var first = TradingReportBuilder.Build("Corner Store", Today, sales, isFinal: true);
        var second = TradingReportBuilder.Build("Corner Store", Today, sales, isFinal: true);

        Assert.Equal(first.Gross, second.Gross);
        Assert.Equal(first.Tax, second.Tax);
        Assert.Equal(first.SaleCount, second.SaleCount);
        Assert.Equal(
            first.TopProducts.Select(p => p.Barcode),
            second.TopProducts.Select(p => p.Barcode));
    }

    [Fact]
    public void Discounts_given_are_totalled_for_the_report()
    {
        var sale = Sale(90.00m, tax: 11.74m) with { TotalDiscount = 10.00m };

        var report = TradingReportBuilder.Build("Corner Store", Today, [sale]);

        Assert.Equal(10.00m, report.Discounts);
    }

    [Fact]
    public void Sales_with_an_unrecognised_status_are_excluded_from_takings()
    {
        // Failing closed is right here: an unknown status must not silently inflate the day.
        var sales = new[] { Sale(100.00m), Sale(50.00m, status: "SomethingNew") };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        Assert.Equal(1, report.SaleCount);
        Assert.Equal(100.00m, report.Gross);
    }

    // ------------------------------------------------------------------ tax summary

    [Fact]
    public void Tax_is_broken_down_by_rate()
    {
        // The figure a return needs. A shop selling standard-rated and zero-rated goods cannot recover
        // the split from a single tax total, so one line per rate has to come out of the report.
        var sales = new[]
        {
            Sale(115.00m, tax: 15.00m, lines: [Line("6001000000017", "Cola", 1m, 100.00m, 15.00m)]),
            Sale(20.00m, tax: 0m, lines: [Line("6001000000024", "Bread", 1m, 20.00m, 0m, taxRate: 0m, taxName: "Zero")]),
        };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        Assert.Equal(2, report.TaxBreakdown.Count);

        var standard = report.TaxBreakdown.Single(c => c.Rate.Name == "VAT");
        var zero = report.TaxBreakdown.Single(c => c.Rate.Name == "Zero");

        Assert.Equal(100.00m, standard.Net);
        Assert.Equal(15.00m, standard.Tax);
        Assert.Equal(20.00m, zero.Net);
        Assert.Equal(0m, zero.Tax);
    }

    [Fact]
    public void Lines_at_the_same_rate_are_summed_into_one_line()
    {
        var sales = new[]
        {
            Sale(115.00m, tax: 15.00m, lines: [Line("6001000000017", "Cola", 1m, 100.00m, 15.00m)]),
            Sale(230.00m, tax: 30.00m, lines: [Line("6001000000024", "Water", 2m, 200.00m, 30.00m)]),
        };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        var line = Assert.Single(report.TaxBreakdown);

        Assert.Equal(300.00m, line.Net);
        Assert.Equal(45.00m, line.Tax);
    }

    [Fact]
    public void The_tax_breakdown_reconciles_with_the_tax_total()
    {
        // If these disagree, one of them is wrong and nobody can tell which — which is worse than a
        // report that simply omits the breakdown.
        var sales = new[]
        {
            Sale(115.00m, tax: 15.00m, lines: [Line("6001000000017", "Cola", 1m, 100.00m, 15.00m)]),
            Sale(46.00m, tax: 6.00m, lines: [Line("6001000000024", "Crisps", 2m, 40.00m, 6.00m)]),
            Sale(20.00m, tax: 0m, lines: [Line("6001000000031", "Bread", 1m, 20.00m, 0m, taxRate: 0m, taxName: "Zero")]),
        };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        Assert.Equal(report.Tax, report.TaxBreakdown.Sum(c => c.Tax));
        Assert.Equal(report.Net, report.TaxBreakdown.Sum(c => c.Net));
    }

    [Fact]
    public void A_voided_sale_contributes_no_tax()
    {
        // A void took no money and withheld no tax. Including it would overstate what is owed.
        var sales = new[]
        {
            Sale(115.00m, tax: 15.00m, lines: [Line("6001000000017", "Cola", 1m, 100.00m, 15.00m)]),
            Sale(999.00m, tax: 130.30m, status: "Voided", lines: [Line("6001000000024", "Voided", 1m, 868.70m, 130.30m)]),
        };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        var line = Assert.Single(report.TaxBreakdown);

        Assert.Equal(15.00m, line.Tax);
    }

    [Fact]
    public void A_day_with_no_sales_reports_an_empty_breakdown_rather_than_a_null_one()
    {
        // Callers should never have to ask which kind of nothing they are holding.
        var report = TradingReportBuilder.Build("Corner Store", Today, []);

        Assert.Empty(report.TaxBreakdown);
        Assert.Empty(report.ByOperator);
        Assert.Empty(report.TaxByRate!);
        Assert.Empty(report.SalesByOperator!);
    }

    // ------------------------------------------------------------------ by operator

    [Fact]
    public void Takings_are_grouped_by_operator_and_ordered_by_what_they_took()
    {
        // An owner reconciling a shortage wants to know who was on the till, and the shape of the day
        // should be visible without reading every row.
        var sales = new[]
        {
            Sale(100.00m, employeeId: "emp-thandi"),
            Sale(300.00m, employeeId: "emp-sipho"),
            Sale(200.00m, employeeId: "emp-thandi"),
        };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        Assert.Equal(2, report.ByOperator.Count);
        Assert.Equal("emp-sipho", report.ByOperator[0].EmployeeId);
        Assert.Equal(300.00m, report.ByOperator[0].Gross);
        Assert.Equal(1, report.ByOperator[0].SaleCount);
        Assert.Equal("emp-thandi", report.ByOperator[1].EmployeeId);
        Assert.Equal(300.00m, report.ByOperator[1].Gross);
        Assert.Equal(2, report.ByOperator[1].SaleCount);
    }

    [Fact]
    public void Discount_given_is_reported_per_operator()
    {
        // Discount is the one figure on this table somebody has discretion over, so it is the one worth
        // showing next to a name.
        var sales = new[]
        {
            Sale(90.00m, employeeId: "emp-thandi", discount: 10.00m),
            Sale(100.00m, employeeId: "emp-sipho"),
        };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        var thandi = report.ByOperator.Single(o => o.EmployeeId == "emp-thandi");

        Assert.Equal(10.00m, thandi.Discounts);
        Assert.Equal(0m, report.ByOperator.Single(o => o.EmployeeId == "emp-sipho").Discounts);
    }

    [Fact]
    public void Sales_with_no_operator_signed_in_are_reported_as_such_rather_than_dropped()
    {
        // The till can sell with nobody signed in, and a report that quietly omitted those sales would
        // not reconcile with the drawer.
        var sales = new[] { Sale(100.00m, employeeId: "emp-thandi"), Sale(50.00m) };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        Assert.Equal(2, report.ByOperator.Count);
        Assert.Null(report.ByOperator.Single(o => o.EmployeeId is null).EmployeeId);
        Assert.Equal(50.00m, report.ByOperator.Single(o => o.EmployeeId is null).Gross);
    }

    [Fact]
    public void A_voided_sale_is_not_credited_to_the_operator_who_rang_it()
    {
        // It took no money. Counting it against a name would make an operator look busier than the
        // drawer says they were.
        var sales = new[]
        {
            Sale(100.00m, employeeId: "emp-thandi"),
            Sale(500.00m, status: "Voided", employeeId: "emp-thandi"),
        };

        var report = TradingReportBuilder.Build("Corner Store", Today, sales);

        var thandi = Assert.Single(report.ByOperator);

        Assert.Equal(1, thandi.SaleCount);
        Assert.Equal(100.00m, thandi.Gross);
    }
}
