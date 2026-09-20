// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;
using Pos.Infrastructure.Checkout;
using Pos.Infrastructure.Security;
using Pos.Infrastructure.Storage;

namespace Pos.Infrastructure.Tests;

/// <summary>
/// Shift lifecycle tests: sign-in, drawer discipline, and the cash-up.
/// </summary>
/// <remarks>
/// This is where cash accountability actually happens, so the tests concentrate on the ways it
/// could be defeated — an unauthenticated operator, two open drawers on one till, a drawer opened
/// without a reason, or a shift closed twice over the count that was signed off.
/// </remarks>
public sealed class ShiftServiceTests
{
    private sealed class FixedTerminal(string id) : ITerminalIdentity
    {
        public string TerminalId { get; } = id;
    }

    /// <summary>A terminal with an in-memory shift store and one enrolled operator.</summary>
    private sealed class Terminal
    {
        public InMemoryShiftStore Store { get; } = new();

        /// <summary>
        /// The terminal's sequence source.
        /// </summary>
        /// <remarks>
        /// The real one: drawer events share the terminal's single ordered stream with sales,
        /// refunds, and stock movements, so the tests exercise the same counter the till uses
        /// rather than a stub that could not collide.
        /// </remarks>
        public InMemoryLocalStore Sequences { get; } = new();

        public ShiftService Shifts { get; }

        public Employee Cashier { get; }

        public Employee Supervisor { get; }

        private const string CashierPin = "7391";
        private const string SupervisorPin = "8256";

        public Terminal(string terminalId = "TILL-1")
        {
            Shifts = new ShiftService(Store, Sequences, new FixedTerminal(terminalId));

            Cashier = NewEmployee("Thandi", EmployeePermissions.Sell, CashierPin);
            Supervisor = NewEmployee("Sipho", EmployeePermissions.Supervisor, SupervisorPin);

            Store.UpsertEmployeesAsync(
            [
                StoredEmployee.FromDomain(Cashier),
                StoredEmployee.FromDomain(Supervisor),
            ]).GetAwaiter().GetResult();
        }

        public static string PinFor(string role) => role switch
        {
            "cashier" => CashierPin,
            "supervisor" => SupervisorPin,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

        private static Employee NewEmployee(string name, EmployeePermissions permissions, string pin)
        {
            var id = EmployeeId.New();

            return new Employee
            {
                Id = id,
                StoreId = StoreId.New(),
                Name = name,
                PinHash = PinHasher.Hash(id, pin),
                Permissions = permissions,
            };
        }

        /// <summary>Signs the cashier in with a default float.</summary>
        public async Task<SignInResult> SignInAsync(decimal openingFloat = 500.00m) =>
            await Shifts.SignInAsync(Cashier.Id.ToString(), CashierPin, openingFloat);
    }

    // ------------------------------------------------------------------------ sign-in

    [Fact]
    public async Task A_correct_pin_signs_the_operator_in_and_opens_a_shift()
    {
        var terminal = new Terminal();

        var result = await terminal.SignInAsync();

        Assert.True(result.OpenedNewShift);
        Assert.Equal(500.00m, result.Shift.OpeningFloat);
        Assert.Equal("Thandi", result.Shift.EmployeeName);
        Assert.True(result.Shift.IsOpen);
    }

    [Fact]
    public async Task A_wrong_pin_is_refused()
    {
        var terminal = new Terminal();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Shifts.SignInAsync(terminal.Cashier.Id.ToString(), "0000", 500.00m));

        Assert.Contains("not recognised", exception.Message);
    }

    [Fact]
    public async Task The_refusal_does_not_reveal_whether_the_account_exists()
    {
        // Both a wrong PIN and a wrong account are the same refusal from the operator's point of
        // view, and distinguishing them helps nobody standing at a till.
        var terminal = new Terminal();

        var wrongPin = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Shifts.SignInAsync(terminal.Cashier.Id.ToString(), "0000", 500.00m));

