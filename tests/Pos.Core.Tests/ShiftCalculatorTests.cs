// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;

namespace Pos.Core.Tests;

/// <summary>
/// Shift and cash-up tests.
/// </summary>
/// <remarks>
/// A cash-up exists to answer one question: does the drawer hold what it should? These pin the
/// arithmetic that decides it, and in particular that non-cash tenders are excluded — including
/// them would make every shift look over by the card total, which would train everyone to ignore
/// the variance.
/// </remarks>
public sealed class ShiftCalculatorTests
{
    private const string Zar = "ZAR";
    private static readonly TaxRate Vat15 = new("VAT", 0.15m);

    private static Shift NewShift(decimal openingFloat = 500.00m) => new()
    {
        Id = ShiftId.New(),
        StoreId = StoreId.New(),
        EmployeeId = EmployeeId.New(),
        EmployeeName = "Thandi",
        TerminalId = "TILL-1",
        OpenedAt = new DateTimeOffset(2026, 3, 25, 8, 0, 0, TimeSpan.FromHours(2)),
        OpeningFloat = openingFloat,
    };

    private static Sale NewSale(
        decimal total,
        decimal tax = 0m,
        TenderType tenderType = TenderType.Cash,
        string status = "Completed")
    {
        var storeId = StoreId.New();

        return new Sale
        {
            Id = SaleId.New(),
            StoreId = storeId,
            Number = new SaleNumber("CT01", new DateOnly(2026, 3, 25), 1),
            CompletedAt = new DateTimeOffset(2026, 3, 25, 10, 0, 0, TimeSpan.FromHours(2)),
            BusinessDate = new DateOnly(2026, 3, 25),
            Currency = Zar,
            TaxMode = TaxMode.Inclusive,
            Lines = [],
            Tenders = [new Tender(tenderType, new Money(total, Zar))],
            Tax = new TaxCalculation(TaxMode.Inclusive, total - tax, tax, total, []),
            Subtotal = total,
            TotalDiscount = 0m,
            Total = total,
            Status = status == "Completed" ? SaleStatus.Completed : SaleStatus.Voided,
        };
    }

    private static SalesReturn NewReturn(decimal total, TenderType tenderType = TenderType.Cash)
    {
        var saleId = SaleId.New();

        return new SalesReturn
        {
            Id = ReturnId.New(),
            StoreId = StoreId.New(),
            OriginalSaleId = saleId,
            OriginalSaleNumber = new SaleNumber("CT01", new DateOnly(2026, 3, 25), 1),
            Number = "R-CT01-20260325-0001",
            CompletedAt = new DateTimeOffset(2026, 3, 25, 11, 0, 0, TimeSpan.FromHours(2)),
            BusinessDate = new DateOnly(2026, 3, 25),
            Currency = Zar,

            // A line is required: TotalRefund sums the line refunds, so a return built with no
            // lines totals zero however its tenders are set.
            Lines =
            [
                new ReturnLine(
                    ProductId: ProductId.New(),
                    Barcode: "6001000000017",
                    Name: "Cola 500ml",
                    Quantity: 1m,
                    UnitRefund: new Money(total, Zar),
                    TaxRate: Vat15,
                    TaxAmount: 0m),
            ],
            Refunds = [new Tender(tenderType, new Money(total, Zar))],
            Reason = ReturnReason.Faulty,
        };
    }

    private static DrawerEvent NewDrawerEvent(
        DrawerEventType type,
        decimal amount = 0m,
        string? reason = "Counting") => new()
        {
            Id = Guid.CreateVersion7().ToString("N"),
            ShiftId = ShiftId.New(),
            StoreId = StoreId.New(),
            Type = type,
            Amount = amount,
            EmployeeId = EmployeeId.New(),
            OccurredAt = DateTimeOffset.UtcNow,
            Reason = reason,
        };

    private static ShiftActivity Activity(
        Shift? shift = null,
        Sale[]? sales = null,
        SalesReturn[]? returns = null,
        DrawerEvent[]? events = null) => new(
            shift ?? NewShift(),
            sales ?? [],
            returns ?? [],
            events ?? []);

    // -------------------------------------------------------------------------- float

    [Fact]
    public void An_untouched_drawer_should_hold_the_float()
    {
        var totals = ShiftCalculator.Calculate(Activity());

        Assert.Equal(500.00m, totals.ExpectedCash);
        Assert.Equal(0, totals.SaleCount);
    }

    // -------------------------------------------------------------------- cash sales

