// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Microsoft.Extensions.Logging;

namespace Pos.Web.Terminal;

/// <summary>
/// Source-generated log messages.
/// </summary>
/// <remarks>
/// The generator emits a cached delegate and a guard per message, so a disabled log level
/// costs a branch rather than a boxing allocation and a format call. These are on the
/// startup and sync paths, which run often enough on a till to be worth it.
/// </remarks>
internal static partial class TerminalLog
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Warning,
        Message = "Durable storage was not granted. Local data may be evicted by the browser, which would lose a day of trading.")]
    public static partial void StorageNotDurable(ILogger logger);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Seeded {Count} demo products into an empty catalogue.")]
    public static partial void CatalogueSeeded(ILogger logger, int count);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "Catalogue change {EntityId} could not be parsed and was skipped.")]
    public static partial void CatalogueChangeUnparsable(ILogger logger, string entityId, Exception exception);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Information,
        Message = "Printer resolved: {Transport} ({Status}).")]
    public static partial void PrinterResolved(ILogger logger, string transport, Pos.Devices.Transport.DeviceStatus status);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Debug,
        Message = "The customer display could not be updated.")]
    public static partial void DisplayPublishFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Warning,
        Message = "The browser capability probe could not be run.")]
    public static partial void CapabilityProbeFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Warning,
        Message = "Recent sales could not be loaded for the refund screen.")]
    public static partial void RefundLookupFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1007,
        Level = LogLevel.Error,
        Message = "A refund was recorded but its slip did not print.")]
    public static partial void RefundSlipFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1008,
        Level = LogLevel.Warning,
        Message = "The stock movement history could not be loaded.")]
    public static partial void CatalogHistoryFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1009,
        Level = LogLevel.Information,
        Message = "Seeded {Count} demo employees into an empty roster.")]
    public static partial void RosterSeeded(ILogger logger, int count);

    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Warning,
        Message = "Shift {ShiftId} is open on this terminal but names {EmployeeName}, who is not on the roster. The drawer is open and unattributed.")]
    public static partial void ShiftOrphaned(ILogger logger, string shiftId, string employeeName);

    [LoggerMessage(
        EventId = 1011,
        Level = LogLevel.Error,
        Message = "A sale was recorded but its receipt did not print.")]
    public static partial void ReceiptFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1012,
        Level = LogLevel.Warning,
        Message = "The roster could not be loaded on the sign-in screen.")]
    public static partial void SignInLoadFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1013,
        Level = LogLevel.Warning,
        Message = "The drawer screen could not load the shift.")]
    public static partial void DrawerLoadFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1014,
        Level = LogLevel.Error,
        Message = "A shift was closed but its slip did not print.")]
    public static partial void ShiftSlipFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1015,
        Level = LogLevel.Warning,
        Message = "The drawer did not open. The shift was still recorded.")]
    public static partial void DrawerKickFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1016,
        Level = LogLevel.Information,
        Message = "A sale needs {TicketCount} preparation ticket(s), but no kitchen printer is paired.")]
    public static partial void KitchenPrinterMissing(ILogger logger, int ticketCount);

    [LoggerMessage(
        EventId = 1017,
        Level = LogLevel.Error,
        Message = "The ticket for station {StationId} did not print. Other stations were still sent theirs.")]
    public static partial void StationTicketFailed(ILogger logger, string stationId, Exception exception);

    [LoggerMessage(
        EventId = 1018,
        Level = LogLevel.Information,
        Message = "{Description} was not printed: no label printer is paired with this terminal.")]
    public static partial void LabelPrinterMissing(ILogger logger, string description);

    [LoggerMessage(
        EventId = 1019,
        Level = LogLevel.Warning,
        Message = "The configured label stock is not usable, so nothing was printed: {Reason}")]
    public static partial void LabelStockInvalid(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 1020,
        Level = LogLevel.Error,
        Message = "{Description} did not print.")]
    public static partial void LabelPrintFailed(ILogger logger, string description, Exception exception);

    [LoggerMessage(
        EventId = 1021,
        Level = LogLevel.Information,
        Message = "Stock transfer {TransferId} was already {ExistingStatus} here, so the relayed {ArrivingStatus} copy was ignored.")]
    public static partial void TransferRelaySuperseded(
        ILogger logger,
        string transferId,
        string existingStatus,
        string arrivingStatus);

    [LoggerMessage(
        EventId = 1022,
        Level = LogLevel.Information,
        Message = "Device credential rotated. The next rotation is due {RotateAfter}.")]
    public static partial void CredentialRotated(ILogger logger, DateTimeOffset rotateAfter);

    [LoggerMessage(
        EventId = 1023,
        Level = LogLevel.Warning,
        Message = "The device credential could not be rotated. This terminal keeps using its current credential, which is still accepted, and will try again on the next sync.")]
    public static partial void CredentialRotationFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1024,
        Level = LogLevel.Warning,
        Message = "The roster could not be read, so the report will name operators by identifier.")]
    public static partial void ReportOperatorNamesFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1025,
        Level = LogLevel.Information,
        Message = "Shift {ShiftId} ({EmployeeName}) is open but was not re-attached: this tab has no matching sign-in marker, so it was signed out deliberately.")]
    public static partial void ShiftRestoreSkipped(ILogger logger, string shiftId, string employeeName);
}
