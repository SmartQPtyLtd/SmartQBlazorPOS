// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Microsoft.EntityFrameworkCore;
using Pos.Infrastructure.Sync;
using Pos.Sync.Server.Data;

namespace Pos.Sync.Server.Endpoints;

/// <summary>
/// The rules for accepting a pushed record.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from the minimal-API endpoint deliberately. These rules decide whether a store's
/// books balance — a replayed push must not double-post a sale, and a legitimate sale must not be
/// refused — so they need to be callable from a test directly. When they lived inside the
/// endpoint delegate, the tests could only assert a hand-written copy of them, which is a test of
/// the copy rather than of the system.
/// </para>
/// <para>
/// The whole batch shares one <see cref="SyncSequenceLedger"/>, because some of the rules depend
/// on what earlier records in the same push already claimed.
/// </para>
/// </remarks>
public static class SyncIngest
{
    /// <summary>Record type that carries a business date and a total.</summary>
    public const string SaleEntityType = SyncPayloadIndexer.SaleEntityType;

    /// <summary>
    /// Applies the ingest rules to one record, staging it in the change tracker.
    /// </summary>
    /// <remarks>
    /// Does not save. The caller owns the transaction, so a batch is atomic: a terminal never
    /// ends up with half its trading day stored.
    /// </remarks>
    public static async Task<SyncRecordResult> ApplyAsync(
        SyncRecord record,
        SyncPrincipal principal,
        SyncDbContext db,
        SyncSequenceLedger sequences,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sequences);

        if (string.IsNullOrWhiteSpace(record.EntityId) || string.IsNullOrWhiteSpace(record.EntityType))
        {
            return new SyncRecordResult(
                record.EntityId, SyncRecordStatus.Rejected, "EntityId and EntityType are required.");
        }

        // TerminalId is the ordering key, so a record without one cannot be placed in its
        // terminal's stream — and because a push is one transaction, letting it through as a null
        // would fail the entire batch with a 500 rather than rejecting the one bad record. A
        // terminal that could not push a whole day's trading because one malformed record rode
        // along with it would have no way to recover on its own.
        if (string.IsNullOrWhiteSpace(record.TerminalId))
        {
            return new SyncRecordResult(
                record.EntityId, SyncRecordStatus.Rejected, "TerminalId is required.");
        }

        if (await sequences.IsAlreadyKnownAsync(db, record.EntityId, ct).ConfigureAwait(false))
        {
            // Not an error: this is the expected outcome of a retry after a lost response, and it
            // must look identical to the terminal either way.
            return new SyncRecordResult(record.EntityId, SyncRecordStatus.Duplicate);
        }

        var (businessDate, total) = SyncPayloadIndexer.Extract(record.EntityType, record.Payload);

        var entity = new SyncedRecordEntity
        {
            Id = record.EntityId,

            // The store comes from the credential and never from the payload, so a terminal in a
            // physically insecure shop cannot write into another store's books by editing a body.
            StoreId = principal.StoreId,
            TerminalId = record.TerminalId,
            EntityType = record.EntityType,
            TerminalSeq = await sequences
                .NextForAsync(db, record.TerminalId, principal.StoreId, record.TerminalSeq, logger, ct)
                .ConfigureAwait(false),
            Payload = record.Payload,
            ReceivedAt = DateTimeOffset.UtcNow,
            BusinessDate = businessDate,
            Total = total,
        };

        db.Records.Add(entity);
        sequences.NoteAccepted(record.EntityId);

        var now = DateTimeOffset.UtcNow;

        // One change-log row per accepted record. Pull cursors are positions in this table, which
        // is why ordering is total and never depends on a wall clock.
        db.ChangeLog.Add(new ChangeLogEntity
        {
            Stream = SyncStreams.Sales,
            EntityType = record.EntityType,
            EntityId = record.EntityId,

            // Scoped to the store that authored it, which is the credential's store and never the
            // payload's.
            StoreId = principal.StoreId,
            Payload = record.Payload,
            CreatedAt = now,
        });

        await RelayTransferAsync(record, principal, db, logger, now, ct).ConfigureAwait(false);

