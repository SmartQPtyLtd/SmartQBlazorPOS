// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Reporting;
using Pos.Devices.Reports;

namespace Pos.Devices.Tests;

/// <summary>
/// Report printing tests.
/// </summary>
/// <remarks>
/// A trading report is signed by a cashier and a manager and filed with the takings, so the
/// printed document has to be unambiguous. These pin the two things most easily got wrong: an
/// X-report must not look like a Z-report, and voids must not be quietly folded into the
/// takings.
/// </remarks>
public sealed class ReportRendererTests
{
    private static readonly DateOnly Day = new(2026, 3, 25);

    private static TradingDayReport Report(
        bool isFinal = false,
        int saleCount = 2,
        int voidCount = 0,
        decimal voidedValue = 0m,
        decimal gross = 135.00m,
        decimal tax = 17.61m,
        decimal discounts = 0m,
        decimal averageSale = 67.50m,
        IReadOnlyDictionary<string, decimal>? tenders = null,
        IReadOnlyList<ProductSales>? products = null,
        IReadOnlyList<HourlySales>? hourly = null) => new(
            StoreName: "CORNER STORE",
            BusinessDate: Day,
            IsFinal: isFinal,
            GeneratedAt: new DateTimeOffset(2026, 3, 25, 19, 30, 0, TimeSpan.FromHours(2)),
            SaleCount: saleCount,
            VoidCount: voidCount,
            VoidedValue: voidedValue,
            Gross: gross,
            Net: gross - tax,
            Tax: tax,
            Discounts: discounts,
            AverageSale: averageSale,
            TenderedByType: tenders ?? new Dictionary<string, decimal>(StringComparer.Ordinal) { ["Cash"] = 135.00m },
            TopProducts: products ?? [],
            Hourly: hourly ?? []);

    private static string Render(TradingDayReport report) =>
        string.Join("\n", EscPosParser.Parse(ReportRenderer.Render(report)).Lines);

