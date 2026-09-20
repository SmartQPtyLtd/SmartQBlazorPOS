// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;

// The stored records carry properties named after the domain types they hold, which shadows those
// type names inside the record bodies. Aliasing keeps the conversion code readable and unambiguous.
using DomainEmployeeId = Pos.Core.Domain.EmployeeId;
using DomainShiftId = Pos.Core.Domain.ShiftId;
using DomainStoreId = Pos.Core.Domain.StoreId;

namespace Pos.Infrastructure.Storage;

/// <summary>
/// A persisted employee.
/// </summary>
public sealed record StoredEmployee
{
    public required string Id { get; init; }

    public required string StoreId { get; init; }

    public required string Name { get; init; }

    public string? Initials { get; init; }

    /// <summary>Salted, stretched hash of the PIN. Never the PIN itself.</summary>
    public required string PinHash { get; init; }

    /// <summary>Permission flags, serialised by name so the set can grow without renumbering.</summary>
    public required string Permissions { get; init; }

    public bool IsActive { get; init; } = true;

    public bool RequiresSupervision { get; init; }

    public required string CreatedAt { get; init; }

    /// <summary>Rebuilds the domain employee.</summary>
    public Employee ToDomain() => new()
    {
        Id = Guid.TryParse(Id, out var id) ? new DomainEmployeeId(id) : DomainEmployeeId.New(),
        StoreId = Guid.TryParse(StoreId, out var storeId) ? new DomainStoreId(storeId) : DomainStoreId.New(),
        Name = Name,
        Initials = Initials,
        PinHash = PinHash,
        Permissions = Enum.TryParse<EmployeePermissions>(Permissions, out var permissions)
            ? permissions
            : EmployeePermissions.None,
        IsActive = IsActive,
        RequiresSupervision = RequiresSupervision,
        CreatedAt = DateTimeOffset.TryParse(
            CreatedAt, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var created)
                ? created
                : DateTimeOffset.UtcNow,
    };