        return new SyncRecordResult(record.EntityId, SyncRecordStatus.Accepted);
    }

    /// <summary>
    /// Delivers a stock transfer to the store it is addressed to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one place the hub deliberately lets a record cross a store boundary, and it does so by
    /// re-scoping rather than by widening the pull.
    /// </para>
    /// <para>
    /// A terminal's pull returns its own store's changes and global ones. That is a security
    /// property — one shop cannot read another's takings — so it must not be relaxed for the sake
    /// of transfers. Instead the hub writes a <b>second</b> change-log row for the same record,
    /// scoped to the destination store, and the existing scoping rule delivers it. The sending
    /// store keeps its own row, so both ends see the document and neither sees anything else.
    /// </para>
    /// <para>
    /// The destination must be a store the hub knows about, and it must not be the sending store.
    /// A transfer addressed nowhere would otherwise sit in the change log forever, invisible to
    /// everyone, while the sending store's stock had already left.
    /// </para>
    /// </remarks>
    private static async Task RelayTransferAsync(
        SyncRecord record,
        SyncPrincipal principal,
        SyncDbContext db,
        ILogger logger,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (!string.Equals(record.EntityType, StockTransferEntityType, StringComparison.Ordinal))
        {
            return;
        }

        var destination = TransferRelay.ReadDestinationStoreId(record.Payload);

        if (destination is null)
        {
            // Stored and synced as normal, just not relayed. Refusing the record would strand a
            // transfer on the sending terminal, which is worse than one that needs looking at.
            ServerLog.TransferNotRelayed(logger, record.EntityId, "no destination store in the payload");
            return;
        }

        if (StoreIdFormat.Same(destination, principal.StoreId))
        {
            ServerLog.TransferNotRelayed(logger, record.EntityId, "the destination is the sending store");
            return;
        }

        // Resolved by identity rather than by string, then relayed under the hub's own spelling.
        // A till writes store ids the way its domain type renders them — with dashes — while the hub
        // mints them without. Comparing the two as strings matched nothing, so a transfer was
        // accepted, stored, and never relayed: indistinguishable from one that had been sent.
        //
        // The estate's store list is read in full rather than queried per record because it is a
        // franchise's worth of branches, not a table of transactions, and being able to compare
        // ids as the identifiers they are is worth more than the round trip.
        var storeIds = await db.Stores
            .AsNoTracking()
            .Select(s => s.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var known = storeIds.FirstOrDefault(id => StoreIdFormat.Same(id, destination));

        if (known is null)
        {
            ServerLog.TransferNotRelayed(logger, record.EntityId, $"store '{destination}' is not known to this hub");
            return;
        }

        db.ChangeLog.Add(new ChangeLogEntity
        {
            Stream = SyncStreams.Sales,

            // The same record type and id, so the receiving terminal applies it as the transfer it
            // is rather than as a second document.
            EntityType = record.EntityType,
            EntityId = record.EntityId,

            // The hub's spelling, because this is what the receiving terminal's pull is scoped by.
            StoreId = known,
            Payload = record.Payload,
            CreatedAt = now,
        });

        ServerLog.TransferRelayed(logger, record.EntityId, principal.StoreId, known);
    }

    /// <summary>Record type a stock transfer travels under.</summary>
    private const string StockTransferEntityType = "stockTransfer";
}

/// <summary>
/// Tracks what a single push batch has already claimed.
/// </summary>
/// <remarks>
/// <para>
/// Both halves exist because reading only the database is wrong inside a batch: a record added
/// moments ago is still unsaved, so a query cannot see it. Two records that both need a
/// reassigned position would then be handed the same one, and the batch would fail on the unique
/// index with the very collision the reassignment exists to absorb.
/// </para>
/// <para>
/// The same applies to the dedupe check. A record that appears twice in one batch is not in the
/// database yet, so both copies would pass and the second would fail on the primary key.
/// </para>
/// </remarks>
public sealed class SyncSequenceLedger
{
    private readonly Dictionary<string, long> _highest = new(StringComparer.Ordinal);
    private readonly HashSet<string> _accepted = new(StringComparer.Ordinal);

    /// <summary>True when this record is already stored, or was accepted earlier in this batch.</summary>
    public async Task<bool> IsAlreadyKnownAsync(SyncDbContext db, string entityId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (_accepted.Contains(entityId))
        {
            return true;
        }

        return await db.Records
            .AsNoTracking()
            .AnyAsync(r => r.Id == entityId, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Notes that a record has been staged, so a repeat within the batch dedupes.</summary>
    public void NoteAccepted(string entityId) => _accepted.Add(entityId);

    /// <summary>
    /// Returns the position to store a record at, absorbing a terminal that restarted its counter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The position a terminal asks for is honoured when it moves the stream forward. When it is at
    /// or below the highest already stored, the terminal has restarted — the ordinary consequence
    /// of a page reload for any terminal that keeps its counter in memory — and it is given the
    /// next free position instead.
    /// </para>
    /// <para>
    /// Refusing it would be worse than wrong. <c>(terminalId, terminalSeq)</c> is unique, a push is
    /// one transaction, so a collision fails the entire batch and the sale is retried until it is
    /// parked as dead. But the sequence number is only ordering metadata; the entity id is the
    /// identity. Losing a real sale to protect a position marker is the wrong trade, and the
    /// terminal's outbox would never recover on its own.
    /// </para>
    /// <para>
    /// Reassigning rather than skipping is also what keeps a restart from looking like a gap. A
    /// gap is the hub's evidence that records were lost in transit, so an alert raised on every
    /// browser refresh would be an alert nobody reads.
    /// </para>
    /// </remarks>
    public async Task<long> NextForAsync(
        SyncDbContext db,
        string terminalId,
        string storeId,
        long requested,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (!_highest.TryGetValue(terminalId, out var highest))
        {
            var tracked = await db.TerminalSequences
                .FirstOrDefaultAsync(s => s.TerminalId == terminalId, ct)
                .ConfigureAwait(false);

            highest = tracked?.HighestSeq ?? 0;
        }

        var actual = requested;

        if (requested <= highest)
        {
            actual = highest + 1;

            ServerLog.SequenceReset(logger, terminalId, storeId, requested, highest, actual);
        }
        else if (requested > highest + 1)
        {
            // A forward jump is the one thing a sequence number is genuinely for: it means records
            // the terminal authored never arrived. Surfaced rather than tolerated, because the
            // alternative is discovering it during a stock count months later.
            ServerLog.SequenceGap(logger, terminalId, storeId, highest + 1, requested);
        }

        _highest[terminalId] = actual;

        var row = await db.TerminalSequences
            .FirstOrDefaultAsync(s => s.TerminalId == terminalId, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            db.TerminalSequences.Add(new TerminalSequenceEntity
            {
                TerminalId = terminalId,
                StoreId = storeId,
                HighestSeq = actual,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            return actual;
        }

        if (actual > row.HighestSeq)
        {
            row.HighestSeq = actual;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return actual;
    }
}
