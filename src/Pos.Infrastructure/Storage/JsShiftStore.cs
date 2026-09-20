// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Text.Json;
using Microsoft.JSInterop;

namespace Pos.Infrastructure.Storage;

/// <summary>
/// <see cref="IShiftStore"/> backed by IndexedDB through <c>local-store.js</c>.
/// </summary>
/// <remarks>
/// <para>
/// The browser implementation, and therefore the one that runs at a real till. It mirrors
/// <see cref="InMemoryShiftStore"/> exactly, including the atomicity guarantees: opening a shift
/// and recording its float is one IndexedDB transaction, and so is closing a shift and recording
/// the count that was signed off.
/// </para>
/// <para>
/// Those two are atomic for the same reason a sale is. A shift marked closed without its count
/// loses the figure the cash-up exists to produce, and there would be no way afterwards to tell
/// whether the drawer was short or had simply never been counted.
/// </para>
/// <para>
/// Uniqueness rules — one open shift per terminal, no activity against a closed shift — are
/// enforced <em>inside</em> the transaction rather than checked before it. A check made before
/// the transaction can be overtaken by a second tab between the check and the write, which is
/// exactly how a till ends up with two open drawers.
/// </para>
/// </remarks>
public sealed class JsShiftStore(IJSRuntime js) : IShiftStore
{
    private const string ModulePath = "./js/local-store.js";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IJSRuntime _js = js ?? throw new ArgumentNullException(nameof(js));
    private IJSObjectReference? _module;

    private async ValueTask<IJSObjectReference> ModuleAsync(CancellationToken ct) =>
        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", ct, ModulePath).ConfigureAwait(false);

    // --------------------------------------------------------------------- employees

    public async Task<IReadOnlyList<StoredEmployee>> GetEmployeesAsync(
        string storeId,
        CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        var rows = await module.InvokeAsync<JsonElement[]>("getEmployees", ct, storeId).ConfigureAwait(false);

        return [.. rows.Select(r => r.Deserialize<StoredEmployee>(Json)!).Where(e => e is not null)];
    }

    public async Task<StoredEmployee?> GetEmployeeAsync(string employeeId, CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        var row = await module
            .InvokeAsync<JsonElement?>("get", ct, "employees", employeeId)
            .ConfigureAwait(false);

        return Deserialize<StoredEmployee>(row);
    }

    public async Task UpsertEmployeesAsync(
        IReadOnlyList<StoredEmployee> employees,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(employees);

        if (employees.Count == 0)
        {
            return;
        }

        var module = await ModuleAsync(ct).ConfigureAwait(false);
        await module.InvokeVoidAsync(
            "putMany",
            ct,
            "employees",
            JsonSerializer.SerializeToElement(employees, Json)).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------ shifts

    public async Task<StoredShift?> GetOpenShiftAsync(string terminalId, CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        var row = await module
            .InvokeAsync<JsonElement?>("getOpenShift", ct, terminalId)
            .ConfigureAwait(false);

        return Deserialize<StoredShift>(row);
    }

    public async Task<StoredShift?> GetShiftAsync(string shiftId, CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        var row = await module
            .InvokeAsync<JsonElement?>("getShift", ct, shiftId)
            .ConfigureAwait(false);

        return Deserialize<StoredShift>(row);
    }

    public async Task<IReadOnlyList<StoredShift>> GetShiftsAsync(
        string storeId,
        int limit = 50,
        CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        var rows = await module
            .InvokeAsync<JsonElement[]>("getShifts", ct, storeId, limit)
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.Deserialize<StoredShift>(Json)!).Where(s => s is not null)];
    }

    public async Task<ShiftCommit> OpenShiftAsync(
        StoredShift shift,
        string shiftPayload,
        StoredDrawerEvent openingEvent,
        string eventPayload,
        string terminalId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(shift);
        ArgumentNullException.ThrowIfNull(openingEvent);

        var module = await ModuleAsync(ct).ConfigureAwait(false);

        var result = await module.InvokeAsync<ShiftCommitDto>(
            "openShift",
            ct,
            JsonSerializer.SerializeToElement(shift, Json),
            shiftPayload,
            JsonSerializer.SerializeToElement(openingEvent, Json),
            eventPayload,
            terminalId).ConfigureAwait(false);

        return new ShiftCommit(result.ShiftId, result.Queued, result.OutboxSeq);
    }

    public async Task<ShiftCommit> CloseShiftAsync(
        StoredShift shift,
        string shiftPayload,
        StoredDrawerEvent closingEvent,
        string eventPayload,
        string terminalId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(shift);
        ArgumentNullException.ThrowIfNull(closingEvent);

        var module = await ModuleAsync(ct).ConfigureAwait(false);

        var result = await module.InvokeAsync<ShiftCommitDto>(
            "closeShift",
            ct,
            JsonSerializer.SerializeToElement(shift, Json),
            shiftPayload,
            JsonSerializer.SerializeToElement(closingEvent, Json),
            eventPayload,
            terminalId).ConfigureAwait(false);

        return new ShiftCommit(result.ShiftId, result.Queued, result.OutboxSeq);
    }

    public async Task<ShiftCommit> RecordDrawerEventAsync(
        StoredDrawerEvent drawerEvent,
        string eventPayload,
        string terminalId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(drawerEvent);

        var module = await ModuleAsync(ct).ConfigureAwait(false);

        var result = await module.InvokeAsync<ShiftCommitDto>(
            "recordDrawerEvent",
            ct,
            JsonSerializer.SerializeToElement(drawerEvent, Json),
            eventPayload,
            terminalId).ConfigureAwait(false);

        return new ShiftCommit(result.ShiftId, result.Queued, result.OutboxSeq);
    }

    public async Task<IReadOnlyList<StoredDrawerEvent>> GetDrawerEventsAsync(
        string shiftId,
        CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        var rows = await module
            .InvokeAsync<JsonElement[]>("getDrawerEvents", ct, shiftId)
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.Deserialize<StoredDrawerEvent>(Json)!).Where(e => e is not null)];
    }

    public async Task<IReadOnlyList<StoredSale>> GetShiftSalesAsync(
        string shiftId,
        CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        var rows = await module
            .InvokeAsync<JsonElement[]>("getShiftSales", ct, shiftId)
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.Deserialize<StoredSale>(Json)!).Where(s => s is not null)];
    }

    public async Task<IReadOnlyList<StoredReturn>> GetShiftReturnsAsync(
        string shiftId,
        CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        var rows = await module
            .InvokeAsync<JsonElement[]>("getShiftReturns", ct, shiftId)
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.Deserialize<StoredReturn>(Json)!).Where(r => r is not null)];
    }

    private static T? Deserialize<T>(JsonElement? element)
        where T : class
    {
        if (element is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.Deserialize<T>(Json);
    }

    private sealed record ShiftCommitDto(string ShiftId, int Queued, long OutboxSeq);
}
