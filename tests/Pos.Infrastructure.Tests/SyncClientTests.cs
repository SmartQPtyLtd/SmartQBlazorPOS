// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;
using Pos.Infrastructure.Checkout;
using Pos.Infrastructure.Storage;
using Pos.Infrastructure.Sync;

namespace Pos.Infrastructure.Tests;

/// <summary>
/// A scriptable hub, so the sync loop can be driven without a network.
/// </summary>
internal sealed class FakeSyncTransport : ISyncTransport
{
    /// <summary>Every push received, in order. Lets a test assert what was actually sent.</summary>
    public List<SyncPushRequest> Pushes { get; } = [];

    /// <summary>Every pull received, as (stream, cursor).</summary>
    public List<(string Stream, string? Cursor)> Pulls { get; } = [];

    /// <summary>Records the hub already holds, keyed by entity id.</summary>
    public HashSet<string> Stored { get; } = new(StringComparer.Ordinal);

    /// <summary>Set to simulate the hub being unreachable.</summary>
    public Exception? PushFailure { get; set; }

    public Exception? PullFailure { get; set; }

    /// <summary>Records the hub will reject, keyed by entity id, with the reason.</summary>
    public Dictionary<string, string> Rejected { get; } = new(StringComparer.Ordinal);

    /// <summary>Changes the hub will return per stream, keyed by stream name.</summary>
    public Dictionary<string, List<SyncChange>> ChangesByStream { get; } = new(StringComparer.Ordinal);

    /// <summary>Cursor the hub reports after a push.</summary>
    public string PushCursor { get; set; } = "100";

    /// <summary>
    /// When set, the hub ingests the records but the response never arrives.
    /// </summary>
    /// <remarks>
    /// The exact failure the idempotency design exists for: the terminal cannot tell
    /// whether the push landed, so it must retry and the hub must deduplicate.
    /// </remarks>
    public bool LoseResponseAfterStoring { get; set; }

    /// <summary>Simulates the terminal exceeding the hub's batch limit.</summary>
    public int MaxRecordsPerPush { get; set; } = int.MaxValue;

    public Task<SyncPushResponse> PushAsync(SyncPushRequest request, CancellationToken ct = default)
    {
        Pushes.Add(request);

        if (PushFailure is { } failure)
        {
            throw failure;
        }

        if (request.Records.Count > MaxRecordsPerPush)
        {
            throw new InvalidOperationException(
                $"Batch of {request.Records.Count} exceeds the limit of {MaxRecordsPerPush}.");
        }

        var results = new List<SyncRecordResult>(request.Records.Count);

        foreach (var record in request.Records)
        {
            if (Rejected.TryGetValue(record.EntityId, out var reason))
            {
                results.Add(new SyncRecordResult(record.EntityId, SyncRecordStatus.Rejected, reason));
                continue;
            }

            // Dedupe by the client-generated id, exactly as the hub does.
            var status = Stored.Add(record.EntityId)
                ? SyncRecordStatus.Accepted
                : SyncRecordStatus.Duplicate;

            results.Add(new SyncRecordResult(record.EntityId, status));
        }

        if (LoseResponseAfterStoring)
        {
            throw new HttpRequestException("The connection was reset before the response arrived.");
        }

        return Task.FromResult(new SyncPushResponse(results, PushCursor));
    }

    public Task<SyncPullResponse> PullAsync(
        string stream,
        string? cursor,
        int limit,
        CancellationToken ct = default)
    {
        Pulls.Add((stream, cursor));

        if (PullFailure is { } failure)
        {
            throw failure;
        }

        if (!ChangesByStream.TryGetValue(stream, out var changes) || changes.Count == 0)
        {
            return Task.FromResult(new SyncPullResponse([], cursor ?? "0", false));
        }

        var from = long.TryParse(cursor, out var parsed) ? parsed : 0;
        var page = changes.Where(c => c.ChangeSeq > from).Take(limit).ToArray();
        var next = page.Length > 0
            ? page[^1].ChangeSeq.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : (cursor ?? "0");

        ChangesByStream[stream] =
            [.. changes.Where(c => c.ChangeSeq > (page.Length > 0 ? page[^1].ChangeSeq : from))];

        return Task.FromResult(new SyncPullResponse(page, next, false));
    }
}

