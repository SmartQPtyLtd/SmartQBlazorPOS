// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;
using Pos.Infrastructure.Storage;

namespace Pos.Infrastructure.Storage;

/// <summary>
/// The cash-drawer side of local storage: employees, shifts, and drawer events.
/// </summary>
/// <remarks>
/// Split from <see cref="ILocalStore"/> because a terminal that cannot sign an operator in is
/// still able to sell, and the sales path should not have to depend on the shift path.
/// </remarks>
public interface IShiftStore
{
    // --------------------------------------------------------------------- employees

    /// <summary>Active employees who can sign in at this store.</summary>
    Task<IReadOnlyList<StoredEmployee>> GetEmployeesAsync(string storeId, CancellationToken ct = default);

    /// <summary>Reads one employee, or null when they are not on this terminal.</summary>
    Task<StoredEmployee?> GetEmployeeAsync(string employeeId, CancellationToken ct = default);

    /// <summary>Inserts or replaces employees, used when a roster is pulled from head office.</summary>
    Task UpsertEmployeesAsync(IReadOnlyList<StoredEmployee> employees, CancellationToken ct = default);

    // ------------------------------------------------------------------------ shifts

    /// <summary>
    /// The shift currently open on a terminal, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Scoped to the terminal rather than the store: two tills in one shop each have their own
    /// drawer and therefore their own shift.
    /// </remarks>
    Task<StoredShift?> GetOpenShiftAsync(string terminalId, CancellationToken ct = default);

    /// <summary>Reads a shift by id.</summary>
    Task<StoredShift?> GetShiftAsync(string shiftId, CancellationToken ct = default);

    /// <summary>Shifts recorded for a store, most recent first.</summary>
    Task<IReadOnlyList<StoredShift>> GetShiftsAsync(
        string storeId,
        int limit = 50,
        CancellationToken ct = default);

    /// <summary>
    /// Opens a shift and records the opening float as a drawer event, atomically.
    /// </summary>
    /// <param name="shift">The shift to open.</param>
    /// <param name="shiftPayload">The shift serialised for sync.</param>
    /// <param name="openingEvent">The drawer event recording the float.</param>
    /// <param name="eventPayload">That event serialised for sync.</param>
    /// <param name="terminalId">Identity of the till, for the ordering key.</param>
    Task<ShiftCommit> OpenShiftAsync(
        StoredShift shift,
        string shiftPayload,
        StoredDrawerEvent openingEvent,
        string eventPayload,
        string terminalId,
        CancellationToken ct = default);

    /// <summary>
    /// Closes a shift and records the closing count as a drawer event, atomically.
    /// </summary>
    /// <remarks>
    /// The close and its count event are one operation: a shift marked closed without its count
    /// would lose the figure the whole cash-up exists to produce.
    /// </remarks>
    Task<ShiftCommit> CloseShiftAsync(
        StoredShift shift,
        string shiftPayload,
        StoredDrawerEvent closingEvent,
        string eventPayload,
        string terminalId,
        CancellationToken ct = default);

    /// <summary>
    /// Records a drawer event against an open shift.
    /// </summary>
    /// <remarks>
    /// Used for no-sale openings and mid-shift cash movements, which happen without the shift
    /// itself changing.
    /// </remarks>
    Task<ShiftCommit> RecordDrawerEventAsync(
        StoredDrawerEvent drawerEvent,
        string eventPayload,
        string terminalId,
        CancellationToken ct = default);

    /// <summary>Every drawer event recorded against a shift, in order.</summary>
    Task<IReadOnlyList<StoredDrawerEvent>> GetDrawerEventsAsync(
        string shiftId,
        CancellationToken ct = default);

    /// <summary>Sales rung during a shift.</summary>
    Task<IReadOnlyList<StoredSale>> GetShiftSalesAsync(string shiftId, CancellationToken ct = default);

    /// <summary>Refunds processed during a shift.</summary>
    Task<IReadOnlyList<StoredReturn>> GetShiftReturnsAsync(string shiftId, CancellationToken ct = default);
}
