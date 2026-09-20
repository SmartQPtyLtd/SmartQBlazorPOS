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
using Pos.Devices.Reports;

namespace Pos.Devices.Tests;

/// <summary>
/// Tests for the printed shift cash-up.
/// </summary>
/// <remarks>
/// The document is what a cashier and a supervisor both sign and file, so what reaches the paper
/// matters as much as what the domain object holds. These tests assert on the decoded slip rather
/// than on the bytes, because the failure that hurts is a figure the reader can see.
/// </remarks>
public sealed class ShiftReportRendererTests
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

    private static ShiftReport Report(bool isFinal, int columns = 48) =>
        ShiftReport.From(Totals(isFinal), "CORNER STORE", "TILL-1", isFinal, Generated);

    private static string Slip(ShiftReport report, int columns = 48) =>
        string.Join('\n', EscPosParser.Parse(ReportRenderer.RenderShift(report, columns)).Lines);

    /// <summary>
    /// The slip with its line breaks flattened to single spaces.
    /// </summary>
    /// <remarks>
    /// A wrapped sentence is no longer a contiguous substring, so an assertion on the phrase would
    /// fail on a narrow roll for a reason that has nothing to do with what is being tested. What
    /// matters is that every word reached the paper.
    /// </remarks>
    private static string Prose(ShiftReport report, int columns = 48) =>
        string.Join(
            ' ',
            Slip(report, columns)
                .Split(['\n', ' '], StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void A_mid_shift_slip_never_prints_what_the_drawer_should_hold()
    {
        // Enforced in the domain object, asserted here on the paper. This is the check that would
        // fail if someone later rendered ExpectedCash directly and trusted the UI to hide it.
        var slip = Slip(Report(isFinal: false));

        Assert.DoesNotContain("1060.00", slip, StringComparison.Ordinal);
        Assert.DoesNotContain("Expected", slip, StringComparison.Ordinal);
        Assert.DoesNotContain("Counted", slip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mid_shift_slip_says_that_the_shift_is_still_open()
    {
        // Mistaking an X reading for a close would have the drawer reconciled twice from two
        // different readings, and the second reconciliation would look like a discrepancy.
        var slip = Slip(Report(isFinal: false));

        Assert.Contains("X-REPORT", slip, StringComparison.Ordinal);
        Assert.Contains("Shift remains open", slip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_closed_shift_slip_prints_the_count_and_the_variance()
    {
        var slip = Slip(Report(isFinal: true));

        Assert.Contains("Z-REPORT", slip, StringComparison.Ordinal);
        Assert.Contains("1060.00", slip, StringComparison.Ordinal);
        Assert.Contains("1047.50", slip, StringComparison.Ordinal);

        // Labelled rather than left as a signed number: "-12.50" makes the reader work out which
        // direction is bad, and half of them get it wrong.
        Assert.Contains("SHORT", slip, StringComparison.Ordinal);
        Assert.Contains("12.50", slip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shortage_is_not_printed_as_a_negative_number()
    {
        // The direction is carried by the word, so the figure itself is the magnitude of the
        // difference. Printing both would read as a double negative.
        var slip = Slip(Report(isFinal: true));

        Assert.DoesNotContain("-12.50", slip, StringComparison.Ordinal);
    }

    [Fact]
    public void Card_takings_are_printed_separately_from_cash()
    {
        // Card money never entered the drawer. Folding it into one takings figure would make every
        // cash-up look over by the card total — and a number that is always wrong in the same
        // direction is one everyone learns to ignore.
        var slip = Slip(Report(isFinal: true));

        Assert.Contains("1380.00", slip, StringComparison.Ordinal);
        Assert.Contains("3450.00", slip, StringComparison.Ordinal);
        Assert.Contains("cash", slip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_no_sale_count_is_always_printed()
    {
        // Every no-sale opening is the classic cover for a small theft, so the count being visible
        // is the entire reason for recording it. "No-sale openings 0" is information; an absent
        // line is ambiguous.
        var quiet = Report(isFinal: true) with { NoSaleCount = 0 };

        var slip = Slip(quiet);

        Assert.Contains("No-sale openings", slip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_operator_is_named_on_the_slip()
    {
        var slip = Slip(Report(isFinal: true));

        Assert.Contains("Thandi Mokoena", slip, StringComparison.Ordinal);
        Assert.Contains("TILL-1", slip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_slip_carries_signature_lines()
    {
        // It is the document two people sign, so the lines they sign on have to be on it.
        var slip = Slip(Report(isFinal: true));

        Assert.Contains("Operator", slip, StringComparison.Ordinal);
        Assert.Contains("Witnessed", slip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_slip_is_cut_so_it_can_be_filed()
    {
        var parser = EscPosParser.Parse(ReportRenderer.RenderShift(Report(isFinal: true)));

        Assert.True(parser.Cut);
    }

    [Fact]
    public void A_58mm_roll_does_not_truncate_the_footer()
    {
        // 58mm is 32 columns. A footer that reads "Mid-shift reading. Shift rem" is worse than
        // useless on a document whose whole purpose is to say whether the shift is still open.
        var parser = EscPosParser.Parse(ReportRenderer.RenderShift(Report(isFinal: false), columns: 32));

        Assert.All(parser.Lines, line => Assert.True(
            line.Length <= 32,
            $"Line exceeded 32 columns on a 58mm roll: '{line}' ({line.Length})"));

        // Every word survived, which is the difference between wrapping and truncating.
        Assert.Contains("Shift remains open", Prose(Report(isFinal: false), columns: 32), StringComparison.Ordinal);
    }

    [Fact]
    public void A_closed_shift_on_a_58mm_roll_still_names_the_variance()
    {
        var parser = EscPosParser.Parse(ReportRenderer.RenderShift(Report(isFinal: true), columns: 32));

        Assert.All(parser.Lines, line => Assert.True(
            line.Length <= 32,
            $"Line exceeded 32 columns on a 58mm roll: '{line}' ({line.Length})"));

        Assert.Contains("SHORT", string.Join('\n', parser.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void A_shift_with_no_activity_says_so()
    {
        var idle = Report(isFinal: true) with { SaleCount = 0, VoidCount = 0, NoSaleCount = 0 };

        var slip = Slip(idle);

        Assert.Contains("NO ACTIVITY RECORDED", slip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_void_count_is_printed_even_when_it_is_zero()
    {
        // Voids are the number a dishonest till would want hidden, so their absence must not be
        // readable as "none" — it must be stated.
        var clean = Report(isFinal: true) with { VoidCount = 0 };

        Assert.Contains("Voids", Slip(clean), StringComparison.Ordinal);
    }
}