/// <summary>Records the changes handed to it.</summary>
internal sealed class RecordingChangeApplier : ISyncChangeApplier
{
    public List<SyncChange> Applied { get; } = [];

    public Task<bool> ApplyAsync(SyncChange change, CancellationToken ct = default)
    {
        Applied.Add(change);
        return Task.FromResult(true);
    }
}

/// <summary>
/// Sync loop tests.
/// </summary>
/// <remarks>
/// The rule under test throughout: a queued record leaves the outbox only once the hub has
/// explicitly acknowledged it. Everything else in the offline design follows from that.
/// </remarks>
public sealed class SyncClientTests
{
    private const string Zar = "ZAR";
    private static readonly TaxRate Vat15 = new("VAT", 0.15m);

    private sealed class FixedTerminal(string id) : ITerminalIdentity
    {
        public string TerminalId { get; } = id;
    }

    private sealed class StubNumbers : ISaleNumberSource
    {
        private int _next;

        public Task<SaleNumber> NextAsync(
            StoreId storeId, string storeCode, DateOnly businessDate, CancellationToken ct = default) =>
            Task.FromResult(new SaleNumber(storeCode, businessDate, ++_next));
    }

    private static Store NewStore() => new()
    {
        Id = StoreId.New(),
        Name = "CORNER STORE",
        Code = "CT01",
        Currency = Zar,
        TaxMode = TaxMode.Inclusive,
        DefaultTaxRate = Vat15,
        ReceiptColumns = 48,
    };

    private static async Task<(CheckoutRecordingService Service, InMemoryLocalStore Store)> RecordSaleAsync(
        InMemoryLocalStore local,
        Store store,
        string productName = "Cola",
        decimal price = 15.00m)
    {
        var service = new CheckoutRecordingService(local, new StubNumbers(), new FixedTerminal("TILL-1"));

        var cart = new Cart(store.Id, store.Currency, store.TaxMode);
        cart.Add(ProductId.New(), "1234567890123", productName, Vat15, new Money(price, Zar), 1m);

        await service.RecordSaleAsync(
            cart,
            [new Tender(TenderType.Cash, new Money(price, Zar), new Money(price, Zar))],
            store);

        return (service, local);
    }

    [Fact]
    public async Task A_queued_sale_is_pushed_and_then_removed_from_the_outbox()
    {
        var local = new InMemoryLocalStore();
        var store = NewStore();
        await RecordSaleAsync(local, store);

        var transport = new FakeSyncTransport();
        var client = new SyncClient(local, transport);

        var result = await client.RunOnceAsync();

        // One sale plus one stock movement.
        Assert.Equal(2, result.Pushed);
        Assert.Equal(0, result.Remaining);
        Assert.True((await local.GetOutboxSummaryAsync()).IsClear);
    }

    [Fact]
    public async Task An_unreachable_hub_leaves_the_queue_intact()
    {
        // A shop with no connectivity is the normal case, not an error. Nothing may be
        // discarded just because the hub could not be reached.
        var local = new InMemoryLocalStore();
        var store = NewStore();
        await RecordSaleAsync(local, store);

        var transport = new FakeSyncTransport { PushFailure = new HttpRequestException("No route to host.") };
        var client = new SyncClient(local, transport);

        var result = await client.RunOnceAsync();

        Assert.NotNull(result.Error);
        Assert.Equal(2, result.Remaining);

        var summary = await local.GetOutboxSummaryAsync();
        Assert.Equal(2, summary.Pending);
        Assert.Equal(0, summary.Dead);
    }

