// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Infrastructure.Storage;

namespace Pos.Infrastructure.Sync;

/// <summary>
/// Talks to the hub on behalf of a terminal.
/// </summary>
/// <remarks>
/// Abstracted so the sync loop can be tested without a network, and so the browser
/// implementation (which must go through <c>fetch</c>) stays separate from the logic that
/// decides what to send and when to advance a cursor.
/// </remarks>
public interface ISyncTransport
{
    /// <summary>
    /// Sends a batch of records.
    /// </summary>
    /// <remarks>
    /// Must be safe to retry: the hub deduplicates by client-generated id, so a batch that
    /// was actually received but whose response was lost can be re-sent unchanged.
    /// </remarks>
    Task<SyncPushResponse> PushAsync(SyncPushRequest request, CancellationToken ct = default);

    /// <summary>Retrieves changes after a cursor for one stream.</summary>
    Task<SyncPullResponse> PullAsync(string stream, string? cursor, int limit, CancellationToken ct = default);
}

/// <summary>How a sync pass finished.</summary>
/// <param name="Pushed">Records the hub accepted or already knew about.</param>
/// <param name="Rejected">Records the hub refused.</param>
/// <param name="Applied">Changes pulled from the hub.</param>
/// <param name="Remaining">Entries still queued after this pass.</param>
/// <param name="Cursor">The cursor after this pass.</param>
/// <param name="Error">Set when the pass could not reach the hub.</param>
public readonly record struct SyncSessionResult(
    int Pushed,
    int Rejected,
    int Applied,
    int Remaining,
    string? Cursor,
    string? Error = null)
{
    /// <summary>True when the pass completed and nothing is left queued.</summary>
    public bool IsFullySynced => Error is null && Remaining == 0 && Rejected == 0;

    /// <summary>A pass that could not reach the hub.</summary>
    public static SyncSessionResult Offline(string error) => new(0, 0, 0, 0, null, error);
}

/// <summary>
/// Applies changes pulled from the hub to the local replica.
/// </summary>
/// <remarks>
/// Separate from the transport so a stream can be added without touching the sync loop.
/// </remarks>
public interface ISyncChangeApplier
{
    /// <summary>Applies one change. Returns true when it was understood.</summary>
    Task<bool> ApplyAsync(SyncChange change, CancellationToken ct = default);
}

/// <summary>Ignores every change. The default until a stream has an applier.</summary>
public sealed class NullChangeApplier : ISyncChangeApplier
{
    public static NullChangeApplier Instance { get; } = new();

    public Task<bool> ApplyAsync(SyncChange change, CancellationToken ct = default) =>
        Task.FromResult(false);
}

/// <summary>
/// Work a terminal does before a sync pass, if it is due.
/// </summary>
/// <remarks>
/// Exists so credential rotation happens wherever a pass happens rather than at each call site. The
/// till syncs after every sale, from two buttons, and from its startup path; a rotation wired into
/// those individually would be forgotten by the next one added, and the failure mode of forgetting is
/// a credential that quietly never expires.
/// </remarks>
public interface ISyncPreflight
{
    /// <summary>
    /// Does whatever is due. Called once at the start of each pass.
    /// </summary>
    /// <remarks>
    /// Implementations must not throw for anything that is merely inconvenient: a preflight that fails
    /// a pass would turn a rotation the hub refused into a shop that cannot take money.
    /// </remarks>
    Task PrepareAsync(CancellationToken ct = default);
}

/// <summary>Does nothing. The default for a terminal with no maintenance to do.</summary>
public sealed class NullSyncPreflight : ISyncPreflight
{
    public static NullSyncPreflight Instance { get; } = new();