    [Fact]
    public void Cash_sales_are_added_to_the_expected_drawer()
    {
        var totals = ShiftCalculator.Calculate(Activity(sales: [NewSale(115.00m, tax: 15.00m)]));

        // 500 float + 115 taken in cash.
        Assert.Equal(615.00m, totals.ExpectedCash);
        Assert.Equal(115.00m, totals.CashTakings);
    }

    [Fact]
    public void Card_sales_are_excluded_from_the_expected_drawer()
    {
        // Card money never entered the drawer. Including it would make every shift look over by
        // the card total, which would train everyone to ignore the variance.
        var totals = ShiftCalculator.Calculate(Activity(
            sales: [NewSale(200.00m, tenderType: TenderType.ExternalCard)]));

        Assert.Equal(500.00m, totals.ExpectedCash);
        Assert.Equal(200.00m, totals.NonCashTakings);
        Assert.Equal(0m, totals.CashTakings);
    }

    [Fact]
    public void Takings_and_non_cash_always_sum_to_the_gross()
    {
        var totals = ShiftCalculator.Calculate(Activity(sales:
        [
            NewSale(115.00m),
            NewSale(200.00m, tenderType: TenderType.ExternalCard),
        ]));

        Assert.Equal(totals.GrossTakings, CartLine.Round(totals.CashTakings + totals.NonCashTakings));
    }

    [Fact]
    public void A_split_tender_counts_only_its_cash_portion_in_the_drawer()
    {
        var sale = NewSale(100.00m);
        var split = new Sale
        {
            Id = sale.Id,
            StoreId = sale.StoreId,
            Number = sale.Number,
            CompletedAt = sale.CompletedAt,
            BusinessDate = sale.BusinessDate,
            Currency = sale.Currency,
            TaxMode = sale.TaxMode,
            Lines = [],
            Tenders =
            [
                new Tender(TenderType.Cash, new Money(30.00m, Zar)),
                new Tender(TenderType.ExternalCard, new Money(70.00m, Zar)),
            ],
            Tax = sale.Tax,
            Subtotal = 100.00m,
            TotalDiscount = 0m,
            Total = 100.00m,
        };

        var totals = ShiftCalculator.Calculate(Activity(sales: [split]));

        Assert.Equal(530.00m, totals.ExpectedCash);
        Assert.Equal(30.00m, totals.CashTakings);
        Assert.Equal(70.00m, totals.NonCashTakings);
    }

    // ------------------------------------------------------------------------- voids

    [Fact]
    public void Voided_sales_are_counted_separately_and_take_no_money()
    {
        var totals = ShiftCalculator.Calculate(Activity(sales:
        [
            NewSale(115.00m),
            NewSale(999.00m, status: "Voided"),
        ]));

        Assert.Equal(1, totals.SaleCount);
        Assert.Equal(1, totals.VoidCount);
        Assert.Equal(115.00m, totals.GrossTakings);
        Assert.Equal(615.00m, totals.ExpectedCash);
    }

    // ----------------------------------------------------------------------- refunds

    [Fact]
    public void Cash_refunds_are_removed_from_the_expected_drawer()
    {
        var totals = ShiftCalculator.Calculate(Activity(
            sales: [NewSale(115.00m)],
            returns: [NewReturn(50.00m, TenderType.Cash)]));

        // 500 + 115 - 50.
        Assert.Equal(565.00m, totals.ExpectedCash);
        Assert.Equal(50.00m, totals.CashRefunds);
    }

    [Fact]
    public void Card_refunds_are_excluded_from_the_expected_drawer()
    {
        // The money went back to a card, so the drawer is untouched.
        var totals = ShiftCalculator.Calculate(Activity(
            sales: [NewSale(115.00m)],
            returns: [NewReturn(50.00m, TenderType.ExternalCard)]));

        Assert.Equal(615.00m, totals.ExpectedCash);
        Assert.Equal(0m, totals.CashRefunds);
        Assert.Equal(50.00m, totals.RefundTotal);
    }

    // ------------------------------------------------------------------ cash movements

    [Fact]
    public void Cash_added_to_the_drawer_is_expected_to_be_there()
    {
        var totals = ShiftCalculator.Calculate(Activity(
            events: [NewDrawerEvent(DrawerEventType.CashIn, 200.00m, "Change top-up")]));

        Assert.Equal(700.00m, totals.ExpectedCash);
        Assert.Equal(200.00m, totals.CashIn);
    }