        var unknownAccount = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Shifts.SignInAsync(Guid.CreateVersion7().ToString(), "7391", 500.00m));

        Assert.Contains("not set up", unknownAccount.Message);
        Assert.Contains("not recognised", wrongPin.Message);
    }

    [Fact]
    public async Task An_inactive_operator_cannot_sign_in()
    {
        var terminal = new Terminal();

        var departed = terminal.Cashier;
        departed.IsActive = false;
        await terminal.Store.UpsertEmployeesAsync([StoredEmployee.FromDomain(departed)]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.SignInAsync());

        Assert.Contains("not active", exception.Message);
    }

    [Fact]
    public async Task A_negative_opening_float_is_refused()
    {
        var terminal = new Terminal();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.SignInAsync(openingFloat: -1m));
    }

    [Fact]
    public async Task Signing_in_again_joins_the_running_shift_rather_than_opening_another()
    {
        // A supervisor may already have started the drawer. Two open shifts on one till would make
        // every cash-up ambiguous.
        var terminal = new Terminal();

        var first = await terminal.SignInAsync();
        var second = await terminal.SignInAsync();

        Assert.True(first.OpenedNewShift);
        Assert.False(second.OpenedNewShift);
        Assert.Equal(first.Shift.Id, second.Shift.Id);
    }

    // ----------------------------------------------------------------- drawer events

    [Fact]
    public async Task Opening_the_drawer_without_a_sale_requires_a_reason()
    {
        // A no-sale opening without a reason is unauditable, and these openings are the classic
        // cover for a small theft.
        var terminal = new Terminal();
        await terminal.SignInAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Shifts.RecordNoSaleAsync(terminal.Cashier, "  "));
    }

    [Fact]
    public async Task A_no_sale_opening_is_recorded_against_the_shift()
    {
        var terminal = new Terminal();
        var signedIn = await terminal.SignInAsync();

        // The supervisor holds OpenDrawer; a plain cashier does not.
        await terminal.Shifts.RecordNoSaleAsync(terminal.Supervisor, "Customer query");

        var totals = await terminal.Shifts.CalculateTotalsAsync(signedIn.Shift.Id);

        Assert.Equal(1, totals.NoSaleCount);
        Assert.Equal(500.00m, totals.ExpectedCash);
    }

    [Fact]
    public async Task An_operator_without_the_permission_cannot_open_the_drawer()
    {
        var terminal = new Terminal();
        await terminal.SignInAsync();

        // The cashier holds Sell only, so must not be able to open the drawer unattended.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Shifts.RecordNoSaleAsync(terminal.Cashier, "Just checking"));

        Assert.Contains("not authorised", exception.Message);
    }

    [Fact]
    public async Task An_operator_without_the_permission_cannot_move_cash_either()
    {
        // Cash movements are the same authority as opening the drawer — both move money without a
        // sale — so a cashier must not reach the same outcome by the other route.
        var terminal = new Terminal();
        await terminal.SignInAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Shifts.RecordCashMovementAsync(
                terminal.Cashier, 50.00m, isCashIn: true, "Sneaky"));

        Assert.Contains("not authorised", exception.Message);
    }

    [Fact]
    public async Task Cash_can_be_added_as_a_change_top_up()
    {
        var terminal = new Terminal();
        var signedIn = await terminal.SignInAsync();

        await terminal.Shifts.RecordCashMovementAsync(
            terminal.Supervisor, 200.00m, isCashIn: true, "Change top-up");

        var totals = await terminal.Shifts.CalculateTotalsAsync(signedIn.Shift.Id);

        Assert.Equal(700.00m, totals.ExpectedCash);
        Assert.Equal(200.00m, totals.CashIn);
    }

    [Fact]
    public async Task A_banking_drop_reduces_what_is_expected()
    {
        var terminal = new Terminal();
        var signedIn = await terminal.SignInAsync();

        await terminal.Shifts.RecordCashMovementAsync(
            terminal.Supervisor, 300.00m, isCashIn: false, "Banking drop");

        var totals = await terminal.Shifts.CalculateTotalsAsync(signedIn.Shift.Id);

        Assert.Equal(200.00m, totals.ExpectedCash);
        Assert.Equal(300.00m, totals.CashOut);
    }

    [Fact]
    public async Task A_cash_movement_must_be_positive()
    {
        // The direction is given by the type, so a negative amount would be ambiguous.
        var terminal = new Terminal();
        await terminal.SignInAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Shifts.RecordCashMovementAsync(
                terminal.Supervisor, -50.00m, isCashIn: true, "Confusing"));
    }

    [Fact]
    public async Task A_cash_movement_requires_a_reason()
    {
        var terminal = new Terminal();
        await terminal.SignInAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Shifts.RecordCashMovementAsync(
                terminal.Supervisor, 50.00m, isCashIn: true, "   "));
    }

    [Fact]
    public async Task Drawer_activity_against_no_open_shift_is_refused()
    {
        var terminal = new Terminal();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Shifts.RecordNoSaleAsync(terminal.Supervisor, "Nobody signed in"));

        Assert.Contains("No shift is open", exception.Message);
    }

    // ------------------------------------------------------------------------ closing

    [Fact]
    public async Task Closing_a_shift_records_the_count_and_produces_a_cash_up()
    {
        var terminal = new Terminal();
        var signedIn = await terminal.SignInAsync();

        var result = await terminal.Shifts.CloseShiftAsync(terminal.Cashier, closingCount: 500.00m);

        Assert.False(result.Shift.IsOpen);
        Assert.Equal(500.00m, result.Totals.ClosingCount);
        Assert.True(result.Totals.Balanced);
    }

    [Fact]
    public async Task Closing_a_shift_twice_is_refused()
    {
        // A second close would overwrite the count that was signed off, destroying the evidence of
        // whatever it showed.
        var terminal = new Terminal();
        await terminal.SignInAsync();
        await terminal.Shifts.CloseShiftAsync(terminal.Cashier, 500.00m);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Shifts.CloseShiftAsync(terminal.Cashier, 999.00m));

        Assert.Contains("No shift is open", exception.Message);
    }

    [Fact]
    public async Task A_shortage_is_reported_at_close()
    {
        var terminal = new Terminal();
        await terminal.SignInAsync();

        var result = await terminal.Shifts.CloseShiftAsync(terminal.Cashier, closingCount: 480.00m);

        Assert.True(result.Totals.IsShort);
        Assert.Equal(-20.00m, result.Totals.Variance);
    }

    [Fact]
    public async Task A_negative_count_is_refused()
    {
        var terminal = new Terminal();
        await terminal.SignInAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Shifts.CloseShiftAsync(terminal.Cashier, closingCount: -1m));
    }

    [Fact]
    public async Task A_cashier_cannot_close_another_operators_shift()
    {
        // Closing someone else's drawer is a supervisory act.
        var terminal = new Terminal();
        await terminal.SignInAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Shifts.CloseShiftAsync(
                terminal.Supervisor, 500.00m, countedByEmployeeId: null));

        // A supervisor has the permission, so the refusal must be about naming the counter.
        Assert.Contains("who counted", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_supervisor_closing_another_operators_shift_is_recorded_against_them()
    {
        var terminal = new Terminal();
        var signedIn = await terminal.SignInAsync();

        var result = await terminal.Shifts.CloseShiftAsync(
            terminal.Supervisor,
            closingCount: 495.00m,
            note: "Cashier went home",
            countedByEmployeeId: terminal.Supervisor.Id.ToString());

        Assert.Equal(terminal.Supervisor.Id.ToString(), result.Shift.ClosedByEmployeeId);
        Assert.Equal(-5.00m, result.Totals.Variance);
        Assert.False(result.Shift.IsOpen);

        _ = signedIn;
    }

    [Fact]
    public async Task A_closed_shift_accepts_no_further_drawer_activity()
    {
        // Late activity would change a cash-up that has already been signed off. The store refuses
        // it, so no caller can bypass the rule.
        var terminal = new Terminal();
        var signedIn = await terminal.SignInAsync();
        await terminal.Shifts.CloseShiftAsync(terminal.Cashier, 500.00m);

        var late = StoredDrawerEvent.FromDomain(
            new DrawerEvent
            {
                Id = Guid.CreateVersion7().ToString("N"),
                ShiftId = new ShiftId(Guid.Parse(signedIn.Shift.Id)),
                StoreId = new StoreId(Guid.Parse(signedIn.Shift.StoreId)),
                Type = DrawerEventType.NoSale,
                EmployeeId = terminal.Cashier.Id,
                OccurredAt = DateTimeOffset.UtcNow,
                Reason = "Too late",
            },
            "TILL-1",
            99);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Store.RecordDrawerEventAsync(late, "{}", "TILL-1"));

        Assert.Contains("closed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Two_open_drawers_on_one_terminal_are_refused()
    {
        // Two open shifts on one till would make every cash-up ambiguous.
        var terminal = new Terminal();
        var signedIn = await terminal.SignInAsync();

        var second = StoredShift.FromDomain(
            new Shift
            {
                Id = ShiftId.New(),
                StoreId = new StoreId(Guid.Parse(signedIn.Shift.StoreId)),
                EmployeeId = terminal.Supervisor.Id,
                EmployeeName = "Sipho",
                TerminalId = "TILL-1",
                OpenedAt = DateTimeOffset.UtcNow,
                OpeningFloat = 100.00m,
            },
            50);

        var opening = StoredDrawerEvent.FromDomain(
            new DrawerEvent
            {
                Id = Guid.CreateVersion7().ToString("N"),
                ShiftId = new ShiftId(Guid.Parse(second.Id)),
                StoreId = new StoreId(Guid.Parse(second.StoreId)),
                Type = DrawerEventType.ShiftOpened,
                Amount = 100.00m,
                EmployeeId = terminal.Supervisor.Id,
                OccurredAt = DateTimeOffset.UtcNow,
                Reason = "Second float",
            },
            "TILL-1",
            51);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Store.OpenShiftAsync(second, "{}", opening, "{}", "TILL-1"));

        Assert.Contains("already open", exception.Message);
    }

    [Fact]
    public async Task Signing_in_after_a_close_opens_a_fresh_shift()
    {
        var terminal = new Terminal();
        var first = await terminal.SignInAsync();
        await terminal.Shifts.CloseShiftAsync(terminal.Cashier, 500.00m);

        var second = await terminal.SignInAsync(openingFloat: 300.00m);

        Assert.True(second.OpenedNewShift);
        Assert.NotEqual(first.Shift.Id, second.Shift.Id);
        Assert.Equal(300.00m, second.Shift.OpeningFloat);
    }

    // ---------------------------------------------------------------------- cash-up

    [Fact]
    public async Task The_cash_up_includes_sales_attached_to_the_shift()
    {
        var terminal = new Terminal();
        var signedIn = await terminal.SignInAsync();

        terminal.Store.AttachSale(NewStoredSale(signedIn.Shift, 115.00m, tax: 15.00m));

        var totals = await terminal.Shifts.CalculateTotalsAsync(signedIn.Shift.Id);

        Assert.Equal(1, totals.SaleCount);
        Assert.Equal(615.00m, totals.ExpectedCash);
        Assert.Equal(15.00m, totals.TaxCollected);
    }

    [Fact]
    public async Task Card_sales_do_not_change_the_expected_drawer()
    {
        var terminal = new Terminal();
        var signedIn = await terminal.SignInAsync();

        terminal.Store.AttachSale(NewStoredSale(signedIn.Shift, 200.00m, tenderType: "ExternalCard"));

        var totals = await terminal.Shifts.CalculateTotalsAsync(signedIn.Shift.Id);

        Assert.Equal(500.00m, totals.ExpectedCash);
        Assert.Equal(200.00m, totals.NonCashTakings);
    }

    [Fact]
    public async Task Cash_refunds_attached_to_the_shift_reduce_the_expected_drawer()
    {
        var terminal = new Terminal();
        var signedIn = await terminal.SignInAsync();

        terminal.Store.AttachSale(NewStoredSale(signedIn.Shift, 115.00m));
        terminal.Store.AttachReturn(NewStoredReturn(signedIn.Shift, 50.00m));

        var totals = await terminal.Shifts.CalculateTotalsAsync(signedIn.Shift.Id);

        Assert.Equal(565.00m, totals.ExpectedCash);
        Assert.Equal(50.00m, totals.CashRefunds);
    }

    [Fact]
    public async Task Sales_from_another_shift_are_not_counted()
    {
        // The property that makes a cash-up answerable to one person rather than the whole day.
        var terminal = new Terminal();
        var first = await terminal.SignInAsync();
        await terminal.Shifts.CloseShiftAsync(terminal.Cashier, 500.00m);

        var second = await terminal.SignInAsync();

        terminal.Store.AttachSale(NewStoredSale(first.Shift, 115.00m));

        var totals = await terminal.Shifts.CalculateTotalsAsync(second.Shift.Id);

        Assert.Equal(0, totals.SaleCount);
        Assert.Equal(500.00m, totals.ExpectedCash);
    }

    [Fact]
    public async Task The_same_shift_always_calculates_to_the_same_totals()
    {
        // A cash-up that could differ between two readings could not be audited.
        var terminal = new Terminal();
        var signedIn = await terminal.SignInAsync();

        terminal.Store.AttachSale(NewStoredSale(signedIn.Shift, 115.00m, tax: 15.00m));
        await terminal.Shifts.RecordNoSaleAsync(terminal.Supervisor, "Query");

        var first = await terminal.Shifts.CalculateTotalsAsync(signedIn.Shift.Id);
        var second = await terminal.Shifts.CalculateTotalsAsync(signedIn.Shift.Id);

        Assert.Equal(first.ExpectedCash, second.ExpectedCash);
        Assert.Equal(first.NoSaleCount, second.NoSaleCount);
        Assert.Equal(first.GrossTakings, second.GrossTakings);
    }

    // ------------------------------------------------------------------------ helpers

    private static StoredSale NewStoredSale(
        StoredShift shift,
        decimal total,
        decimal tax = 0m,
        string tenderType = "Cash") => new()
        {
            Id = Guid.CreateVersion7().ToString("N"),
            StoreId = shift.StoreId,
            Number = "CT01-20260325-0001",
            TerminalId = shift.TerminalId,
            TerminalSeq = 10,
            CompletedAt = DateTimeOffset.UtcNow.ToString("O"),
            BusinessDate = "2026-03-25",
            LocalHour = 10,
            Currency = "ZAR",
            TaxMode = "Inclusive",
            Status = "Completed",
            Lines = [],
            Tenders = [new StoredTender { Type = tenderType, Amount = total }],
            Subtotal = total,
            TotalDiscount = 0m,
            TaxTotal = tax,
            Total = total,

            // The link the cash-up depends on.
            ShiftId = shift.Id,
        };

    private static StoredReturn NewStoredReturn(StoredShift shift, decimal total) => new()
    {
        Id = Guid.CreateVersion7().ToString("N"),
        StoreId = shift.StoreId,
        OriginalSaleId = Guid.CreateVersion7().ToString("N"),
        OriginalSaleNumber = "CT01-20260325-0001",
        Number = "R-CT01-20260325-0001",
        TerminalId = shift.TerminalId,
        TerminalSeq = 20,
        CompletedAt = DateTimeOffset.UtcNow.ToString("O"),
        BusinessDate = "2026-03-25",
        LocalHour = 11,
        Currency = "ZAR",
        Lines =
        [
            new StoredReturnLine
            {
                ProductId = Guid.CreateVersion7().ToString("N"),
                Barcode = "6001000000017",
                Name = "Cola",
                Quantity = 1m,
                UnitRefund = total,
                TaxName = "VAT",
                TaxRate = 0.15m,
                TaxAmount = 0m,
            },
        ],
        Refunds = [new StoredTender { Type = "Cash", Amount = total }],
        Reason = "Faulty",
        TotalRefund = total,
        TaxReversed = 0m,
        ShiftId = shift.Id,
    };

    // ------------------------------------------------------- a sale inside a real shift

    /// <summary>
    /// A terminal whose checkout and shift share one durable store, as the browser's does.
    /// </summary>
    private sealed class TradingTerminal
    {
        private readonly Store _store = new()
        {
            Id = StoreId.New(),
            Name = "CORNER STORE",
            Code = "CT01",
            Currency = "ZAR",
            TaxMode = TaxMode.Inclusive,
            DefaultTaxRate = new TaxRate("VAT", 0.15m),
            ReceiptColumns = 48,
        };

        public InMemoryLocalStore Local { get; } = new();

        /// <summary>Linked to the ledger, as the browser's single database effectively is.</summary>
        public InMemoryShiftStore Shifts { get; }

        public CheckoutRecordingService Checkout { get; }

        public ShiftService ShiftService { get; }

        public Employee Cashier { get; }

        private const string CashierPin = "7391";

        public TradingTerminal()
        {
            Shifts = new InMemoryShiftStore(Local);

            var terminal = new FixedTerminal("TILL-1");

            Checkout = new CheckoutRecordingService(Local, new LocalSaleNumberSource(Local), terminal);
            ShiftService = new ShiftService(Shifts, Local, terminal);

            var id = EmployeeId.New();
            Cashier = new Employee
            {
                Id = id,
                StoreId = _store.Id,
                Name = "Thandi",
                PinHash = PinHasher.Hash(id, CashierPin),
                Permissions = EmployeePermissions.Sell | EmployeePermissions.OpenDrawer,
            };

            Shifts.UpsertEmployeesAsync([StoredEmployee.FromDomain(Cashier)]).GetAwaiter().GetResult();
        }

        public async Task<SignInResult> SignInAsync(decimal openingFloat = 500.00m) =>
            await ShiftService.SignInAsync(Cashier.Id.ToString(), CashierPin, openingFloat);

        /// <summary>Rings a cash sale against the given shift, exactly as the till does.</summary>
        public async Task<CompletedSale> SellAsync(StoredShift shift, decimal price, TenderType tender = TenderType.Cash)
        {
            var cart = new Cart(_store.Id, _store.Currency, _store.TaxMode);
            cart.AddProduct(
                new Product
                {
                    Id = ProductId.New(),
                    StoreId = _store.Id,
                    Barcode = "6001000000017",
                    Name = "Cola 500ml",
                    UnitPrice = new Money(price, _store.Currency),
                    TaxRate = _store.DefaultTaxRate,
                });

            return await Checkout.RecordSaleAsync(
                cart,
                [new Tender(tender, new Money(price, _store.Currency))],
                _store,
                employeeId: Cashier.Id.ToString(),
                shiftId: shift.Id);
        }

        private sealed class FixedTerminal(string id) : ITerminalIdentity
        {
            public string TerminalId { get; } = id;
        }
    }

    [Fact]
    public async Task A_real_sale_lands_in_the_cash_up_for_its_shift()
    {
        // The unit tests above attach a sale to a shift by hand. This one rings it through the
        // actual checkout path, which is the only way to catch the two services disagreeing about
        // which shift a sale belongs to — the failure that would make every cash-up wrong while
        // every individual test stayed green.
        var terminal = new TradingTerminal();
        var signedIn = await terminal.SignInAsync(openingFloat: 500.00m);

        await terminal.SellAsync(signedIn.Shift, 115.00m);

        var totals = await terminal.ShiftService.CalculateTotalsAsync(signedIn.Shift.Id);

        Assert.Equal(1, totals.SaleCount);
        Assert.Equal(115.00m, totals.CashTakings);

        // 500 float + 115 taken.
        Assert.Equal(615.00m, totals.ExpectedCash);
    }

    [Fact]
    public async Task A_card_sale_does_not_move_the_expected_drawer()
    {
        var terminal = new TradingTerminal();
        var signedIn = await terminal.SignInAsync(openingFloat: 500.00m);

        await terminal.SellAsync(signedIn.Shift, 200.00m, TenderType.ExternalCard);

        var totals = await terminal.ShiftService.CalculateTotalsAsync(signedIn.Shift.Id);

        Assert.Equal(200.00m, totals.NonCashTakings);
        Assert.Equal(0m, totals.CashTakings);
        Assert.Equal(500.00m, totals.ExpectedCash);
    }

    [Fact]
    public async Task Closing_a_shift_records_the_variance_against_the_real_takings()
    {
        var terminal = new TradingTerminal();
        var signedIn = await terminal.SignInAsync(openingFloat: 500.00m);

        await terminal.SellAsync(signedIn.Shift, 115.00m);

        // 615 expected, 610 counted: five rand short.
        var result = await terminal.ShiftService.CloseShiftAsync(terminal.Cashier, 610.00m);

        Assert.Equal(615.00m, result.Totals.ExpectedCash);
        Assert.Equal(610.00m, result.Totals.ClosingCount);
        Assert.Equal(-5.00m, result.Totals.Variance);
        Assert.True(result.Totals.IsShort);
    }

    [Fact]
    public async Task A_sale_after_the_shift_closed_is_not_counted_in_it()
    {
        // Closing is final. A sale recorded afterwards that still carried the closed shift's id
        // would change a cash-up that has already been signed off, which is the one thing the
        // close is supposed to prevent.
        var terminal = new TradingTerminal();
        var signedIn = await terminal.SignInAsync(openingFloat: 500.00m);

        await terminal.SellAsync(signedIn.Shift, 115.00m);
        await terminal.ShiftService.CloseShiftAsync(terminal.Cashier, 615.00m);

        var after = await terminal.ShiftService.CalculateTotalsAsync(signedIn.Shift.Id);

        Assert.Equal(1, after.SaleCount);
        Assert.Equal(615.00m, after.ExpectedCash);
        Assert.False(after.IsOpen);
    }

    [Fact]
    public async Task A_signed_in_operator_survives_a_refresh_with_the_same_drawer()
    {
        // A refresh is the most ordinary event in a browser. If the shift did not come back, the
        // next sale would record no shift and the cash-up would come up short by exactly the
        // takings since the reload, with nothing to explain the difference.
        var terminal = new TradingTerminal();
        var first = await terminal.SignInAsync(openingFloat: 500.00m);

        // A second ShiftService over the same storage is what a page reload produces.
        var reloaded = new ShiftService(terminal.Shifts, terminal.Local, new FixedTerminal("TILL-1"));

        var stillOpen = await reloaded.GetOpenShiftAsync();

        Assert.NotNull(stillOpen);
        Assert.Equal(first.Shift.Id, stillOpen.Id);

        var totals = await reloaded.CalculateTotalsAsync(stillOpen.Id);
        Assert.Equal(500.00m, totals.OpeningFloat);
    }
}

