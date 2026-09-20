// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pos.Infrastructure.Sync;
using Pos.Sync.Server.Data;

namespace Pos.Sync.Server.Endpoints;

/// <summary>
/// The push and pull endpoints that terminals synchronise against.
/// </summary>
public static class SyncEndpoints
{
    /// <summary>Maximum records accepted in one push, to bound a single transaction.</summary>
    private const int MaxBatchSize = 500;

    /// <summary>Maximum changes returned by one pull.</summary>
    private const int MaxPullSize = 500;

    private const string SalesStream = SyncStreams.Sales;
    private const string CatalogStream = SyncStreams.Catalog;

    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/sync");

        group.MapPost("/push", PushAsync);
        group.MapGet("/pull", PullAsync);
        group.MapGet("/status", StatusAsync);
        group.MapGet("/stores", StoresAsync);
    }

    /// <summary>
    /// Accepts a batch of records from a terminal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotent by construction. A duplicate is detected by the primary key on the
    /// client-generated id and reported as <see cref="SyncRecordStatus.Duplicate"/> with
    /// the same shape of response as a first acceptance. A terminal that retries after a
    /// lost response therefore cannot double-post a sale.
    /// </para>
    /// <para>
    /// The whole batch is one transaction, so a terminal never ends up with half its
    /// records stored.
    /// </para>
    /// </remarks>
    private static async Task<IResult> PushAsync(
        SyncPushRequest request,
        HttpContext context,
        SyncDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Sync.Push");

        var principal = await SyncAuthentication
            .AuthenticateAsync(context, db, ct)
            .ConfigureAwait(false);

        if (principal is null)
        {
            return SyncAuthentication.Unauthorised("A valid device credential is required.");
        }

        var identity = principal.Value;

        if (identity.UsedPreviousSecret)
        {
            // The lost-rotation-response path, seen from the hub. Logged rather than treated as normal:
            // it means a rotation happened that the till never heard about, and it stops being accepted
            // when the grace window closes.
            ServerLog.SupersededSecretInUse(logger, identity.DeviceId);
        }

        if (request.Records.Count == 0)
        {
            return Results.Ok(new SyncPushResponse([], await CurrentCursorAsync(db, ct).ConfigureAwait(false)));
        }

        if (request.Records.Count > MaxBatchSize)
        {
            return Results.Problem(
                title: "Batch too large",
                detail: $"A push may contain at most {MaxBatchSize} records; received {request.Records.Count}.",
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var results = new List<SyncRecordResult>(request.Records.Count);

        // One ledger per batch, because some ingest rules depend on what earlier records in the
        // same push already claimed — and a record added to the change tracker is not yet visible
        // to a query.
        var sequences = new SyncSequenceLedger();

        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        foreach (var record in request.Records)
        {
            results.Add(await SyncIngest
                .ApplyAsync(record, identity, db, sequences, logger, ct)
                .ConfigureAwait(false));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        var cursor = await CurrentCursorAsync(db, ct).ConfigureAwait(false);

        return Results.Ok(new SyncPushResponse(results, cursor));
    }

    /// <summary>
    /// Returns changes after a cursor.
    /// </summary>
    /// <remarks>
    /// The cursor is opaque to the terminal: it is a position in the hub's change log and
    /// the terminal must not interpret it beyond passing it back. A terminal that presents
    /// a cursor the hub has already trimmed is told to reset rather than silently
    /// receiving an incomplete history.
    /// </remarks>
    private static async Task<IResult> PullAsync(
        HttpContext context,
        SyncDbContext db,
        string? cursor = null,
        string stream = SalesStream,
        int limit = MaxPullSize,
        CancellationToken ct = default)
    {
        var principal = await SyncAuthentication
            .AuthenticateAsync(context, db, ct)
            .ConfigureAwait(false);

        if (principal is null)
        {
            return SyncAuthentication.Unauthorised("A valid device credential is required.");
        }

        var identity = principal.Value;
        var take = Math.Clamp(limit, 1, MaxPullSize);

        // A missing or unparseable cursor means "from the beginning of the log". The
        // terminal is expected to bootstrap with a snapshot, but starting at zero is
        // still correct, just slower.
        long from = 0;
        if (!string.IsNullOrWhiteSpace(cursor) && !long.TryParse(cursor, out from))
        {
            return Results.Problem(
                title: "Invalid cursor",
                detail: "The cursor must be an opaque value previously returned by the hub.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var streamName = string.IsNullOrWhiteSpace(stream) ? SalesStream : stream;
        if (streamName is not (SalesStream or CatalogStream or SyncStreams.Global))
        {
            return Results.Problem(
                title: "Unknown stream",
                detail: $"'{streamName}' is not a stream this hub serves.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var query = db.ChangeLog
            .AsNoTracking()
            .Where(c => c.ChangeSeq > from);

        // Store-scoped streams only expose this store's changes plus global ones.
        query = streamName == SyncStreams.Global
            ? query.Where(c => c.StoreId == null)
            : query.Where(c => c.Stream == streamName && (c.StoreId == identity.StoreId || c.StoreId == null));

        // Fetch one extra to detect whether more work is already waiting.
        var rows = await query
            .OrderBy(c => c.ChangeSeq)
            .Take(take + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var hasMore = rows.Count > take;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var changes = rows
            .Select(c => new SyncChange(c.ChangeSeq, c.EntityType, c.EntityId, c.StoreId, c.Payload))
            .ToArray();

        var nextCursor = changes.Length > 0
            ? changes[^1].ChangeSeq.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : from.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return Results.Ok(new SyncPullResponse(changes, nextCursor, hasMore));
    }

    /// <summary>
    /// Reports the hub's current position and what it holds for this store.
    /// </summary>
    /// <remarks>
    /// Lets a terminal show a sync indicator and lets an operator confirm that a day's
    /// trading has actually landed before cashing up.
    /// </remarks>
    private static async Task<IResult> StatusAsync(
        HttpContext context,
        SyncDbContext db,
        CancellationToken ct)
    {
        var principal = await SyncAuthentication
            .AuthenticateAsync(context, db, ct)
            .ConfigureAwait(false);

        if (principal is null)
        {
            return SyncAuthentication.Unauthorised("A valid device credential is required.");
        }

        var identity = principal.Value;

        return Results.Ok(new
        {
            storeId = identity.StoreId,
            terminalId = identity.DeviceId,
            cursor = await CurrentCursorAsync(db, ct).ConfigureAwait(false),
            recordCount = await db.Records.CountAsync(r => r.StoreId == identity.StoreId, ct).ConfigureAwait(false),
            serverTime = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>
    /// The estate's store directory: which stores exist, so a till can address a transfer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately separate from the head-office store register. That register carries the
    /// estate's tax configuration, currency and registration numbers and is therefore gated
    /// behind the head-office token — a secret that must never be handed to a till, because a
    /// till is a browser in a shop. But a shop cannot send stock to another branch it cannot
    /// name, so an enrolled device is entitled to the branch list itself.
    /// </para>
    /// <para>
    /// The projection is the whole point: id, code, name and whether the store is trading.
    /// Everything else the hub knows about a store is withheld, so widening this response is a
    /// deliberate act rather than an accident of returning the entity.
    /// </para>
    /// </remarks>
    private static async Task<IResult> StoresAsync(
        HttpContext context,
        SyncDbContext db,
        CancellationToken ct)
    {
        var principal = await SyncAuthentication
            .AuthenticateAsync(context, db, ct)
            .ConfigureAwait(false);

        if (principal is null)
        {
            return SyncAuthentication.Unauthorised("A valid device credential is required.");
        }

        var stores = await db.Stores
            .AsNoTracking()
            .OrderBy(s => s.Code)
            .Select(s => new SyncStoreSummary(s.Id, s.Code, s.Name, s.IsActive))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Results.Ok(new SyncStoreDirectoryResponse(principal.Value.StoreId, stores));
    }

    /// <summary>
    /// The hub's current change-log position.
    /// </summary>
    /// <remarks>
    /// Returned from a push so a terminal can advance its cursor past its own writes
    /// instead of pulling them back.
    /// </remarks>
    private static async Task<string> CurrentCursorAsync(SyncDbContext db, CancellationToken ct)
    {
        var highest = await db.ChangeLog
            .AsNoTracking()
            .OrderByDescending(c => c.ChangeSeq)
            .Select(c => (long?)c.ChangeSeq)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return (highest ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
