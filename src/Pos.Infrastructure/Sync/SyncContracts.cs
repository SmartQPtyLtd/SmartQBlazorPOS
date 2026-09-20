// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

namespace Pos.Infrastructure.Sync;

/// <summary>Streams a terminal can pull independently.</summary>
public static class SyncStreams
{
    /// <summary>Sales and their stock movements, authored by this store.</summary>
    public const string Sales = "sales";

    /// <summary>Catalogue, pricing, and tax published by head office.</summary>
    public const string Catalog = "catalog";

    /// <summary>Reference data shared by every store.</summary>
    public const string Global = "global";
}

/// <summary>Status of a single record within a push.</summary>
public enum SyncRecordStatus
{
    /// <summary>Accepted and durable on the hub.</summary>
    Accepted = 0,

    /// <summary>
    /// Already present. Returned with the same body as the original acceptance, so a
    /// retried push is indistinguishable from the first and a lost response is harmless.
    /// </summary>
    Duplicate = 1,

    /// <summary>Rejected. <c>Error</c> carries the reason.</summary>
    Rejected = 2,
}

/// <summary>One record in a push batch.</summary>
/// <param name="EntityType">Discriminator, e.g. <c>sale</c> or <c>stockMovement</c>.</param>
/// <param name="EntityId">Client-generated UUIDv7. The dedupe key.</param>
/// <param name="TerminalId">Till that authored the record.</param>
/// <param name="TerminalSeq">Monotonic per-terminal ordering.</param>
/// <param name="Payload">The record, serialised.</param>
public sealed record SyncRecord(
    string EntityType,
    string EntityId,
    string TerminalId,
    long TerminalSeq,
    string Payload);

/// <summary>A batch of records pushed from a terminal.</summary>
/// <param name="BatchId">Client-generated id for this attempt, for tracing.</param>
/// <param name="Records">The records to send, in terminal sequence order.</param>
public sealed record SyncPushRequest(string BatchId, IReadOnlyList<SyncRecord> Records);

/// <summary>Per-record outcome of a push.</summary>
/// <param name="EntityId">The record this outcome refers to.</param>
/// <param name="Status">Whether it was accepted, already known, or rejected.</param>
/// <param name="Error">Reason when <paramref name="Status"/> is Rejected.</param>
public sealed record SyncRecordResult(string EntityId, SyncRecordStatus Status, string? Error = null);

/// <summary>Result of a push.</summary>
/// <param name="Results">One outcome per submitted record.</param>
/// <param name="Cursor">
/// The server's current change cursor after ingest. Returned so the pushing terminal can
/// advance past its own writes without re-pulling them.
/// </param>
public sealed record SyncPushResponse(IReadOnlyList<SyncRecordResult> Results, string Cursor)
{
    /// <summary>True when every record was accepted or already present.</summary>
    public bool AllSucceeded => Results.All(r => r.Status != SyncRecordStatus.Rejected);
}

/// <summary>A single change pulled from the hub.</summary>
/// <param name="ChangeSeq">Monotonic server-assigned position. Opaque to the client.</param>
/// <param name="EntityType">Discriminator.</param>
/// <param name="EntityId">Record identity.</param>
/// <param name="StoreId">Owning store, for store-scoped changes.</param>
/// <param name="Payload">The record, serialised.</param>
public sealed record SyncChange(
    long ChangeSeq,
    string EntityType,
    string EntityId,
    string? StoreId,
    string Payload);

/// <summary>Result of a pull.</summary>
/// <param name="Changes">Changes after the requested cursor.</param>
/// <param name="NextCursor">Cursor to send on the next pull.</param>
/// <param name="HasMore">True when more changes are already waiting.</param>
/// <param name="ResetRequired">
/// True when the requested cursor is older than the retained change log, so the terminal
/// must discard its replica and bootstrap from a full snapshot.
/// </param>
public sealed record SyncPullResponse(
    IReadOnlyList<SyncChange> Changes,
    string NextCursor,
    bool HasMore,
    bool ResetRequired = false);

/// <summary>
/// One store in the estate directory, as a till is allowed to see it.
/// </summary>
/// <remarks>
/// Deliberately a projection rather than the store record: a till needs to name a
/// destination for a stock transfer and nothing more. Currency, tax mode and registration
/// numbers are absent because a browser in a shop is not a trustworthy place for the
/// estate's configuration.
/// </remarks>
/// <param name="Id">Hub-assigned store id.</param>
/// <param name="Code">Short code, e.g. <c>CT01</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="IsActive">False when the store is closed but retained for history.</param>
public sealed record SyncStoreSummary(string Id, string Code, string Name, bool IsActive);