    /// <summary>Projects a domain employee into its stored form.</summary>
    public static StoredEmployee FromDomain(Employee employee)
    {
        ArgumentNullException.ThrowIfNull(employee);

        return new StoredEmployee
        {
            Id = employee.Id.ToString(),
            StoreId = employee.StoreId.ToString(),
            Name = employee.Name,
            Initials = employee.Initials,
            PinHash = employee.PinHash,
            Permissions = employee.Permissions.ToString(),
            IsActive = employee.IsActive,
            RequiresSupervision = employee.RequiresSupervision,
            CreatedAt = employee.CreatedAt.ToUniversalTime().ToString(
                "O", System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}

/// <summary>
/// A persisted shift.
/// </summary>
/// <remarks>
/// Shifts are terminal-owned and append-only in effect: a shift is opened once and closed once,
/// and the totals are derived from the activity recorded against it rather than stored as counters.
/// </remarks>
public sealed record StoredShift
{
    public required string Id { get; init; }

    public required string StoreId { get; init; }

    public required string EmployeeId { get; init; }

    /// <summary>Operator name at the time of the shift, so a report survives a rename.</summary>
    public required string EmployeeName { get; init; }

    public required string TerminalId { get; init; }

    public required long TerminalSeq { get; init; }

    public required string OpenedAt { get; init; }

    public string? ClosedAt { get; init; }

    public string? EndReason { get; init; }

    public required decimal OpeningFloat { get; init; }

    public decimal? ClosingCount { get; init; }

    /// <summary>Expected cash as worked out at close, preserving what was signed off.</summary>
    public decimal? ExpectedCashAtClose { get; init; }

    public string? Note { get; init; }

    public string? ClosedByEmployeeId { get; init; }

    /// <summary>True while the shift has no close recorded.</summary>
    public bool IsOpen => string.IsNullOrEmpty(ClosedAt);

    /// <summary>Rebuilds the domain shift.</summary>
    public Shift ToDomain() => new()
    {
        Id = Guid.TryParse(Id, out var id) ? new DomainShiftId(id) : DomainShiftId.New(),
        StoreId = Guid.TryParse(StoreId, out var storeId) ? new DomainStoreId(storeId) : DomainStoreId.New(),
        EmployeeId = Guid.TryParse(EmployeeId, out var employeeId) ? new DomainEmployeeId(employeeId) : DomainEmployeeId.New(),
        EmployeeName = EmployeeName,
        TerminalId = TerminalId,
        OpenedAt = DateTimeOffset.TryParse(
            OpenedAt, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var opened)
                ? opened
                : DateTimeOffset.UtcNow,
        ClosedAt = DateTimeOffset.TryParse(
            ClosedAt, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var closed)
                ? closed
                : null,
        EndReason = Enum.TryParse<ShiftEndReason>(EndReason, out var reason) ? reason : null,
        OpeningFloat = OpeningFloat,
        ClosingCount = ClosingCount,
        ExpectedCashAtClose = ExpectedCashAtClose,
        Note = Note,
        ClosedByEmployeeId = ClosedByEmployeeId,
    };

    /// <summary>Projects a domain shift into its stored form.</summary>
    public static StoredShift FromDomain(Shift shift, long terminalSeq)
    {
        ArgumentNullException.ThrowIfNull(shift);

        return new StoredShift
        {
            Id = shift.Id.ToString(),
            StoreId = shift.StoreId.ToString(),
            EmployeeId = shift.EmployeeId.ToString(),
            EmployeeName = shift.EmployeeName,
            TerminalId = shift.TerminalId,
            TerminalSeq = terminalSeq,
            OpenedAt = shift.OpenedAt.ToUniversalTime().ToString(
                "O", System.Globalization.CultureInfo.InvariantCulture),
            ClosedAt = shift.ClosedAt?.ToUniversalTime().ToString(
                "O", System.Globalization.CultureInfo.InvariantCulture),
            EndReason = shift.EndReason?.ToString(),
            OpeningFloat = shift.OpeningFloat,
            ClosingCount = shift.ClosingCount,
            ExpectedCashAtClose = shift.ExpectedCashAtClose,
            Note = shift.Note,
            ClosedByEmployeeId = shift.ClosedByEmployeeId,
        };
    }
}

/// <summary>
/// A persisted cash drawer event.
/// </summary>
/// <remarks>
/// Append-only. A drawer that opened six times during a quiet hour is information, and every
/// no-sale opening is the classic cover for a small theft, so these are never deleted and their
/// count is never reset.
/// </remarks>
public sealed record StoredDrawerEvent
{
    public required string Id { get; init; }

    public required string ShiftId { get; init; }

    public required string StoreId { get; init; }

    public required string TerminalId { get; init; }

    public required long TerminalSeq { get; init; }

    public required string Type { get; init; }

    public decimal Amount { get; init; }

    public required string EmployeeId { get; init; }

    public required string OccurredAt { get; init; }

    public string? Reason { get; init; }

    /// <summary>Rebuilds the domain drawer event.</summary>
    public DrawerEvent ToDomain() => new()
    {
        Id = Id,
        ShiftId = Guid.TryParse(ShiftId, out var shiftId) ? new DomainShiftId(shiftId) : DomainShiftId.New(),
        StoreId = Guid.TryParse(StoreId, out var storeId) ? new DomainStoreId(storeId) : DomainStoreId.New(),
        Type = Enum.TryParse<DrawerEventType>(Type, out var type) ? type : DrawerEventType.NoSale,
        Amount = Amount,
        EmployeeId = Guid.TryParse(EmployeeId, out var employeeId) ? new DomainEmployeeId(employeeId) : DomainEmployeeId.New(),
        OccurredAt = DateTimeOffset.TryParse(
            OccurredAt, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var occurred)
                ? occurred
                : DateTimeOffset.UtcNow,
        Reason = Reason,
    };

    /// <summary>Projects a domain drawer event into its stored form.</summary>
    public static StoredDrawerEvent FromDomain(DrawerEvent drawerEvent, string terminalId, long terminalSeq)
    {
        ArgumentNullException.ThrowIfNull(drawerEvent);

        return new StoredDrawerEvent
        {
            Id = drawerEvent.Id,
            ShiftId = drawerEvent.ShiftId.ToString(),
            StoreId = drawerEvent.StoreId.ToString(),
            TerminalId = terminalId,
            TerminalSeq = terminalSeq,
            Type = drawerEvent.Type.ToString(),
            Amount = drawerEvent.Amount,
            EmployeeId = drawerEvent.EmployeeId.ToString(),
            OccurredAt = drawerEvent.OccurredAt.ToUniversalTime().ToString(
                "O", System.Globalization.CultureInfo.InvariantCulture),
            Reason = drawerEvent.Reason,
        };
    }
}

/// <summary>Result of a shift lifecycle commit.</summary>
/// <param name="ShiftId">The shift affected.</param>
/// <param name="Queued">How many outbox entries the commit created.</param>
/// <param name="OutboxSeq">The local outbox position after the commit.</param>
public readonly record struct ShiftCommit(string ShiftId, int Queued, long OutboxSeq);