/// <summary>Operator PIN hashing tests.</summary>
public sealed class PinHasherTests
{
    [Fact]
    public void A_pin_verifies_against_its_own_hash()
    {
        var id = EmployeeId.New();

        Assert.True(PinHasher.Verify(id, "7391", PinHasher.Hash(id, "7391")));
    }

    [Fact]
    public void A_wrong_pin_does_not_verify()
    {
        var id = EmployeeId.New();
        var hash = PinHasher.Hash(id, "7391");

        Assert.False(PinHasher.Verify(id, "7392", hash));
        Assert.False(PinHasher.Verify(id, "0000", hash));
    }

    [Fact]
    public void The_same_pin_produces_different_hashes_for_different_employees()
    {
        // Staff routinely share a PIN like 1234. Without per-employee salting, one leaked hash
        // would reveal that several employees use the same code.
        var first = PinHasher.Hash(EmployeeId.New(), "7391");
        var second = PinHasher.Hash(EmployeeId.New(), "7391");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void A_pin_hashed_for_one_employee_does_not_verify_for_another()
    {
        // The property that makes the salt meaningful: a hash cannot be moved between staff.
        var first = EmployeeId.New();
        var second = EmployeeId.New();

        var hash = PinHasher.Hash(first, "7391");

        Assert.False(PinHasher.Verify(second, "7391", hash));
    }

    [Fact]
    public void The_hash_is_stable_for_the_same_inputs()
    {
        var id = EmployeeId.New();

        Assert.Equal(PinHasher.Hash(id, "7391"), PinHasher.Hash(id, "7391"));
    }

    [Fact]
    public void The_stored_value_is_not_the_pin()
    {
        var id = EmployeeId.New();
        var hash = PinHasher.Hash(id, "7391");

        Assert.DoesNotContain("7391", hash, StringComparison.Ordinal);
        Assert.Equal(64, hash.Length); // 32 bytes, hex encoded
    }

    [Theory]
    [InlineData("1111")]
    [InlineData("0000")]
    [InlineData("9999")]
    public void A_pin_of_identical_digits_is_rejected(string pin)
    {
        Assert.False(PinHasher.IsAcceptable(pin, out var reason));
        Assert.Contains("identical", reason);
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("4321")]
    [InlineData("6789")]
    public void A_sequential_pin_is_rejected(string pin)
    {
        Assert.False(PinHasher.IsAcceptable(pin, out var reason));
        Assert.Contains("sequential", reason);
    }

    [Theory]
    [InlineData("7391")]
    [InlineData("1947")]
    [InlineData("58302")]
    public void A_reasonable_pin_is_accepted(string pin)
    {
        Assert.True(PinHasher.IsAcceptable(pin, out var reason));
        Assert.Empty(reason);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12")]
    [InlineData("123456789")]
    [InlineData("12a4")]
    public void A_pin_of_the_wrong_shape_is_rejected(string pin)
    {
        Assert.False(PinHasher.IsAcceptable(pin, out _));
    }

    [Fact]
    public void An_empty_pin_is_rejected()
    {
        Assert.False(PinHasher.IsAcceptable(string.Empty, out _));
        Assert.False(PinHasher.IsAcceptable("   ", out _));
    }

}
