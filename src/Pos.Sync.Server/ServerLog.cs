// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

namespace Pos.Sync.Server;

/// <summary>
/// Source-generated log messages for the hub.
/// </summary>
/// <remarks>
/// The generator emits a cached delegate and a level guard per message, so a disabled log
/// level costs a branch rather than a boxing allocation. These sit on the ingest path,
/// which runs for every record a terminal pushes.
/// </remarks>
internal static partial class ServerLog
{
    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Warning,
        Message = "Sequence gap from terminal {TerminalId} in store {StoreId}: expected {Expected} but received {Received}. Records may have been lost in transit.")]
    public static partial void SequenceGap(
        ILogger logger,
        string terminalId,
        string storeId,
        long expected,
        long received);

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Terminal {DeviceId} enrolled against store {StoreCode}.")]
    public static partial void TerminalEnrolled(ILogger logger, string deviceId, string storeCode);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Information,
        Message = "Terminal {TerminalId} in store {StoreId} restarted its sequence counter, asking for position {Requested} when {Highest} is already stored. Stored at {Assigned} instead.")]
    public static partial void SequenceReset(
        ILogger logger,
        string terminalId,
        string storeId,
        long requested,
        long highest,
        long assigned);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Information,
        Message = "Published {Published} product(s) to store {StoreCode}; {Rejected} rejected.")]
    public static partial void CataloguePublished(
        ILogger logger,
        string storeCode,
        int published,
        int rejected);

    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Warning,
        Message = "Head-office access is NOT configured. Set HeadOffice:Token to a token of at least {MinimumLength} characters before provisioning stores or enrolling terminals; head-office endpoints will refuse every request until then.")]
    public static partial void HeadOfficeNotConfigured(ILogger logger, int minimumLength);

    [LoggerMessage(
        EventId = 2005,
        Level = LogLevel.Information,
        Message = "Relayed stock transfer {TransferId} from store {FromStoreId} to store {ToStoreId}.")]
    public static partial void TransferRelayed(
        ILogger logger,
        string transferId,
        string fromStoreId,
        string toStoreId);

    [LoggerMessage(
        EventId = 2006,
        Level = LogLevel.Warning,
        Message = "Stock transfer {TransferId} was stored but not relayed: {Reason}.")]
    public static partial void TransferNotRelayed(ILogger logger, string transferId, string reason);

    [LoggerMessage(
        EventId = 2007,
        Level = LogLevel.Information,
        Message = "Terminal {DeviceId} rotated its credential; the previous one is accepted until {PreviousValidUntil}.")]
    public static partial void CredentialRotated(
        ILogger logger,
        string deviceId,
        DateTimeOffset previousValidUntil);

    [LoggerMessage(
        EventId = 2008,
        Level = LogLevel.Warning,
        Message = "Terminal {DeviceId} retried a rotation that had already been applied. That is the lost-response path and is harmless, but it means a response did not reach the till.")]
    public static partial void CredentialRotationRetried(ILogger logger, string deviceId);

    [LoggerMessage(
        EventId = 2009,
        Level = LogLevel.Error,
        Message = "A superseded refresh token for terminal {DeviceId} was used to mint different credentials. Two parties hold this device's credentials, so the device has been revoked and must be enrolled again.")]
    public static partial void CredentialReuseDetected(ILogger logger, string deviceId);

    [LoggerMessage(
        EventId = 2010,
        Level = LogLevel.Warning,
        Message = "Terminal {DeviceId} is still using the secret its last rotation replaced, so it has not yet confirmed that rotation. It is accepted until the grace window closes.")]
    public static partial void SupersededSecretInUse(ILogger logger, string deviceId);

    [LoggerMessage(
        EventId = 2011,
        Level = LogLevel.Critical,
        Message = "The hub's database predates this build: {Count} column(s) are missing from table '{Table}'. EnsureCreated never alters an existing table, so these must be applied by hand. Run: {Statements}")]
    public static partial void SchemaOutOfDate(
        ILogger logger,
        int count,
        string table,
        string statements);
}