/// <summary>
/// The estate's store directory, plus which of them the caller is.
/// </summary>
/// <param name="StoreId">The calling device's own store, so it can tell itself apart.</param>
/// <param name="Stores">Every store known to the hub, ordered by code.</param>
public sealed record SyncStoreDirectoryResponse(
    string StoreId,
    IReadOnlyList<SyncStoreSummary> Stores);

/// <summary>
/// Header names used by the sync endpoints.
/// </summary>
/// <remarks>
/// The store is taken from the authenticated credential and never from the request body:
/// a terminal in a physically insecure shop must not be able to write into another
/// store's books by editing a payload.
/// </remarks>
public static class SyncHeaders
{
    /// <summary>Bearer token issued at enrolment.</summary>
    public const string Authorization = "Authorization";

    /// <summary>Store the credential is scoped to. Echoed for diagnostics only.</summary>
    public const string StoreId = "X-Pos-Store";

    /// <summary>Terminal identity, used to reject a token replayed from another device.</summary>
    public const string TerminalId = "X-Pos-Terminal";

    /// <summary>
    /// The refresh token. Sent only when rotating a credential, and never on an ordinary
    /// sync request, so a secret that leaks from a request log cannot mint a new one.
    /// </summary>
    public const string RefreshToken = "X-Pos-Refresh";
}

/// <summary>
/// A request to replace this device's credential with a new one.
/// </summary>
/// <remarks>
/// <para>
/// The new credential is generated <em>by the terminal</em>, exactly as at enrolment, and only its
/// hashes are ever stored. That is what makes this endpoint safe to retry: a lost response leaves the
/// till holding the same pair it already sent, so retrying is the identical request rather than a
/// second rotation. Had the hub minted the secret, a lost response would have to be resolved by
/// issuing a third credential over a connection that has already shown it drops responses.
/// </para>
/// <para>
/// Both values are generated on the device and never sent anywhere else, so the hub cannot disclose a
/// credential it does not have.
/// </para>
/// </remarks>
/// <param name="NewSecret">The replacement bearer secret, 32 bytes of CSPRNG output.</param>
/// <param name="NewRefreshToken">The replacement refresh token, generated the same way.</param>
public sealed record DeviceRotateRequest(string NewSecret, string NewRefreshToken);

/// <summary>Result of a rotation.</summary>
/// <param name="DeviceId">The device whose credential changed.</param>
/// <param name="RotateAfter">
/// When the terminal should rotate again. Sent as a time rather than a duration so a till that was
/// switched off across the due date rotates on its next sync rather than a week later.
/// </param>
public sealed record DeviceRotateResponse(string DeviceId, DateTimeOffset RotateAfter);

/// <summary>
/// How long a device credential is meant to last, and how long the one it replaced still works.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the hub and the till so the two cannot disagree about when a rotation is due: a terminal
/// that thought its credential was good for a year against a hub that expected a week would be
/// refused with no way to tell why.
/// </para>
/// <para>
/// The grace window is not a security feature. It is the price of rotating over a connection that can
/// drop: a till whose rotation response was lost keeps syncing on the superseded secret and retries
/// the identical rotation later, instead of needing a head-office code and a site visit. A shorter
/// window is a shorter exposure and a greater chance of a till that has to be enrolled by hand.
/// </para>
/// </remarks>
public static class SyncCredentialPolicy
{
    /// <summary>How long a credential is used before the terminal rotates it.</summary>
    public static readonly TimeSpan RotationInterval = TimeSpan.FromDays(7);

    /// <summary>How long the credential a rotation replaced is still accepted.</summary>
    public static readonly TimeSpan PreviousSecretGrace = TimeSpan.FromDays(14);

    /// <summary>
    /// The shortest secret either side will accept, in characters.
    /// </summary>
    /// <remarks>
    /// The check exists on the hub because a terminal in the field may be older than the hub and
    /// should not be able to enrol itself with something weak. It exists here so the till refuses to
    /// generate one below it, rather than discovering the hub's opinion over the network.
    /// </remarks>
    public const int MinimumSecretLength = 32;
}