    public Task PrepareAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Drains the local outbox to the hub and pulls changes back.
/// </summary>
/// <remarks>
/// <para>
/// The whole reason the terminal can keep trading through an outage. It is written to be
/// <b>safe to run repeatedly and safe to interrupt</b>: an entry leaves the outbox only
/// after the hub has explicitly acknowledged it.
/// </para>
/// <para>
/// The ordering matters. Pushing before pulling means the terminal's own writes are
/// already on the hub when it advances its cursor, so it never re-pulls and re-applies its
/// own sales.
/// </para>
/// </remarks>
public sealed class SyncClient(
    ILocalStore store,
    ISyncTransport transport,
    ISyncChangeApplier? changeApplier = null,
    ISyncPreflight? preflight = null)
{
    /// <summary>Entries sent in one batch. Bounded so a large backlog makes progress visibly.</summary>
    private const int BatchSize = 50;

    /// <summary>Changes requested per pull.</summary>
    private const int PullLimit = 200;

    /// <summary>
    /// Safety bound on pull iterations.
    /// </summary>
    /// <remarks>
    /// A paginating loop that trusts the server to eventually say "no more" can spin
    /// forever against a buggy or hostile hub. This bounds one pass; the next pass
    /// continues from the stored cursor, so nothing is lost by stopping early.
    /// </remarks>
    private const int MaxPullPages = 50;

    private readonly ILocalStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ISyncTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly ISyncChangeApplier _applier = changeApplier ?? NullChangeApplier.Instance;
    private readonly ISyncPreflight _preflight = preflight ?? NullSyncPreflight.Instance;

    /// <summary>
    /// Runs one sync pass: push everything queued, then pull what the hub has.
    /// </summary>
    /// <remarks>
    /// A pass that cannot reach the hub reports itself as offline rather than throwing. A
    /// till in a shop with no connectivity is the normal case, not an error worth
    /// interrupting a cashier over.
    /// </remarks>
    public async Task<SyncSessionResult> RunOnceAsync(CancellationToken ct = default)
    {
        // Before anything is pushed, in case the credential is due. Its failure is not the pass's
        // failure: a credential inside its grace window still works, and the alternative is a till that
        // refuses to send a sale because a housekeeping call went wrong.
        try
        {
            await _preflight.PrepareAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SyncAuthorisationException)
        {
            // The hub has revoked this device. Let the pass report it the same way a push would, so the
            // screen that handles revocation is reached by one path rather than two.
            throw;
        }
        catch
        {
            // Retried on the next pass.
        }

        var pushed = 0;
        var rejected = 0;
        string? cursor = null;

        var pending = await _store.PeekOutboxAsync(BatchSize, ct).ConfigureAwait(false);

        if (pending.Count > 0)
        {
            var records = new List<SyncRecord>(pending.Count);

            foreach (var entry in pending)
            {
                // Read from local storage at send time. An entry whose record has vanished
                // is dropped from the queue rather than sent as an empty payload, because a
                // sale with no contents is worse than no sale at all.
                var payload = await _store.GetSyncPayloadAsync(entry.EntityId, ct).ConfigureAwait(false);

                if (payload is null)
                {
                    await _store
                        .MarkOutboxFailedAsync(entry.Id, "The record is no longer present in local storage.", ct)
                        .ConfigureAwait(false);

                    continue;
                }

                records.Add(new SyncRecord(
                    EntityType: entry.EntityType,
                    EntityId: entry.EntityId,
                    TerminalId: entry.TerminalId,
                    TerminalSeq: entry.TerminalSeq,
                    Payload: payload));
            }

            if (records.Count == 0)
            {
                var idle = await _store.GetOutboxSummaryAsync(ct).ConfigureAwait(false);
                return new SyncSessionResult(0, 0, 0, idle.Pending, null);
            }

            var request = new SyncPushRequest(
                BatchId: Guid.CreateVersion7().ToString("N"),
                Records: records);

            SyncPushResponse response;
            try
            {
                response = await _transport.PushAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Connectivity failure. Nothing is dequeued and nothing is marked failed:
                // the queue is simply retried on the next pass.
                var summary = await _store.GetOutboxSummaryAsync(ct).ConfigureAwait(false);
                return SyncSessionResult.Offline(ex.Message) with { Remaining = summary.Pending };
            }

            cursor = response.Cursor;

            await ApplyPushOutcomeAsync(pending, records, response, ct).ConfigureAwait(false);

            pushed = response.Results.Count(r => r.Status != SyncRecordStatus.Rejected);
            rejected = response.Results.Count(r => r.Status == SyncRecordStatus.Rejected);
        }

        var applied = await PullAsync(ct).ConfigureAwait(false);

        var after = await _store.GetOutboxSummaryAsync(ct).ConfigureAwait(false);

        return new SyncSessionResult(
            Pushed: pushed,
            Rejected: rejected,
            Applied: applied,
            Remaining: after.Pending,
            Cursor: cursor);
    }

    /// <summary>
    /// Records the hub's verdict on each entry and dequeues the ones it owns.
    /// </summary>
    /// <remarks>
    /// A duplicate is dequeued just like an acceptance: the hub already holds the record,
    /// so keeping it queued would resend it forever. Rejections stay out of the outbox only
    /// once they have exhausted their retries.
    /// </remarks>
    private async Task ApplyPushOutcomeAsync(
        IReadOnlyList<OutboxEntry> pending,
        IReadOnlyList<SyncRecord> sent,
        SyncPushResponse response,
        CancellationToken ct)
    {
        var byId = response.Results.ToDictionary(r => r.EntityId, StringComparer.Ordinal);

        // Only entries that were actually in this batch can be resolved by its response.
        var sentIds = sent.Select(r => r.EntityId).ToHashSet(StringComparer.Ordinal);

        var acknowledged = new List<string>();
        var failures = new List<(string Id, string Error)>();

        foreach (var entry in pending)
        {
            if (!sentIds.Contains(entry.EntityId))
            {
                continue;
            }

            if (!byId.TryGetValue(entry.EntityId, out var result))
            {
                // The hub said nothing about this record. Leave it queued rather than
                // guessing; guessing wrong loses a sale.
                continue;
            }

            if (result.Status == SyncRecordStatus.Rejected)
            {
                failures.Add((entry.Id, result.Error ?? "The hub rejected the record."));
            }
            else
            {
                acknowledged.Add(entry.Id);
            }
        }

        if (acknowledged.Count > 0)
        {
            await _store.AcknowledgeOutboxAsync(acknowledged, ct).ConfigureAwait(false);
        }

        foreach (var (id, error) in failures)
        {
            await _store.MarkOutboxFailedAsync(id, error, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Pulls changes for every stream until the hub has nothing more.
    /// </summary>
    private async Task<int> PullAsync(CancellationToken ct)
    {
        var applied = 0;

        foreach (var stream in new[] { SyncStreams.Catalog, SyncStreams.Sales })
        {
            var cursor = await _store.GetCursorAsync(stream, ct).ConfigureAwait(false);

            for (var page = 0; page < MaxPullPages; page++)
            {
                ct.ThrowIfCancellationRequested();

                SyncPullResponse response;
                try
                {
                    response = await _transport
                        .PullAsync(stream, cursor, PullLimit, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Partial progress is persisted per page, so stopping here loses
                    // nothing; the next pass resumes from the stored cursor.
                    break;
                }

                if (response.ResetRequired)
                {
                    // The cursor is older than the hub's retained log, so the replica is
                    // incomplete. Clearing the cursor forces a full re-pull from the start
                    // rather than silently continuing with a hole in the data.
                    await _store.SetCursorAsync(stream, "0", ct).ConfigureAwait(false);
                    cursor = "0";
                    continue;
                }

                foreach (var change in response.Changes)
                {
                    await _applier.ApplyAsync(change, ct).ConfigureAwait(false);
                    applied++;
                }

                // The cursor advances only after the changes have been handed to the
                // applier, so an interruption re-delivers rather than skips.
                if (!string.IsNullOrEmpty(response.NextCursor))
                {
                    await _store.SetCursorAsync(stream, response.NextCursor, ct).ConfigureAwait(false);
                    cursor = response.NextCursor;
                }

                if (!response.HasMore)
                {
                    break;
                }
            }
        }

        return applied;
    }
}