    [Fact]
    public void A_report_is_headed_and_cut()
    {
        var parsed = EscPosParser.Parse(ReportRenderer.Render(Report()));

        Assert.Equal("Initialise", parsed.Commands[0]);
        Assert.True(parsed.Cut);
        Assert.Contains("X-REPORT", string.Join("\n", parsed.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void A_mid_shift_reading_is_labelled_as_an_X_report()
    {
        var text = Render(Report(isFinal: false));

        Assert.Contains("X-REPORT", text, StringComparison.Ordinal);
        Assert.Contains("Day remains open", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_day_close_is_labelled_as_a_Z_report()
    {
        var text = Render(Report(isFinal: true));

        Assert.Contains("Z-REPORT", text, StringComparison.Ordinal);
        Assert.Contains("Day closed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_X_report_never_claims_the_day_is_closed()
    {
        // A mid-shift reading mistaken for a day close would mean the day is reconciled twice
        // from two different readings.
        var text = Render(Report(isFinal: false));

        Assert.DoesNotContain("Day closed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_key_figures_are_printed()
    {
        var text = Render(Report());

        Assert.Contains("135.00", text, StringComparison.Ordinal); // gross
        Assert.Contains("117.39", text, StringComparison.Ordinal); // net
        Assert.Contains("17.61", text, StringComparison.Ordinal);  // tax
        Assert.Contains("67.50", text, StringComparison.Ordinal);  // average
    }

    [Fact]
    public void The_store_name_and_business_date_are_printed()
    {
        var text = Render(Report());

        Assert.Contains("CORNER STORE", text, StringComparison.Ordinal);
        Assert.Contains("25 March 2026", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Voids_are_printed_separately_and_excluded_from_the_takings()
    {
        // A single netted figure would hide voids, and voids are the number a dishonest till
        // would most want hidden.
        var text = Render(Report(voidCount: 2, voidedValue: 250.00m));

        Assert.Contains("VOIDS", text, StringComparison.Ordinal);
        Assert.Contains("250.00", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_day_with_no_voids_prints_no_void_section()
    {
        var text = Render(Report(voidCount: 0));

        Assert.DoesNotContain("VOIDS", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tender_split_is_printed_with_a_total_to_count_against_the_drawer()
    {
        var tenders = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["Cash"] = 30.00m,
            ["ExternalCard"] = 70.00m,
        };

        var text = Render(Report(tenders: tenders));

        Assert.Contains("Cash", text, StringComparison.Ordinal);
        Assert.Contains("ExternalCard", text, StringComparison.Ordinal);
        Assert.Contains("total", text, StringComparison.Ordinal);
        Assert.Contains("100.00", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Top_sellers_are_listed()
    {
        var products = new List<ProductSales>
        {
            new("6001000000017", "Cola 500ml", 5m, 75.00m, 9.78m),
            new("6001000000031", "White Bread", 2m, 37.98m, 4.95m),
        };

        var text = Render(Report(products: products));

        Assert.Contains("TOP SELLERS", text, StringComparison.Ordinal);
        Assert.Contains("Cola 500ml", text, StringComparison.Ordinal);
        Assert.Contains("5 x Cola", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Weighted_goods_show_their_fractional_quantity_in_the_seller_list()
    {
        var products = new List<ProductSales> { new("333", "Bananas", 0.734m, 16.88m, 2.20m) };

        var text = Render(Report(products: products));

        Assert.Contains("0.734 x Bananas", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Hourly_takings_are_listed_with_their_sale_counts()
    {
        var hourly = new List<HourlySales>
        {
            new(8, 2, 30.00m),
            new(9, 0, 0m),
            new(10, 5, 105.00m),
        };

        var text = Render(Report(hourly: hourly));

        Assert.Contains("BY HOUR", text, StringComparison.Ordinal);
        Assert.Contains("08:00 (2)", text, StringComparison.Ordinal);
        Assert.Contains("09:00 (0)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_day_says_so_rather_than_printing_zeroes_alone()
    {
        var text = Render(Report(saleCount: 0, gross: 0m, tax: 0m, averageSale: 0m));

        Assert.Contains("NO TRADING RECORDED", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Signature_lines_are_printed_because_the_report_is_signed_and_filed()
    {
        var text = Render(Report());

        Assert.Contains("Cashier", text, StringComparison.Ordinal);
        Assert.Contains("Manager", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_printed_line_fits_the_paper_width()
    {
        // A report longer than the paper width wraps mid-figure, which silently corrupts the
        // numbers on a document someone is reconciling against cash.
        var report = Report(
            products: [new("6001000000017", "A very long product name that will not fit", 12m, 9999.99m, 1304.35m)],
            hourly: [new(23, 45, 12345.67m)]);

        var parsed = EscPosParser.Parse(ReportRenderer.Render(report, columns: 48));

        Assert.All(parsed.Lines, line => Assert.True(
            line.Length <= 48,
            $"Line exceeds paper width ({line.Length} > 48): \"{line}\""));
    }

    [Fact]
    public void A_58mm_printer_renders_the_report_within_32_columns()
    {
        var parsed = EscPosParser.Parse(ReportRenderer.Render(Report(), columns: 32));

        Assert.All(parsed.Lines, line => Assert.True(
            line.Length <= 32,
            $"Too wide: \"{line}\""));
    }

    [Fact]
    public void The_report_never_opens_the_cash_drawer()
    {
        // Printing a report at the end of the day must not pop the drawer, which would be both
        // surprising and a security concern.
        var parsed = EscPosParser.Parse(ReportRenderer.Render(Report()));

        Assert.False(parsed.OpenedDrawer);
    }

    [Fact]
    public void A_report_with_no_top_sellers_omits_the_section()
    {
        var text = Render(Report(products: []));

        Assert.DoesNotContain("TOP SELLERS", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_with_no_trading_hours_omits_the_section()
    {
        var text = Render(Report(hourly: []));

        Assert.DoesNotContain("BY HOUR", text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------- refunds

    /// <summary>A closing report with the given refunds against it.</summary>
    private static TradingDayReport WithRefunds(int count, decimal total, decimal tax) => new(
        StoreName: "CORNER STORE",
        BusinessDate: new DateOnly(2026, 3, 25),
        IsFinal: true,
        GeneratedAt: new DateTimeOffset(2026, 3, 25, 18, 0, 0, TimeSpan.Zero),
        SaleCount: 10,
        VoidCount: 0,
        VoidedValue: 0m,
        Gross: 500.00m,
        Net: 434.78m,
        Tax: 65.22m,
        Discounts: 0m,
        AverageSale: 50.00m,
        TenderedByType: new Dictionary<string, decimal>(),
        TopProducts: [],
        Hourly: [],
        RefundCount: count,
        RefundTotal: total,
        RefundTax: tax);

    private static string Amount(decimal value) =>
        value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void A_day_with_refunds_prints_them()
    {
        // The defect this exists for: the signed Z-report showed takings that ignored money handed
        // back, while the estate report subtracted it. The two disagreed about the same day, and
        // the shop's own figure was the wrong one.
        var slip = Render(WithRefunds(count: 3, total: 115.00m, tax: 15.00m));

        Assert.Contains("REFUNDS", slip, StringComparison.Ordinal);
        Assert.Contains("115.00", slip, StringComparison.Ordinal);
    }

    [Fact]
    public void Net_takings_are_stated_on_the_slip()
    {
        // What a shopkeeper means by "what we took today", and the figure the estate report uses.
        var slip = Render(WithRefunds(count: 1, total: 65.22m, tax: 8.51m));

        Assert.Contains("NET TAKINGS", slip, StringComparison.Ordinal);

        // 500 gross less 65.22 refunded.
        Assert.Contains(Amount(434.78m), slip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_day_with_no_refunds_does_not_print_an_empty_refund_block()
    {
        // An empty section reads as a printing fault. "No refunds" is the absence of a line.
        var slip = Render(WithRefunds(count: 0, total: 0m, tax: 0m));

        Assert.DoesNotContain("REFUNDS", slip, StringComparison.Ordinal);
    }

    [Fact]
    public void Refunded_tax_is_kept_apart_from_tax_collected()
    {
        // One is owed onward, the other reclaimed; a net figure would hide both sides.
        var slip = Render(WithRefunds(count: 1, total: 50.00m, tax: 6.52m));

        Assert.Contains("Tax reversed", slip, StringComparison.Ordinal);
        Assert.Contains("6.52", slip, StringComparison.Ordinal);
        Assert.Contains("65.22", slip, StringComparison.Ordinal);
    }
}