    [Fact]
    public async Task A_lost_response_is_retried_and_the_hub_deduplicates_it()
    {
        // The scenario the whole idempotency design exists for: the hub stored the sale but
        // the terminal never learned that. It must retry, and the retry must be harmless.
        var local = new InMemoryLocalStore();
        var store = NewStore();
        await RecordSaleAsync(local, store);

        var transport = new FakeSyncTransport { LoseResponseAfterStoring = true };
        var client = new SyncClient(local, transport);

        // First pass: the hub ingests but the response is lost.
        var first = await client.RunOnceAsync();
        Assert.NotNull(first.Error);
        Assert.Equal(2, first.Remaining);

        // Second pass: the hub recognises both records as duplicates.
        transport.LoseResponseAfterStoring = false;
        var second = await client.RunOnceAsync();

        Assert.Null(second.Error);
        Assert.Equal(0, second.Remaining);
        Assert.True((await local.GetOutboxSummaryAsync()).IsClear);

        // The hub holds each record exactly once.
        Assert.Equal(2, transport.Stored.Count);
    }

    [Fact]
    public async Task A_duplicate_acknowledgement_still_clears_the_queue()
    {
        // A duplicate means the hub already holds the record, so keeping it queued would
        // resend it on every pass forever.
        var local = new InMemoryLocalStore();
        var store = NewStore();
        await RecordSaleAsync(local, store);

        var transport = new FakeSyncTransport();
        var client = new SyncClient(local, transport);

        await client.RunOnceAsync();
        Assert.True((await local.GetOutboxSummaryAsync()).IsClear);
    }

    [Fact]
    public async Task A_rejected_record_cannot_clear_but_does_not_block_the_others()
    {
        // One poison record must not stop the rest of a store's trading from syncing.
        var local = new InMemoryLocalStore();
        var store = NewStore();
        await RecordSaleAsync(local, store);

        var pending = await local.PeekOutboxAsync();
        var saleEntry = pending.Single(e => e.EntityType == "sale");

        var transport = new FakeSyncTransport();
        transport.Rejected[saleEntry.EntityId] = "Duplicate receipt number.";

        var client = new SyncClient(local, transport);
        var result = await client.RunOnceAsync();

        Assert.Equal(1, result.Rejected);

        // The movement was accepted and dequeued; the rejected sale remains queued.
        var summary = await local.GetOutboxSummaryAsync();
        Assert.Equal(1, summary.Pending);
        Assert.Equal(0, summary.Dead);
    }

    [Fact]
    public async Task A_record_that_is_no_longer_stored_is_not_pushed_as_an_empty_payload()
    {
        // Sending a sale with no contents would be worse than not sending it, because the
        // hub would record a meaningless financial event.
        var local = new InMemoryLocalStore();
        var store = NewStore();
        await RecordSaleAsync(local, store);

        var transport = new FakeSyncTransport();
        var client = new SyncClient(local, transport);

        // Wipe the payloads but leave the queue, simulating corruption.
        await local.WipeAsync();

        var result = await client.RunOnceAsync();

        Assert.Equal(0, result.Pushed);
        Assert.Empty(transport.Pushes);
    }

    [Fact]
    public async Task Changes_are_pulled_and_the_cursor_is_persisted()
    {
        var local = new InMemoryLocalStore();
        var transport = new FakeSyncTransport();

        transport.ChangesByStream["catalog"] =
        [
            new SyncChange(1, "product", "p-1", null, """{"id":"p-1"}"""),
            new SyncChange(2, "product", "p-2", null, """{"id":"p-2"}"""),
        ];

        var applier = new RecordingChangeApplier();
        var client = new SyncClient(local, transport, applier);

        var result = await client.RunOnceAsync();

        Assert.Equal(2, result.Applied);
        Assert.Equal(2, applier.Applied.Count);
        Assert.Equal("2", await local.GetCursorAsync("catalog"));
    }