    [Fact]
    public void A_banking_drop_reduces_what_is_expected_in_the_drawer()
    {
        // Cash removed for banking has legitimately left the drawer, so a cash-up that ignored it
        // would report a large false shortage.
        var totals = ShiftCalculator.Calculate(Activity(
            sales: [NewSale(1150.00m)],
            events: [NewDrawerEvent(DrawerEventType.CashOut, 1000.00m, "Banking drop")]));

        Assert.Equal(650.00m, totals.ExpectedCash);
        Assert.Equal(1000.00m, totals.CashOut);
    }

    [Fact]
    public void A_no_sale_opening_does_not_change_the_expected_cash()
    {
        // It moves no money, but it is still recorded: it is the classic cover for a small theft.
        var totals = ShiftCalculator.Calculate(Activity(
            events: [NewDrawerEvent(DrawerEventType.NoSale, 0m, "Customer query")]));

        Assert.Equal(500.00m, totals.ExpectedCash);
        Assert.Equal(1, totals.NoSaleCount);
    }

    [Fact]
    public void No_sale_openings_are_counted()
    {
        var totals = ShiftCalculator.Calculate(Activity(events:
        [
            NewDrawerEvent(DrawerEventType.NoSale, 0m, "Query"),
            NewDrawerEvent(DrawerEventType.NoSale, 0m, "Query"),
            NewDrawerEvent(DrawerEventType.NoSale, 0m, "Refund without slip"),
        ]));

        Assert.Equal(3, totals.NoSaleCount);
    }

    // ------------------------------------------------------------------------ variance

    [Fact]
    public void A_drawer_counting_exactly_to_expectation_balances()
    {
        var shift = NewShift();
        shift.ClosingCount = 615.00m;

        shift.ClosedAt = DateTimeOffset.UtcNow;

        var totals = ShiftCalculator.Calculate(Activity(
            shift,
            sales: [NewSale(115.00m)]));

        Assert.True(totals.Balanced);
        Assert.Equal(0m, totals.Variance);
    }

    [Fact]
    public void A_short_drawer_is_reported_as_short()
    {
        // The number a cash-up exists to produce. Hiding it by forcing it to zero would defeat the
        // whole exercise.
        var shift = NewShift();
        shift.ClosingCount = 600.00m;

        var totals = ShiftCalculator.Calculate(Activity(
            shift,
            sales: [NewSale(115.00m)]));

        Assert.True(totals.IsShort);
        Assert.Equal(-15.00m, totals.Variance);
    }

    [Fact]
    public void An_over_drawer_is_reported_as_over()
    {
        var shift = NewShift();
        shift.ClosingCount = 620.00m;

        var totals = ShiftCalculator.Calculate(Activity(
            shift,
            sales: [NewSale(115.00m)]));

        Assert.True(totals.IsOver);
        Assert.Equal(5.00m, totals.Variance);
    }

    [Fact]
    public void An_open_shift_has_no_variance_yet()
    {
        // Nothing has been counted, so reporting a variance would be inventing a discrepancy.
        var totals = ShiftCalculator.Calculate(Activity(sales: [NewSale(115.00m)]));

        Assert.True(totals.IsOpen);
        Assert.Null(totals.Variance);
        Assert.False(totals.Balanced);
    }

    [Fact]
    public void The_variance_is_the_count_minus_what_was_expected()
    {
        var shift = NewShift();
        shift.ClosingCount = 499.99m;

        var totals = ShiftCalculator.Calculate(Activity(shift));

        Assert.Equal(-0.01m, totals.Variance);
    }

    // ------------------------------------------------------------------- a full shift

    [Fact]
    public void A_full_shift_reconciles_end_to_end()
    {
        // Float 500, cash sales 1150, card sales 200, cash refund 50, banking drop 1000,
        // one no-sale opening. Expected in the drawer: 500 + 1150 - 50 - 1000 = 600.
        var shift = NewShift();
        shift.ClosingCount = 600.00m;

        var totals = ShiftCalculator.Calculate(Activity(
            shift,
            sales:
            [
                NewSale(1150.00m, tax: 150.00m),
                NewSale(200.00m, tax: 26.09m, tenderType: TenderType.ExternalCard),
            ],
            returns: [NewReturn(50.00m, TenderType.Cash)],
            events:
            [
                NewDrawerEvent(DrawerEventType.CashOut, 1000.00m, "Banking drop"),
                NewDrawerEvent(DrawerEventType.NoSale, 0m, "Customer query"),
            ]));

        Assert.Equal(600.00m, totals.ExpectedCash);
        Assert.Equal(600.00m, totals.ClosingCount);
        Assert.True(totals.Balanced);
        Assert.Equal(2, totals.SaleCount);
        Assert.Equal(1350.00m, totals.GrossTakings);
        Assert.Equal(1, totals.NoSaleCount);
    }

