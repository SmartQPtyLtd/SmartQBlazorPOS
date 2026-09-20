// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;
using Pos.Core.Reporting;

namespace Pos.Core.Tests;

/// <summary>
/// Tests for the shift cash-up document.
/// </summary>
/// <remarks>
/// The rule under test is the one that decides whether a count means anything: a mid-shift
/// reading must not carry the figure the drawer is expected to hold. A cashier who can see that
/// the till expects 1 240.50 will count to 1 240.50, and the count stops being evidence.
/// </remarks>
public sealed class ShiftReportTests
{
    private static readonly DateTimeOffset Opened = new(2026, 3, 25, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Closed = new(2026, 3, 25, 17, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Generated = new(2026, 3, 25, 17, 30, 5, TimeSpan.Zero);

    private static ShiftTotals Totals(bool isClosed) => new(
        ShiftId: ShiftId.New(),
        EmployeeName: "Thandi Mokoena",
        OpenedAt: Opened,
        ClosedAt: isClosed ? Closed : null,
        SaleCount: 42,
        VoidCount: 1,
        GrossTakings: 4_830.00m,
        TaxCollected: 630.00m,
        CashTakings: 1_380.00m,
        NonCashTakings: 3_450.00m,
        RefundTotal: 120.00m,
        CashRefunds: 120.00m,
        OpeningFloat: 200.00m,
        CashIn: 100.00m,
        CashOut: 500.00m,
        NoSaleCount: 3,
        ExpectedCash: 1_060.00m,
        ClosingCount: isClosed ? 1_047.50m : null,
        Variance: isClosed ? -12.50m : null);

    [Fact]
    public void A_mid_shift_reading_withholds_what_the_drawer_is_expected_to_hold()
    {
        // The whole point of a blind count. Printing the expectation next to a count box turns
        // counting into copying, and the count stops being able to reveal a shortage.
        var report = ShiftReport.From(Totals(isClosed: false), "CORNER STORE", "TILL-1", isFinal: false, Generated);

        Assert.Null(report.ExpectedCash);
        Assert.Null(report.ClosingCount);
        Assert.Null(report.Variance);
    }

    [Fact]
    public void A_mid_shift_reading_still_reports_the_activity()
    {
        // Withholding the drawer figure must not gut the reading. An X-report exists so a
        // supervisor can see the till behaving during the day, so takings, refunds, and the
        // no-sale count are all still there.
        var report = ShiftReport.From(Totals(isClosed: false), "CORNER STORE", "TILL-1", isFinal: false, Generated);

        Assert.Equal(42, report.SaleCount);
        Assert.Equal(4_830.00m, report.GrossTakings);
        Assert.Equal(1_380.00m, report.CashTakings);
        Assert.Equal(3_450.00m, report.NonCashTakings);
        Assert.Equal(3, report.NoSaleCount);
        Assert.False(report.IsFinal);
        Assert.Equal("X-REPORT (mid-shift)", report.Title);
    }

    [Fact]
    public void A_final_report_carries_the_count_and_the_variance()
    {
        var report = ShiftReport.From(Totals(isClosed: true), "CORNER STORE", "TILL-1", isFinal: true, Generated);

        Assert.Equal(1_060.00m, report.ExpectedCash);
        Assert.Equal(1_047.50m, report.ClosingCount);
        Assert.Equal(-12.50m, report.Variance);
        Assert.True(report.IsFinal);
        Assert.Equal("Z-REPORT (shift close)", report.Title);
    }

    [Fact]
    public void A_shortage_is_identifiable_as_a_shortage()
    {
        // The sign carries the meaning, and it is easy to invert when a report is rendered. The
        // document answers the question rather than leaving the reader to work out which
        // direction is bad.
        var report = ShiftReport.From(Totals(isClosed: true), "CORNER STORE", "TILL-1", isFinal: true, Generated);

        Assert.True(report.IsShort);
        Assert.False(report.IsOver);
        Assert.False(report.Balanced);
    }

    [Fact]
    public void A_balanced_drawer_is_reported_as_balanced_rather_than_as_zero_variance()
    {
        var balanced = Totals(isClosed: true) with { ClosingCount = 1_060.00m, Variance = 0m };

        var report = ShiftReport.From(balanced, "CORNER STORE", "TILL-1", isFinal: true, Generated);

        Assert.True(report.Balanced);
        Assert.False(report.IsShort);
        Assert.False(report.IsOver);
    }

    [Fact]
    public void An_over_drawer_is_distinguished_from_a_short_one()
    {
        // Over is not the same problem as short and must not render the same way: an over drawer
        // usually means change was given incorrectly, a short one means cash is missing.
        var over = Totals(isClosed: true) with { ClosingCount = 1_075.00m, Variance = 15.00m };

        var report = ShiftReport.From(over, "CORNER STORE", "TILL-1", isFinal: true, Generated);

        Assert.True(report.IsOver);
        Assert.False(report.IsShort);
    }

    [Fact]
    public void A_shift_with_nothing_recorded_says_so()
    {
        var idle = Totals(isClosed: true) with
        {
            SaleCount = 0,
            VoidCount = 0,
            NoSaleCount = 0,
        };

        var report = ShiftReport.From(idle, "CORNER STORE", "TILL-1", isFinal: true, Generated);

        Assert.True(report.IsEmpty);
    }

    [Fact]
    public void A_no_sale_opening_alone_is_not_an_empty_shift()
    {
        // A drawer that was opened and nothing else is exactly the shift worth looking at, so it
        // must not be filed away as "no activity".
        var quiet = Totals(isClosed: true) with
        {
            SaleCount = 0,
            VoidCount = 0,
            NoSaleCount = 2,
        };

        var report = ShiftReport.From(quiet, "CORNER STORE", "TILL-1", isFinal: true, Generated);

        Assert.False(report.IsEmpty);
    }

    [Fact]
    public void The_operator_is_named_on_the_document()
    {
        // The cash-up exists to put a name against a drawer. A report that does not carry the name
        // cannot do the one thing it is for.
        var report = ShiftReport.From(Totals(isClosed: true), "CORNER STORE", "TILL-1", isFinal: true, Generated);

        Assert.Equal("Thandi Mokoena", report.EmployeeName);
        Assert.Equal("TILL-1", report.TerminalId);
    }
}