    [Fact]
    public async Task A_second_pass_does_not_re_pull_changes_already_applied()
    {
        // The cursor is what stops a terminal replaying its whole history on every sync.
        var local = new InMemoryLocalStore();
        var transport = new FakeSyncTransport();

        transport.ChangesByStream["catalog"] =
        [
            new SyncChange(1, "product", "p-1", null, """{"id":"p-1"}"""),
        ];

        var applier = new RecordingChangeApplier();
        var client = new SyncClient(local, transport, applier);

        await client.RunOnceAsync();
        await client.RunOnceAsync();

        Assert.Single(applier.Applied);
    }

    [Fact]
    public async Task A_pull_failure_does_not_discard_the_queued_push()
    {
        // Pushing is the part that must succeed for a store's takings to reach head office;
        // a failure to pull must not undo it.
        var local = new InMemoryLocalStore();
        var store = NewStore();
        await RecordSaleAsync(local, store);

        var transport = new FakeSyncTransport { PullFailure = new HttpRequestException("Timeout.") };
        var client = new SyncClient(local, transport);

        var result = await client.RunOnceAsync();

        // The push still happened and cleared the queue.
        Assert.Equal(2, result.Pushed);
        Assert.True((await local.GetOutboxSummaryAsync()).IsClear);
    }

    [Fact]
    public async Task Pushing_before_pulling_avoids_re_applying_the_terminals_own_sales()
    {
        // If the terminal pulled first it would receive its own just-pushed sale back and
        // store a duplicate of it locally.
        var local = new InMemoryLocalStore();
        var store = NewStore();
        await RecordSaleAsync(local, store);

        var transport = new FakeSyncTransport();
        var client = new SyncClient(local, transport);

        await client.RunOnceAsync();

        // The push was recorded before any pull was attempted.
        Assert.Single(transport.Pushes);
        Assert.NotEmpty(transport.Pulls);
    }

    [Fact]
    public async Task The_batch_is_bounded_so_a_large_backlog_still_makes_progress()
    {
        var local = new InMemoryLocalStore();
        var store = NewStore();

        for (var i = 0; i < 40; i++)
        {
            await RecordSaleAsync(local, store, $"Product {i}", 1.00m + i);
        }

        var transport = new FakeSyncTransport { MaxRecordsPerPush = 50 };
        var client = new SyncClient(local, transport);

        var first = await client.RunOnceAsync();

        Assert.NotNull(first.Cursor);
        Assert.Single(transport.Pushes);
        Assert.True(transport.Pushes[0].Records.Count <= 50);

        // Remaining entries drain on subsequent passes.
        for (var pass = 0; pass < 5; pass++)
        {
            if ((await local.GetOutboxSummaryAsync()).IsClear)
            {
                break;
            }

            await client.RunOnceAsync();
        }

        Assert.True((await local.GetOutboxSummaryAsync()).IsClear);
        Assert.All(transport.Pushes, p => Assert.True(p.Records.Count <= 50));
    }

    [Fact]
    public async Task Each_pushed_record_carries_its_own_payload()
    {
        // The outbox stores a reference; the payload is read back from local storage at
        // send time. An empty payload would be silently accepted by a lenient hub.
        var local = new InMemoryLocalStore();
        var store = NewStore();
        await RecordSaleAsync(local, store);

        var transport = new FakeSyncTransport();
        var client = new SyncClient(local, transport);

        await client.RunOnceAsync();

        var records = transport.Pushes[0].Records;
        Assert.Equal(2, records.Count);

        var sale = records.Single(r => r.EntityType == "sale");
        Assert.Contains("CT01", sale.Payload);
        Assert.NotEqual("{}", sale.Payload);

        var movement = records.Single(r => r.EntityType == "stockMovement");
        Assert.Contains("qtyDelta", movement.Payload);
        Assert.NotEqual("{}", movement.Payload);
    }