    [Fact]
    public void The_same_activity_always_produces_the_same_totals()
    {
        // A cash-up that could differ between two readings could not be audited.
        var shift = NewShift();
        shift.ClosingCount = 615.00m;

        var activity = Activity(shift, sales: [NewSale(115.00m, tax: 15.00m)]);

        var first = ShiftCalculator.Calculate(activity);
        var second = ShiftCalculator.Calculate(activity);

        Assert.Equal(first.ExpectedCash, second.ExpectedCash);
        Assert.Equal(first.Variance, second.Variance);
        Assert.Equal(first.GrossTakings, second.GrossTakings);
    }

    [Fact]
    public void A_shift_with_no_activity_still_reports_its_float()
    {
        var shift = NewShift(openingFloat: 250.00m);
        shift.ClosingCount = 250.00m;

        var totals = ShiftCalculator.Calculate(Activity(shift));

        Assert.Equal(250.00m, totals.ExpectedCash);
        Assert.True(totals.Balanced);
    }

    [Fact]
    public void Tax_collected_is_reported_for_the_tax_return()
    {
        var totals = ShiftCalculator.Calculate(Activity(sales:
        [
            NewSale(115.00m, tax: 15.00m),
            NewSale(230.00m, tax: 30.00m),
        ]));

        Assert.Equal(45.00m, totals.TaxCollected);
    }

    [Fact]
    public void The_operator_is_named_against_the_totals()
    {
        // The point of the shift: a cash-up is answerable to a person, not a terminal.
        var totals = ShiftCalculator.Calculate(Activity());

        Assert.Equal("Thandi", totals.EmployeeName);
    }

    [Fact]
    public void Calculating_from_nothing_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => ShiftCalculator.Calculate(null!));
    }
}

/// <summary>Employee permission tests.</summary>
public sealed class EmployeeTests
{
    private static Employee NewEmployee(EmployeePermissions permissions) => new()
    {
        Id = EmployeeId.New(),
        StoreId = StoreId.New(),
        Name = "Thandi",
        PinHash = "hash",
        Permissions = permissions,
    };

    [Fact]
    public void A_cashier_can_sell()
    {
        Assert.True(NewEmployee(EmployeePermissions.Sell).Can(EmployeePermissions.Sell));
    }

    [Fact]
    public void A_cashier_cannot_refund()
    {
        // Refunds are the most abuse-prone operation, so they are granted separately rather than
        // bundled with selling.
        Assert.False(NewEmployee(EmployeePermissions.Sell).Can(EmployeePermissions.Refund));
    }

    [Fact]
    public void A_supervisor_can_refund_and_void()
    {
        var supervisor = NewEmployee(EmployeePermissions.Supervisor);

        Assert.True(supervisor.Can(EmployeePermissions.Refund));
        Assert.True(supervisor.Can(EmployeePermissions.Void));
        Assert.True(supervisor.Can(EmployeePermissions.OpenDrawer));
    }

    [Fact]
    public void A_supervisor_cannot_change_prices()
    {
        Assert.False(NewEmployee(EmployeePermissions.Supervisor).Can(EmployeePermissions.ManageCatalog));
    }

    [Fact]
    public void A_manager_can_do_everything()
    {
        var manager = NewEmployee(EmployeePermissions.Manager);

        Assert.True(manager.Can(EmployeePermissions.Sell));
        Assert.True(manager.Can(EmployeePermissions.Refund));
        Assert.True(manager.Can(EmployeePermissions.ManageCatalog));
    }

    [Fact]
    public void A_deactivated_employee_can_do_nothing()
    {
        // Someone who has left must not be able to ring up a sale on a shared till.
        var employee = NewEmployee(EmployeePermissions.Manager);
        employee.IsActive = false;

        Assert.False(employee.Can(EmployeePermissions.Sell));
        Assert.False(employee.Can(EmployeePermissions.Refund));
    }

    [Fact]
    public void An_account_with_no_permissions_cannot_sell()
    {
        // Failing closed matters here: a misconfigured account must not be able to take money.
        Assert.False(NewEmployee(EmployeePermissions.None).Can(EmployeePermissions.Sell));
    }

    [Fact]
    public void A_combined_permission_needs_every_part()
    {
        var employee = NewEmployee(EmployeePermissions.Sell | EmployeePermissions.Refund);

        Assert.True(employee.Can(EmployeePermissions.Sell));
        Assert.True(employee.Can(EmployeePermissions.Refund));
        Assert.False(employee.Can(EmployeePermissions.Sell | EmployeePermissions.Void));
    }
}
