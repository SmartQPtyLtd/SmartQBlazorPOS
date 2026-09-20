// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Globalization;
using System.Text.Json;
using Pos.Core.Domain;
using Pos.Infrastructure.Security;
using Pos.Infrastructure.Storage;

namespace Pos.Infrastructure.Checkout;

/// <summary>Outcome of an operator signing in.</summary>
/// <param name="Employee">The operator now signed in.</param>
/// <param name="Shift">The shift that was opened, or the one already running.</param>
/// <param name="OpenedNewShift">True when this sign-in started a new shift.</param>
public readonly record struct SignInResult(Employee Employee, StoredShift Shift, bool OpenedNewShift);

/// <summary>Outcome of closing a shift.</summary>
/// <param name="Shift">The closed shift.</param>
/// <param name="Totals">The cash-up produced at close.</param>
public readonly record struct ShiftCloseResult(StoredShift Shift, ShiftTotals Totals);

/// <summary>
/// Runs the shift lifecycle: sign in, open a drawer, record cash movements, close and cash up.
/// </summary>
/// <remarks>
/// <para>
/// This is where cash accountability actually happens. Every sale and refund records the shift it
/// belongs to, so a shortage can be traced to a person and a period rather than to a terminal.
/// </para>
/// <para>
/// Sign-in verifies the PIN and then opens a shift if one is not already running on this terminal.
/// A single open shift per terminal is enforced by the store, because two open drawers on one till
/// would make every cash-up ambiguous.
/// </para>
/// </remarks>
public sealed class ShiftService(
    IShiftStore shifts,
    ILocalStore sequences,
    ITerminalIdentity terminal,
    TimeProvider? timeProvider = null) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IShiftStore _shifts = shifts ?? throw new ArgumentNullException(nameof(shifts));
    private readonly ILocalStore _sequences = sequences ?? throw new ArgumentNullException(nameof(sequences));
    private readonly ITerminalIdentity _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Verifies a PIN and signs the operator in.
    /// </summary>
    /// <param name="employeeId">The operator claiming to sign in.</param>
    /// <param name="pin">The PIN they entered.</param>
    /// <param name="openingFloat">Cash counted into the drawer, when a shift is opened.</param>
    /// <exception cref="InvalidOperationException">The PIN is wrong, or the account is unusable.</exception>
    public async Task<SignInResult> SignInAsync(
        string employeeId,
        string pin,
        decimal openingFloat = 0m,
        CancellationToken ct = default)
    {
        var employee = await _shifts.GetEmployeeAsync(employeeId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("That operator is not set up on this terminal.");

        if (!employee.IsActive)
        {
            throw new InvalidOperationException("That operator account is not active.");
        }

        var domain = employee.ToDomain();

        if (!PinHasher.Verify(domain.Id, pin, employee.PinHash))
        {
            // Deliberately does not say whether the account exists or the PIN was wrong. Both are
            // the same refusal from the operator's point of view, and distinguishing them helps
            // nobody standing at a till.
            throw new InvalidOperationException("That PIN was not recognised.");
        }

        // A supervisor may already have started the drawer for this terminal. Signing in then joins
        // the running shift rather than opening a second one.
        var existing = await _shifts.GetOpenShiftAsync(_terminal.TerminalId, ct).ConfigureAwait(false);

        if (existing is not null)
        {
            return new SignInResult(domain, existing, OpenedNewShift: false);
        }

        if (openingFloat < 0m)
        {
            throw new InvalidOperationException("The opening float cannot be negative.");
        }

        var shift = await OpenShiftAsync(domain, openingFloat, ct).ConfigureAwait(false);

        return new SignInResult(domain, shift, OpenedNewShift: true);
    }

    /// <summary>Opens a shift for an operator and records the float.</summary>
    public async Task<StoredShift> OpenShiftAsync(
        Employee employee,
        decimal openingFloat,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(employee);

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var now = _time.GetUtcNow();

            var shift = new Shift
            {
                Id = ShiftId.New(),
                StoreId = employee.StoreId,
                EmployeeId = employee.Id,
                EmployeeName = employee.Name,
                TerminalId = _terminal.TerminalId,
                OpenedAt = now,
                OpeningFloat = openingFloat,
            };

            var sequence = await _sequences
                .ReserveTerminalSequenceAsync(2, ct)
                .ConfigureAwait(false);

            var stored = StoredShift.FromDomain(shift, sequence);

            // The float is a drawer event in its own right: it is cash that entered the drawer and
            // a cash-up has to account for it.
            var openingEvent = new DrawerEvent
            {
                Id = Guid.CreateVersion7().ToString("N"),
                ShiftId = shift.Id,
                StoreId = shift.StoreId,
                Type = DrawerEventType.ShiftOpened,
                Amount = openingFloat,
                EmployeeId = employee.Id,
                OccurredAt = now,
                Reason = "Opening float",
            };

            var storedEvent = StoredDrawerEvent.FromDomain(openingEvent, _terminal.TerminalId, sequence + 1);

            await _shifts.OpenShiftAsync(
                stored,
                JsonSerializer.Serialize(stored, JsonOptions),
                storedEvent,
                JsonSerializer.Serialize(storedEvent, JsonOptions),
                _terminal.TerminalId,
                ct).ConfigureAwait(false);

            return stored;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The shift currently open on this terminal, if any.</summary>
    public Task<StoredShift?> GetOpenShiftAsync(CancellationToken ct = default) =>
        _shifts.GetOpenShiftAsync(_terminal.TerminalId, ct);

    /// <summary>
    /// Records a drawer opening that is not a sale.
    /// </summary>
    /// <param name="employee">Operator who opened it.</param>
    /// <param name="reason">
    /// Why the drawer was opened. Required: a no-sale opening without a reason is unauditable, and
    /// these openings are the classic cover for a small theft.
    /// </param>
    public async Task RecordNoSaleAsync(Employee employee, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(employee);

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidOperationException(
                "A reason is required to open the drawer without a sale.");
        }

        if (!employee.Can(EmployeePermissions.OpenDrawer))
        {
            throw new InvalidOperationException(
                $"{employee.Name} is not authorised to open the cash drawer.");
        }

        var shift = await RequireOpenShiftAsync(ct).ConfigureAwait(false);

        await RecordEventAsync(
            shift,
            employee,
            DrawerEventType.NoSale,
            amount: 0m,
            reason,
            ct).ConfigureAwait(false);
    }

    /// <summary>Records cash added to or removed from the drawer.</summary>
    /// <param name="employee">Operator making the movement.</param>
    /// <param name="amount">Amount moved. Always positive; the type gives the direction.</param>
    /// <param name="isCashIn">True to add cash, false to remove it.</param>
    /// <param name="reason">Why the cash moved. Required, and shown on the cash-up.</param>
    public async Task RecordCashMovementAsync(
        Employee employee,
        decimal amount,
        bool isCashIn,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(employee);

        // Cash movements are the same authority as opening the drawer: both are ways for money to
        // move without a sale. Checking both keeps the rule consistent, so a cashier cannot reach
        // the same outcome by a route that was not guarded.
        if (!employee.Can(EmployeePermissions.OpenDrawer))
        {
            throw new InvalidOperationException(
                $"{employee.Name} is not authorised to move cash in the drawer.");
        }

        if (amount <= 0m)
        {
            throw new InvalidOperationException("A cash movement must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidOperationException("A reason is required for a cash movement.");
        }

        var shift = await RequireOpenShiftAsync(ct).ConfigureAwait(false);

        await RecordEventAsync(
            shift,
            employee,
            isCashIn ? DrawerEventType.CashIn : DrawerEventType.CashOut,
            amount,
            reason,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Works out the cash-up for a shift without closing it.
    /// </summary>
    /// <remarks>
    /// Used for a mid-shift reading, and to show the operator what is expected before they count
    /// the drawer. It must not reveal the expectation before the count — that would invite
    /// counting to the number rather than counting the cash.
    /// </remarks>
    public async Task<ShiftTotals> CalculateTotalsAsync(string shiftId, CancellationToken ct = default)
    {
        var stored = await _shifts.GetShiftAsync(shiftId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("That shift is not on this terminal.");

        var activity = await LoadActivityAsync(stored, ct).ConfigureAwait(false);

        return ShiftCalculator.Calculate(activity);
    }

    /// <summary>
    /// Closes a shift, recording the counted drawer and producing the cash-up.
    /// </summary>
    /// <param name="employee">Operator closing the shift.</param>
    /// <param name="closingCount">Cash actually counted in the drawer.</param>
    /// <param name="endReason">Why the shift ended.</param>
    /// <param name="note">Optional note, e.g. an explanation for a discrepancy.</param>
    /// <param name="countedByEmployeeId">
    /// Who counted the cash, when that is not the person who worked the shift. Required when a
    /// supervisor closes someone else's drawer.
    /// </param>
    public async Task<ShiftCloseResult> CloseShiftAsync(
        Employee employee,
        decimal closingCount,
        ShiftEndReason endReason = ShiftEndReason.Handover,
        string? note = null,
        string? countedByEmployeeId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(employee);

        if (closingCount < 0m)
        {
            throw new InvalidOperationException("The counted cash cannot be negative.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var stored = await RequireOpenShiftAsync(ct).ConfigureAwait(false);

            var isOwnShift = string.Equals(
                stored.EmployeeId, employee.Id.ToString(), StringComparison.OrdinalIgnoreCase);

            if (!isOwnShift)
            {
                // Closing someone else's drawer is a supervisory act, and it needs a name against
                // it or accountability for any discrepancy disappears when it matters most.
                if (!employee.Can(EmployeePermissions.CloseShift))
                {
                    throw new InvalidOperationException(
                        $"{employee.Name} is not authorised to close another operator's shift.");
                }

                if (string.IsNullOrWhiteSpace(countedByEmployeeId))
                {
                    throw new InvalidOperationException(
                        "Naming who counted the cash is required when closing another operator's shift.");
                }
            }

            // The expectation is derived from the recorded activity, never from a counter.
            var activity = await LoadActivityAsync(stored, ct).ConfigureAwait(false);
            var totals = ShiftCalculator.Calculate(activity);

            var now = _time.GetUtcNow();

            var closed = stored with
            {
                ClosedAt = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                EndReason = endReason.ToString(),
                ClosingCount = closingCount,

                // Preserved so what was signed off is recoverable, but reporting uses the
                // derivation rather than this value.
                ExpectedCashAtClose = totals.ExpectedCash,
                Note = note,
                ClosedByEmployeeId = isOwnShift ? null : countedByEmployeeId,
            };

            var sequence = await _sequences
                .ReserveTerminalSequenceAsync(2, ct)
                .ConfigureAwait(false);

            var closingEvent = new DrawerEvent
            {
                Id = Guid.CreateVersion7().ToString("N"),
                ShiftId = new ShiftId(Guid.Parse(stored.Id)),
                StoreId = new StoreId(Guid.Parse(stored.StoreId)),
                Type = DrawerEventType.ShiftClosed,
                Amount = closingCount,
                EmployeeId = employee.Id,
                OccurredAt = now,
                Reason = note ?? "Closing count",
            };

            var storedEvent = StoredDrawerEvent.FromDomain(closingEvent, _terminal.TerminalId, sequence + 1);

            await _shifts.CloseShiftAsync(
                closed,
                JsonSerializer.Serialize(closed, JsonOptions),
                storedEvent,
                JsonSerializer.Serialize(storedEvent, JsonOptions),
                _terminal.TerminalId,
                ct).ConfigureAwait(false);

            var finalTotals = ShiftCalculator.Calculate(
                new ShiftActivity(closed.ToDomain(), activity.Sales, activity.Returns, activity.DrawerEvents));

            return new ShiftCloseResult(closed, finalTotals);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------------- helpers

    private async Task<StoredShift> RequireOpenShiftAsync(CancellationToken ct) =>
        await _shifts.GetOpenShiftAsync(_terminal.TerminalId, ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException(
            "No shift is open on this terminal. Sign an operator in first.");

    private async Task RecordEventAsync(
        StoredShift shift,
        Employee employee,
        DrawerEventType type,
        decimal amount,
        string reason,
        CancellationToken ct)
    {
        var drawerEvent = new DrawerEvent
        {
            Id = Guid.CreateVersion7().ToString("N"),
            ShiftId = new ShiftId(Guid.Parse(shift.Id)),
            StoreId = new StoreId(Guid.Parse(shift.StoreId)),
            Type = type,
            Amount = amount,
            EmployeeId = employee.Id,
            OccurredAt = _time.GetUtcNow(),
            Reason = reason,
        };

        var sequence = await _sequences
            .ReserveTerminalSequenceAsync(1, ct)
            .ConfigureAwait(false);

        var storedEvent = StoredDrawerEvent.FromDomain(drawerEvent, _terminal.TerminalId, sequence);

        await _shifts.RecordDrawerEventAsync(
            storedEvent,
            JsonSerializer.Serialize(storedEvent, JsonOptions),
            _terminal.TerminalId,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads everything recorded against a shift, as domain types.
    /// </summary>
    /// <remarks>
    /// Rebuilt as domain objects so <see cref="ShiftCalculator"/> runs against the same types it
    /// was written and tested against, rather than a second implementation over the stored shapes
    /// that could drift from it.
    /// </remarks>
    private async Task<ShiftActivity> LoadActivityAsync(StoredShift stored, CancellationToken ct)
    {
        var sales = await _shifts.GetShiftSalesAsync(stored.Id, ct).ConfigureAwait(false);
        var returns = await _shifts.GetShiftReturnsAsync(stored.Id, ct).ConfigureAwait(false);
        var events = await _shifts.GetDrawerEventsAsync(stored.Id, ct).ConfigureAwait(false);

        return new ShiftActivity(
            stored.ToDomain(),
            [.. sales.Select(SaleMapper.ToDomain)],
            [.. returns.Select(SaleMapper.ToDomain)],
            [.. events.Select(e => e.ToDomain())]);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }
}