    [Fact]
    public async Task A_pass_with_nothing_queued_still_pulls_changes()
    {
        // A terminal that has sold nothing still needs catalogue and price updates.
        var local = new InMemoryLocalStore();
        var transport = new FakeSyncTransport();

        transport.ChangesByStream["catalog"] =
        [
            new SyncChange(1, "product", "p-1", null, """{"id":"p-1"}"""),
        ];

        var applier = new RecordingChangeApplier();
        var client = new SyncClient(local, transport, applier);

        var result = await client.RunOnceAsync();

        Assert.Equal(0, result.Pushed);
        Assert.Equal(1, result.Applied);
        Assert.Empty(transport.Pushes);
    }

    [Fact]
    public async Task Every_pushed_record_carries_the_terminal_identity()
    {
        // The hub orders records by (terminalId, terminalSeq), so a missing or wrong
        // terminal id would corrupt the ordering of a store's history.
        var local = new InMemoryLocalStore();
        var store = NewStore();
        await RecordSaleAsync(local, store);

        var transport = new FakeSyncTransport();
        await new SyncClient(local, transport).RunOnceAsync();

        Assert.All(transport.Pushes[0].Records, r => Assert.Equal("TILL-1", r.TerminalId));
    }

    [Fact]
    public async Task Terminal_sequences_within_a_push_are_distinct_and_ordered()
    {
        var local = new InMemoryLocalStore();
        var store = NewStore();
        await RecordSaleAsync(local, store);

        var transport = new FakeSyncTransport();
        await new SyncClient(local, transport).RunOnceAsync();

        var sequences = transport.Pushes[0].Records.Select(r => r.TerminalSeq).ToArray();

        Assert.Equal(sequences.Length, sequences.Distinct().Count());
        Assert.Equal(sequences.OrderBy(s => s), sequences);
    }

    /// <summary>
    /// A pass does its preflight before it pushes, and does the push whatever the preflight did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The preflight is where a due credential rotation happens, and this is the wire that makes it
    /// happen everywhere a pass happens rather than only where somebody remembered to call it. Four
    /// defects in this project's history were a correct part with no wire, so the wire is asserted
    /// rather than assumed.
    /// </para>
    /// <para>
    /// The second half matters as much as the first: a shop must not stop sending its sales because
    /// housekeeping failed. The credential inside its grace window still works, and the next pass tries
    /// again.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_pass_runs_the_preflight_first_and_pushes_whatever_the_preflight_did()
    {
        var local = new InMemoryLocalStore();
        var store = NewStore();
        await RecordSaleAsync(local, store);

        var transport = new FakeSyncTransport();
        var preflight = new RecordingPreflight();

        var result = await new SyncClient(local, transport, changeApplier: null, preflight).RunOnceAsync();

        Assert.Equal(1, preflight.Calls);
        Assert.Single(transport.Pushes);

        // A sale commits its stock movements in the same transaction, so the queue holds more than the
        // sale itself. Asserted against what was actually sent rather than against a number, so this
        // test is about the wiring and not about how many records a sale happens to produce.
        Assert.NotEmpty(transport.Pushes[0].Records);
        Assert.Equal(transport.Pushes[0].Records.Count, result.Pushed);

        // And a preflight that throws for an ordinary reason is swallowed, not passed on.
        var offline = new InMemoryLocalStore();
        await RecordSaleAsync(offline, store);

        var secondTransport = new FakeSyncTransport();

        var second = await new SyncClient(
            offline,
            secondTransport,
            changeApplier: null,
            new RecordingPreflight(new HttpRequestException("the hub is unreachable"))).RunOnceAsync();

        Assert.Single(secondTransport.Pushes);
        Assert.Equal(secondTransport.Pushes[0].Records.Count, second.Pushed);
    }

    /// <summary>Counts its calls, and optionally fails.</summary>
    private sealed class RecordingPreflight(Exception? failure = null) : ISyncPreflight
    {
        public int Calls { get; private set; }

        public Task PrepareAsync(CancellationToken ct = default)
        {
            Calls++;

            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }
    }
}
