// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Globalization;
using Pos.Core.Domain;

namespace Pos.Infrastructure.Storage;

/// <summary>
/// In-memory <see cref="IShiftStore"/>, mirroring the browser implementation.
/// </summary>
/// <remarks>
/// <para>
/// Implements the same atomicity guarantees, so the shift lifecycle can be exercised end to end
/// on a build agent with no browser.
/// </para>
/// <para>
/// <b>Link it to an <see cref="InMemoryLocalStore"/> to get a faithful cash-up.</b> In the browser
/// both stores read one IndexedDB, so a shift's cash-up finds the sales that were actually
/// committed. An unlinked pair cannot: the ledger's sales are invisible to the shift store, so a
/// cash-up would report an empty shift while the till had been trading all morning. Pass the
/// ledger in unless the test is deliberately isolating the shift machinery.
/// </para>
/// </remarks>
public sealed class InMemoryShiftStore(InMemoryLocalStore? ledger = null) : IShiftStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StoredEmployee> _employees = [];
    private readonly Dictionary<string, StoredShift> _shifts = [];
    private readonly Dictionary<string, StoredDrawerEvent> _drawerEvents = [];

    /// <summary>
    /// Activity attached directly, for tests that isolate the shift machinery.
    /// </summary>
    /// <remarks>
    /// Kept alongside the linked ledger rather than replacing it, so a test can pose a cash-up
    /// without having to ring a sale through the whole checkout path to do it.
    /// </remarks>
    private readonly Dictionary<string, StoredSale> _sales = [];
    private readonly Dictionary<string, StoredReturn> _returns = [];

    private long _outboxSeq;
    private long _terminalSeq;

    /// <summary>Simulates a failed commit, to exercise rollback.</summary>
    public Exception? FailNextCommit { get; set; }

    // --------------------------------------------------------------------- employees

    public Task<IReadOnlyList<StoredEmployee>> GetEmployeesAsync(string storeId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<StoredEmployee> result = _employees.Values
                .Where(e => e.StoreId == storeId && e.IsActive)
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Task.FromResult(result);
        }
    }

    public Task<StoredEmployee?> GetEmployeeAsync(string employeeId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_employees.TryGetValue(employeeId, out var employee) ? employee : null);
        }
    }

    public Task UpsertEmployeesAsync(IReadOnlyList<StoredEmployee> employees, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(employees);

        lock (_gate)
        {
            foreach (var employee in employees)
            {
                _employees[employee.Id] = employee;
            }
        }

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------------ shifts

    public Task<StoredShift?> GetOpenShiftAsync(string terminalId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var open = _shifts.Values
                .Where(s => s.TerminalId == terminalId && s.IsOpen)
                .OrderByDescending(s => s.OpenedAt, StringComparer.Ordinal)
                .FirstOrDefault();

            return Task.FromResult(open);
        }
    }

    public Task<StoredShift?> GetShiftAsync(string shiftId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_shifts.TryGetValue(shiftId, out var shift) ? shift : null);
        }
    }

    public Task<IReadOnlyList<StoredShift>> GetShiftsAsync(
        string storeId,
        int limit = 50,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<StoredShift> result = _shifts.Values
                .Where(s => s.StoreId == storeId)
                .OrderByDescending(s => s.OpenedAt, StringComparer.Ordinal)
                .Take(Math.Max(1, limit))
                .ToArray();

            return Task.FromResult(result);
        }
    }

    public Task<ShiftCommit> OpenShiftAsync(
        StoredShift shift,
        string shiftPayload,
        StoredDrawerEvent openingEvent,
        string eventPayload,
        string terminalId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(shift);
        ArgumentNullException.ThrowIfNull(openingEvent);

        lock (_gate)
        {
            ThrowIfCommitFails();

            // Only one shift may be open on a terminal. Two open drawers on one till would make
            // the cash-up ambiguous, which defeats the point of having shifts at all.
            if (_shifts.Values.Any(s => s.TerminalId == terminalId && s.IsOpen))
            {
                throw new InvalidOperationException(
                    "A shift is already open on this terminal. Close it before opening another.");
            }

            var addedShift = !_shifts.ContainsKey(shift.Id);
            var addedEvent = !_drawerEvents.ContainsKey(openingEvent.Id);

            _shifts[shift.Id] = shift;
            _drawerEvents[openingEvent.Id] = openingEvent;

            Enqueue("shift", shift.Id, terminalId, shift.TerminalSeq);
            Enqueue("drawerEvent", openingEvent.Id, terminalId, openingEvent.TerminalSeq);

            _ = shiftPayload;
            _ = eventPayload;
            _ = addedShift;
            _ = addedEvent;

            return Task.FromResult(new ShiftCommit(shift.Id, 2, _outboxSeq));
        }
    }

    public Task<ShiftCommit> CloseShiftAsync(
        StoredShift shift,
        string shiftPayload,
        StoredDrawerEvent closingEvent,
        string eventPayload,
        string terminalId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(shift);
        ArgumentNullException.ThrowIfNull(closingEvent);

        lock (_gate)
        {
            ThrowIfCommitFails();

            if (!_shifts.TryGetValue(shift.Id, out var existing))
            {
                throw new InvalidOperationException("That shift is not on this terminal.");
            }

            if (!existing.IsOpen)
            {
                // Closing twice would overwrite the count that was signed off, destroying the
                // evidence of whatever it showed.
                throw new InvalidOperationException("That shift has already been closed.");
            }

            _shifts[shift.Id] = shift;
            _drawerEvents[closingEvent.Id] = closingEvent;

            Enqueue("shift", shift.Id, terminalId, shift.TerminalSeq);
            Enqueue("drawerEvent", closingEvent.Id, terminalId, closingEvent.TerminalSeq);

            _ = shiftPayload;
            _ = eventPayload;

            return Task.FromResult(new ShiftCommit(shift.Id, 2, _outboxSeq));
        }
    }

    public Task<ShiftCommit> RecordDrawerEventAsync(
        StoredDrawerEvent drawerEvent,
        string eventPayload,
        string terminalId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(drawerEvent);

        lock (_gate)
        {
            ThrowIfCommitFails();

            if (!_shifts.TryGetValue(drawerEvent.ShiftId, out var shift))
            {
                throw new InvalidOperationException(
                    "That drawer event belongs to a shift this terminal does not have.");
            }

            if (!shift.IsOpen)
            {
                // A drawer event against a closed shift would change a cash-up that has already
                // been signed off.
                throw new InvalidOperationException(
                    "The shift is closed, so no further drawer activity can be recorded against it.");
            }

            _drawerEvents[drawerEvent.Id] = drawerEvent;
            Enqueue("drawerEvent", drawerEvent.Id, terminalId, drawerEvent.TerminalSeq);

            _ = eventPayload;

            return Task.FromResult(new ShiftCommit(drawerEvent.ShiftId, 1, _outboxSeq));
        }
    }

    public Task<IReadOnlyList<StoredDrawerEvent>> GetDrawerEventsAsync(
        string shiftId,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<StoredDrawerEvent> result = _drawerEvents.Values
                .Where(e => e.ShiftId == shiftId)
                .OrderBy(e => e.TerminalSeq)
                .ToArray();

            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<StoredSale>> GetShiftSalesAsync(string shiftId, CancellationToken ct = default)
    {
        // The linked ledger holds what was really committed; the attached set holds what a test
        // posed directly. Both count, because both are sales recorded during this shift.
        var ledgerSales = ledger?.Sales ?? [];

        IReadOnlyList<StoredSale> result =
        [
            .. ledgerSales
                .Concat(AttachedSales())
                .Where(s => s.ShiftId == shiftId)
                .OrderBy(s => s.TerminalSeq),
        ];

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<StoredReturn>> GetShiftReturnsAsync(string shiftId, CancellationToken ct = default)
    {
        var ledgerReturns = ledger?.Returns ?? [];

        IReadOnlyList<StoredReturn> result =
        [
            .. ledgerReturns
                .Concat(AttachedReturns())
                .Where(r => r.ShiftId == shiftId)
                .OrderBy(r => r.TerminalSeq),
        ];

        return Task.FromResult(result);
    }

    private StoredSale[] AttachedSales()
    {
        lock (_gate)
        {
            return [.. _sales.Values];
        }
    }

    private StoredReturn[] AttachedReturns()
    {
        lock (_gate)
        {
            return [.. _returns.Values];
        }
    }

    // ------------------------------------------------------------------------- test aid

    /// <summary>Attaches a sale to a shift, as a real commit would.</summary>
    public void AttachSale(StoredSale sale)
    {
        lock (_gate)
        {
            _sales[sale.Id] = sale;
        }
    }

    /// <summary>Attaches a refund to a shift, as a real commit would.</summary>
    public void AttachReturn(StoredReturn salesReturn)
    {
        lock (_gate)
        {
            _returns[salesReturn.Id] = salesReturn;
        }
    }

    private void ThrowIfCommitFails()
    {
        if (FailNextCommit is { } failure)
        {
            FailNextCommit = null;
            throw failure;
        }
    }

    private void Enqueue(string entityType, string entityId, string terminalId, long terminalSeq)
    {
        _outboxSeq++;
        _terminalSeq = Math.Max(_terminalSeq, terminalSeq);

        // The in-memory store does not model the outbox itself; the shift lifecycle is what these
        // tests exercise. The browser implementation writes real outbox entries in the same
        // transaction.
        _ = entityType;
        _ = entityId;
        _ = terminalId;
    }
}
