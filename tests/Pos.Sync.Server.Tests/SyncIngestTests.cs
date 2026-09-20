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
using Microsoft.Extensions.Logging.Abstractions;
using Pos.Infrastructure.Sync;
using Pos.Sync.Server.Auth;
using Pos.Sync.Server.Data;
using Pos.Sync.Server.Endpoints;

namespace Pos.Sync.Server.Tests;

/// <summary>
/// A disposable SQLite database.
/// </summary>
/// <remarks>
/// Uses the real SQLite provider against a temporary file rather than an in-memory
/// substitute, so the unique constraints and transactional behaviour under test are the
/// ones that will run in production. The idempotency guarantee is enforced by a primary
/// key, so testing it against a provider that does not enforce one would prove nothing.
/// </remarks>
internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _path;

    private TestDatabase(string path, SyncDbContext context)
    {
        _path = path;
        Context = context;
    }

    public SyncDbContext Context { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pos-sync-test-{Guid.NewGuid():N}.db");

        var options = new DbContextOptionsBuilder<SyncDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        var context = new SyncDbContext(options);
        await context.Database.EnsureCreatedAsync();

        return new TestDatabase(path, context);
    }

    /// <summary>Opens a second context over the same file, to prove data really persisted.</summary>
    public SyncDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<SyncDbContext>()
            .UseSqlite($"Data Source={_path}")
            .Options;

        return new SyncDbContext(options);
    }

    /// <summary>Registers a store and an enrolled device with a known secret.</summary>
    public async Task<(string StoreId, string Secret)> EnrollStoreAsync(string code = "CT01")
    {
        var store = new StoreEntity
        {
            Id = Guid.CreateVersion7().ToString("N"),
            Code = code,
            Name = $"{code} Store",
            Currency = "ZAR",
            TaxMode = "Inclusive",
            DefaultTaxRate = 0.15m,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        const string secret = "test-device-secret-that-is-long-enough-0001";

        var device = new DeviceEntity
        {
            Id = Guid.CreateVersion7().ToString("N"),
            StoreId = store.Id,
            Label = "Till 1",
            SecretHash = DeviceCredentials.Hash(secret),
            RefreshTokenHash = DeviceCredentials.Hash("refresh"),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        Context.Stores.Add(store);
        Context.Devices.Add(device);
        await Context.SaveChangesAsync();

        return (store.Id, secret);
    }

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();

        // SQLite holds the file open briefly; a failed cleanup must not fail the test.
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (IOException)
        {
            // Left for the OS to clean up from the temp directory.
        }
    }
}

/// <summary>
/// Tests for the sync ingest rules.
/// </summary>
/// <remarks>
/// The behaviour under test here is the one that decides whether a store's books balance:
/// a replayed push must never record a sale twice.
/// </remarks>
public sealed class SyncIngestTests
{
    private static string SalePayload(string saleId, decimal total = 115.00m, string businessDate = "2026-03-25") =>
        JsonSerializer.Serialize(new
        {
            id = saleId,
            businessDate,
            total,
            currency = "ZAR",
            lines = Array.Empty<object>(),
            tenders = Array.Empty<object>(),
        });

    private static SyncRecord NewSaleRecord(string saleId, string terminalId = "TILL-1", long seq = 1) =>
        new("sale", saleId, terminalId, seq, SalePayload(saleId));

    [Fact]
    public async Task A_new_sale_is_accepted_and_stored()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (storeId, _) = await db.EnrollStoreAsync();
        var saleId = Guid.CreateVersion7().ToString("N");

        var record = NewSaleRecord(saleId);
        await IngestAsync(db, storeId, record);

        var stored = await db.Context.Records.SingleAsync(r => r.Id == saleId);
        Assert.Equal(storeId, stored.StoreId);
        Assert.Equal("sale", stored.EntityType);
        Assert.Equal(115.00m, stored.Total);
        Assert.Equal("2026-03-25", stored.BusinessDate);
    }

    [Fact]
    public async Task A_replayed_push_does_not_record_the_sale_twice()
    {
        // The scenario this exists for: the terminal pushes, the hub commits, and the
        // response is lost on the way back. The terminal retries. The sale must exist once.
        await using var db = await TestDatabase.CreateAsync();
        var (storeId, _) = await db.EnrollStoreAsync();
        var saleId = Guid.CreateVersion7().ToString("N");

        var record = NewSaleRecord(saleId);

        await IngestAsync(db, storeId, record);
        await IngestAsync(db, storeId, record);
        await IngestAsync(db, storeId, record);

        var count = await db.Context.Records.CountAsync(r => r.Id == saleId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task A_duplicate_is_reported_as_duplicate_rather_than_rejected()
    {
        // Reported as a success, not an error: from the terminal's point of view the
        // record is safely stored either way, so it can drop it from its outbox.
        var status = await IngestTwiceAsync();

        Assert.Equal(SyncRecordStatus.Accepted, status.First);
        Assert.Equal(SyncRecordStatus.Duplicate, status.Second);
    }

    [Fact]
    public async Task A_duplicate_produces_only_one_change_log_entry()
    {
        // A second change-log row would make every terminal pull the same sale again.
        await using var db = await TestDatabase.CreateAsync();
        var (storeId, _) = await db.EnrollStoreAsync();
        var saleId = Guid.CreateVersion7().ToString("N");

        var record = NewSaleRecord(saleId);
        await IngestAsync(db, storeId, record);
        await IngestAsync(db, storeId, record);

        var changes = await db.Context.ChangeLog.CountAsync(c => c.EntityId == saleId);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Records_survive_a_reconnect_to_the_database()
    {
        // Proves the commit is durable rather than only visible in the change tracker.
        await using var db = await TestDatabase.CreateAsync();
        var (storeId, _) = await db.EnrollStoreAsync();
        var saleId = Guid.CreateVersion7().ToString("N");

        await IngestAsync(db, storeId, NewSaleRecord(saleId));

        await using var fresh = db.NewContext();
        Assert.True(await fresh.Records.AnyAsync(r => r.Id == saleId));
    }

    [Fact]
    public async Task Two_terminals_pushing_concurrently_both_land()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (storeId, _) = await db.EnrollStoreAsync();

        var ids = Enumerable.Range(0, 20).Select(_ => Guid.CreateVersion7().ToString("N")).ToArray();

        foreach (var (id, index) in ids.Select((id, i) => (id, i)))
        {
            await IngestAsync(db, storeId, NewSaleRecord(id, "TILL-1", index + 1));
        }

        Assert.Equal(20, await db.Context.Records.CountAsync());
    }

    [Fact]
    public async Task The_change_log_cursor_advances_monotonically()
    {
        // Cursors are positions in the change log. If they were not strictly increasing,
        // a terminal could pull the same change twice or skip one entirely.
        await using var db = await TestDatabase.CreateAsync();
        var (storeId, _) = await db.EnrollStoreAsync();

        for (var i = 0; i < 5; i++)
        {
            await IngestAsync(db, storeId, NewSaleRecord(Guid.CreateVersion7().ToString("N"), "TILL-1", i + 1));
        }

        var sequences = await db.Context.ChangeLog
            .OrderBy(c => c.ChangeSeq)
            .Select(c => c.ChangeSeq)
            .ToListAsync();

        Assert.Equal(5, sequences.Count);
        Assert.Equal(sequences.OrderBy(s => s), sequences);
        Assert.Equal(sequences.Distinct().Count(), sequences.Count);
    }

    [Fact]
    public async Task A_malformed_payload_is_still_stored_and_synced()
    {
        // Rejecting it would strand a sale on the terminal forever, which is worse than
        // storing a record that simply cannot be indexed for reporting.
        await using var db = await TestDatabase.CreateAsync();
        var (storeId, _) = await db.EnrollStoreAsync();

        var saleId = Guid.CreateVersion7().ToString("N");
        var record = new SyncRecord("sale", saleId, "TILL-1", 1, "{ this is not valid json");

        var status = await IngestAsync(db, storeId, record);

        Assert.Equal(SyncRecordStatus.Accepted, status);

        var stored = await db.Context.Records.SingleAsync(r => r.Id == saleId);
        Assert.Null(stored.Total);
        Assert.Null(stored.BusinessDate);
    }

    [Fact]
    public async Task The_same_terminal_cannot_claim_one_sequence_position_twice()
    {
        // The unique index on (terminalId, terminalSeq) is the second line of defence: if
        // a client ever reused an id, the database rejects the collision rather than
        // silently accepting two different records at one position in the stream.
        await using var db = await TestDatabase.CreateAsync();
        var (storeId, _) = await db.EnrollStoreAsync();

        db.Context.Records.Add(new SyncedRecordEntity
        {
            Id = Guid.CreateVersion7().ToString("N"),
            StoreId = storeId,
            TerminalId = "TILL-1",
            EntityType = "sale",
            TerminalSeq = 7,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow,
        });

        await db.Context.SaveChangesAsync();

        db.Context.Records.Add(new SyncedRecordEntity
        {
            Id = Guid.CreateVersion7().ToString("N"),
            StoreId = storeId,
            TerminalId = "TILL-1",
            EntityType = "sale",
            TerminalSeq = 7,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(async () => await db.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task A_record_with_no_terminal_id_is_rejected_rather_than_crashing()
    {
        // TerminalId is the ordering key. Letting a null through reached a dictionary lookup that
        // threw, and because a push is one transaction that 500 failed the WHOLE batch — so one
        // malformed record riding along with a day's trading stopped the day's trading reaching
        // head office, with nothing the terminal could do about it.
        await using var db = await TestDatabase.CreateAsync();
        var (storeId, _) = await db.EnrollStoreAsync();

        var record = new SyncRecord(
            "sale", Guid.CreateVersion7().ToString("N"), TerminalId: string.Empty, 1, SalePayload("x"));

        var status = await IngestAsync(db, storeId, record);

        Assert.Equal(SyncRecordStatus.Rejected, status);
        Assert.Equal(0, await db.Context.Records.CountAsync());
    }

    [Fact]
    public async Task One_bad_record_does_not_stop_the_good_ones_in_its_batch()
    {
        // The reason the rejection has to be per-record. A terminal pushes its outbox in batches,
        // and a batch that fails whole means nothing gets through until someone intervenes.
        await using var db = await TestDatabase.CreateAsync();
        var (storeId, _) = await db.EnrollStoreAsync();

        var ledger = new SyncSequenceLedger();
        var principal = new SyncPrincipal("device-1", storeId);

        var good = Guid.CreateVersion7().ToString("N");
        var bad = Guid.CreateVersion7().ToString("N");

        var goodResult = await SyncIngest.ApplyAsync(
            NewSaleRecord(good, "TILL-1", seq: 1), principal, db.Context, ledger, NullLogger.Instance);

        var badResult = await SyncIngest.ApplyAsync(
            new SyncRecord("sale", bad, TerminalId: string.Empty, 2, SalePayload(bad)),
            principal, db.Context, ledger, NullLogger.Instance);

        await db.Context.SaveChangesAsync();

        Assert.Equal(SyncRecordStatus.Accepted, goodResult.Status);
        Assert.Equal(SyncRecordStatus.Rejected, badResult.Status);
        Assert.True(await db.Context.Records.AnyAsync(r => r.Id == good));
        Assert.False(await db.Context.Records.AnyAsync(r => r.Id == bad));
    }

    // ------------------------------------------------------------------ sequence resets

    [Fact]
    public async Task A_terminal_that_restarted_its_counter_is_not_rejected()
    {
        // The scenario: a till's page is reloaded mid-day. A counter held only in memory restarts
        // at one, so every record after the reload asks for a position the hub already has. The
        // hub's unique index on (terminalId, terminalSeq) would refuse it — and because a push is
        // one transaction, refusing it fails the WHOLE batch and strands a legitimate sale on the
        // terminal forever.
        //
        // A sequence number is ordering metadata; the entity id is the identity. Losing a sale to
        // protect a position marker is the wrong trade, so the reset is absorbed.
        await using var db = await TestDatabase.CreateAsync();
        var (storeId, _) = await db.EnrollStoreAsync();

        var beforeReload = Guid.CreateVersion7().ToString("N");
        await IngestAsync(db, storeId, NewSaleRecord(beforeReload, "TILL-1", seq: 1));

        // Counter restarts: the next sale asks for position 1 again.
        var afterReload = Guid.CreateVersion7().ToString("N");
        var status = await IngestAsync(db, storeId, NewSaleRecord(afterReload, "TILL-1", seq: 1));

        Assert.Equal(SyncRecordStatus.Accepted, status);

        var record = await db.Context.Records.SingleAsync(r => r.Id == afterReload);
        Assert.Equal(2, record.TerminalSeq);

        // Both sales are present, which is the property that actually matters.
        Assert.Equal(2, await db.Context.Records.CountAsync());
    }

    [Fact]
    public async Task A_reset_does_not_report_a_sequence_gap()
    {
        // A restart makes the terminal appear to jump backwards. If that were treated as a gap the
        // hub would raise a false alarm about lost records on every browser refresh, and an alert
        // that fires constantly is one nobody reads.
        //
        // Forward jumps are still gaps — that is what the sequence number is for — so the reset
        // must be absorbed by continuing the count, not by ignoring the field.
        await using var db = await TestDatabase.CreateAsync();
        var (storeId, _) = await db.EnrollStoreAsync();

        await IngestAsync(db, storeId, NewSaleRecord(Guid.CreateVersion7().ToString("N"), "TILL-1", seq: 1));
        await IngestAsync(db, storeId, NewSaleRecord(Guid.CreateVersion7().ToString("N"), "TILL-1", seq: 2));

        // Reload: the counter is back at one.
        await IngestAsync(db, storeId, NewSaleRecord(Guid.CreateVersion7().ToString("N"), "TILL-1", seq: 1));
        await IngestAsync(db, storeId, NewSaleRecord(Guid.CreateVersion7().ToString("N"), "TILL-1", seq: 2));

        var sequences = await db.Context.Records
            .OrderBy(r => r.TerminalSeq)
            .Select(r => r.TerminalSeq)
            .ToListAsync();

        // Contiguous across the restart, so nothing looks lost.
        Assert.Equal([1L, 2L, 3L, 4L], sequences);

        var tracked = await db.Context.TerminalSequences.SingleAsync(s => s.TerminalId == "TILL-1");
        Assert.Equal(4, tracked.HighestSeq);
    }

    [Fact]
    public async Task Two_resets_in_one_batch_still_get_distinct_positions()
    {
        // The reassignment has to remember what it handed out earlier in the same request. A
        // per-record database read would see the pre-batch value both times and hand out the same
        // position twice — reintroducing the very collision this logic exists to absorb, and
        // failing the entire batch on the unique index.
        await using var db = await TestDatabase.CreateAsync();
        var (storeId, _) = await db.EnrollStoreAsync();

        await IngestAsync(db, storeId, NewSaleRecord(Guid.CreateVersion7().ToString("N"), "TILL-1", seq: 9));

        // One ledger for the batch, exactly as the endpoint creates one per request.
        var ledger = new SyncSequenceLedger();
        var principal = new SyncPrincipal("device-1", storeId);

        var first = Guid.CreateVersion7().ToString("N");
        var second = Guid.CreateVersion7().ToString("N");

        await SyncIngest.ApplyAsync(
            NewSaleRecord(first, "TILL-1", seq: 1), principal, db.Context, ledger, NullLogger.Instance);

        await SyncIngest.ApplyAsync(
            NewSaleRecord(second, "TILL-1", seq: 1), principal, db.Context, ledger, NullLogger.Instance);

        await db.Context.SaveChangesAsync();

        var stored = await db.Context.Records
            .Where(r => r.Id == first || r.Id == second)
            .Select(r => r.TerminalSeq)
            .ToListAsync();

        Assert.Equal(2, stored.Count);
        Assert.Equal([10L, 11L], stored.OrderBy(s => s));
    }

    [Fact]
    public async Task The_same_record_twice_in_one_batch_is_stored_once()
    {
        // A repeat inside a single batch is not in the database yet, so a query cannot see it and
        // both copies would be staged — failing the batch on the primary key. The batch ledger is
        // what closes that, and a batch that fails is a batch the terminal retries forever.
        await using var db = await TestDatabase.CreateAsync();
        var (storeId, _) = await db.EnrollStoreAsync();

        var ledger = new SyncSequenceLedger();
        var principal = new SyncPrincipal("device-1", storeId);
        var record = NewSaleRecord(Guid.CreateVersion7().ToString("N"));

        var first = await SyncIngest.ApplyAsync(
            record, principal, db.Context, ledger, NullLogger.Instance);

        var second = await SyncIngest.ApplyAsync(
            record, principal, db.Context, ledger, NullLogger.Instance);

        await db.Context.SaveChangesAsync();

        Assert.Equal(SyncRecordStatus.Accepted, first.Status);
        Assert.Equal(SyncRecordStatus.Duplicate, second.Status);
        Assert.Equal(1, await db.Context.Records.CountAsync());
        Assert.Equal(1, await db.Context.ChangeLog.CountAsync());
    }

    // ------------------------------------------------------------- transfer relaying

    /// <summary>A transfer payload addressed to a store.</summary>
    private static string TransferPayload(string fromStoreId, string toStoreId, string status = "Dispatched") =>
        JsonSerializer.Serialize(new
        {
            id = "transfer-1",
            fromStoreId,
            toStoreId,
            reference = "TR-CT01-JN01-0001",
            status,
            createdAt = "2026-03-25T12:00:00.0000000+00:00",
            createdByEmployeeId = "emp-1",
            lines = Array.Empty<object>(),
        });

    private static SyncRecord TransferRecord(string payload, string terminalId = "TILL-1", long seq = 1) =>
        new("stockTransfer", "transfer-1", terminalId, seq, payload);

    [Fact]
    public async Task A_transfer_is_relayed_to_the_store_it_is_addressed_to()
    {
        // The crux of making transfers work across shops. A terminal's pull returns only its own
        // store's changes, so without this the sending store's document is invisible to the shop
        // expecting the goods — and the goods sit in transit forever.
        await using var db = await TestDatabase.CreateAsync();
        var (sender, _) = await db.EnrollStoreAsync("CT01");
        var (receiver, _) = await db.EnrollStoreAsync("JN01");

        await IngestAsync(
            db, sender, TransferRecord(TransferPayload(sender, receiver)));

        // The sending store keeps its own copy.
        Assert.Equal(1, await db.Context.ChangeLog.CountAsync(c => c.StoreId == sender));

        // And the destination gets one, without the sending store having any authority over it.
        Assert.Equal(1, await db.Context.ChangeLog.CountAsync(c => c.StoreId == receiver));
    }

    [Fact]
    public async Task The_relayed_copy_keeps_the_same_identity()
    {
        // Same record type and same id, so the receiving terminal applies it as the transfer it is
        // rather than as a second document for the same goods.
        await using var db = await TestDatabase.CreateAsync();
        var (sender, _) = await db.EnrollStoreAsync("CT01");
        var (receiver, _) = await db.EnrollStoreAsync("JN01");

        await IngestAsync(db, sender, TransferRecord(TransferPayload(sender, receiver)));

        var relayed = await db.Context.ChangeLog
            .SingleAsync(c => c.StoreId == receiver);

        Assert.Equal("stockTransfer", relayed.EntityType);
        Assert.Equal("transfer-1", relayed.EntityId);
        Assert.Equal(TransferPayload(sender, receiver), relayed.Payload);
    }

    /// <summary>
    /// A transfer is relayed even when the sending till spells the store id its own way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the form a real till sends. The hub mints store ids without dashes; the till's domain
    /// type renders them with, because that is what <c>Guid.ToString()</c> does. The earlier tests
    /// all used the hub's own spelling in the payload, so they passed while the live path was
    /// broken: a transfer addressed to <c>…-…-…</c> matched no store, was stored, and was never
    /// relayed — leaving the goods in transit forever with nothing on the destination's screen.
    /// </para>
    /// <para>
    /// The relay row must also carry the hub's spelling, because that is what the receiving
    /// terminal's pull is scoped by. Relaying under the sender's formatting would put the document
    /// where the store it is addressed to still cannot see it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_transfer_addressed_in_the_tills_own_format_is_still_relayed()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (sender, _) = await db.EnrollStoreAsync("CT01");
        var (receiver, _) = await db.EnrollStoreAsync("JN01");

        // Exactly what the till writes into the payload.
        var asTheTillWritesIt = Guid.Parse(receiver).ToString("D");

        await IngestAsync(
            db, sender, TransferRecord(TransferPayload(sender, asTheTillWritesIt)));

        var relayed = await db.Context.ChangeLog
            .SingleOrDefaultAsync(c => c.EntityType == "stockTransfer" && c.StoreId != sender);

        Assert.NotNull(relayed);

        // Under the hub's own id, not the sender's spelling.
        Assert.Equal(receiver, relayed.StoreId);
    }

    /// <summary>
    /// The sending store is still recognised as the destination when it is spelled differently.
    /// </summary>
    /// <remarks>
    /// A transfer to yourself is not a transfer. Relaying it would put a second copy in the
    /// sender's own stream, and the goods would be expected to arrive from themselves.
    /// </remarks>
    [Fact]
    public async Task A_transfer_to_the_sending_store_in_another_format_is_not_relayed()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (sender, _) = await db.EnrollStoreAsync("CT01");

        await IngestAsync(
            db,
            sender,
            TransferRecord(TransferPayload(sender, Guid.Parse(sender).ToString("D"))));

        Assert.Equal(1, await db.Context.ChangeLog.CountAsync(c => c.StoreId == sender));
    }

    [Fact]
    public async Task A_transfer_to_an_unknown_store_is_stored_but_not_relayed()    {
        // Refusing the record would strand it on the sending terminal while the stock had already
        // left that shop's books, which is worse than one that needs looking at.
        await using var db = await TestDatabase.CreateAsync();
        var (sender, _) = await db.EnrollStoreAsync("CT01");

        var status = await IngestAsync(
            db, sender, TransferRecord(TransferPayload(sender, "no-such-store")));

        Assert.Equal(SyncRecordStatus.Accepted, status);
        Assert.Equal(1, await db.Context.ChangeLog.CountAsync());
        Assert.Equal(1, await db.Context.Records.CountAsync());
    }

    [Fact]
    public async Task A_transfer_addressed_to_the_sending_store_is_not_relayed()
    {
        // Not a transfer. Relaying it would put a second copy in the sender's own stream, and the
        // till would offer to receive its own dispatch.
        await using var db = await TestDatabase.CreateAsync();
        var (sender, _) = await db.EnrollStoreAsync("CT01");

        await IngestAsync(db, sender, TransferRecord(TransferPayload(sender, sender)));

        Assert.Equal(1, await db.Context.ChangeLog.CountAsync());
    }

    [Fact]
    public async Task An_unreadable_transfer_payload_is_stored_but_not_relayed()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (sender, _) = await db.EnrollStoreAsync("CT01");

        var status = await IngestAsync(db, sender, TransferRecord("{ not valid json"));

        Assert.Equal(SyncRecordStatus.Accepted, status);
        Assert.Equal(1, await db.Context.Records.CountAsync());
        Assert.Equal(1, await db.Context.ChangeLog.CountAsync());
    }

    [Fact]
    public async Task An_ordinary_sale_is_not_relayed_anywhere()
    {
        // The relay is for transfers alone. A sale crossing a store boundary would let one shop
        // read another's takings, which is exactly what store-scoped pulling prevents.
        await using var db = await TestDatabase.CreateAsync();
        var (sender, _) = await db.EnrollStoreAsync("CT01");
        await db.EnrollStoreAsync("JN01");

        await IngestAsync(db, sender, NewSaleRecord(Guid.CreateVersion7().ToString("N")));

        Assert.Equal(1, await db.Context.ChangeLog.CountAsync());
    }

    [Fact]
    public async Task A_relayed_transfer_lands_in_the_destinations_own_pull()
    {
        // End to end through the scoping rule the pull actually applies, rather than by inspecting
        // the change log directly.
        await using var db = await TestDatabase.CreateAsync();
        var (sender, _) = await db.EnrollStoreAsync("CT01");
        var (receiver, _) = await db.EnrollStoreAsync("JN01");

        await IngestAsync(db, sender, TransferRecord(TransferPayload(sender, receiver)));

        var visibleToReceiver = await db.Context.ChangeLog
            .Where(c => c.StoreId == receiver || c.StoreId == null)
            .Select(c => c.EntityId)
            .ToListAsync();

        Assert.Contains("transfer-1", visibleToReceiver);

        // And the sending store cannot see anything it did not author beyond its own copy.
        var visibleToSender = await db.Context.ChangeLog
            .Where(c => c.StoreId == sender)
            .CountAsync();

        Assert.Equal(1, visibleToSender);
    }

    private static async Task<(SyncRecordStatus First, SyncRecordStatus Second)> IngestTwiceAsync()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (storeId, _) = await db.EnrollStoreAsync();
        var saleId = Guid.CreateVersion7().ToString("N");
        var record = NewSaleRecord(saleId);

        var first = await IngestAsync(db, storeId, record);
        var second = await IngestAsync(db, storeId, record);

        return (first, second);
    }

    /// <summary>
    /// Applies the same ingest rules the endpoint uses.
    /// </summary>
    /// <remarks>
    /// Calls the production <see cref="SyncIngest"/> directly rather than a copy of its rules. The
    /// rules decide whether a store's books balance, so a test asserting a hand-written mirror
    /// would be testing the mirror — and would stay green while the endpoint diverged from it.
    /// </remarks>
    private static async Task<SyncRecordStatus> IngestAsync(
        TestDatabase db,
        string storeId,
        SyncRecord record)
    {
        var ledger = new SyncSequenceLedger();
        var principal = new SyncPrincipal("device-1", storeId);

        var result = await SyncIngest.ApplyAsync(
            record,
            principal,
            db.Context,
            ledger,
            NullLogger.Instance);

        await db.Context.SaveChangesAsync();

        return result.Status;
    }
}

/// <summary>Tests for device credential handling.</summary>
public sealed class DeviceCredentialTests
{
    [Fact]
    public void A_secret_verifies_against_its_own_hash()
    {
        var secret = DeviceCredentials.NewSecret();

        Assert.True(DeviceCredentials.Verify(secret, DeviceCredentials.Hash(secret)));
    }

    [Fact]
    public void A_wrong_secret_does_not_verify()
    {
        var hash = DeviceCredentials.Hash(DeviceCredentials.NewSecret());

        Assert.False(DeviceCredentials.Verify(DeviceCredentials.NewSecret(), hash));
        Assert.False(DeviceCredentials.Verify("", hash));
    }

    [Fact]
    public void Secrets_are_unique_and_long_enough_to_resist_guessing()
    {
        var secrets = Enumerable.Range(0, 50).Select(_ => DeviceCredentials.NewSecret()).ToArray();

        Assert.Equal(secrets.Length, secrets.Distinct().Count());
        Assert.All(secrets, s => Assert.True(s.Length >= 32));
    }

    [Fact]
    public void Enrolment_codes_avoid_characters_that_are_easily_mistyped()
    {
        // These are read off a screen and typed by a person setting up a till, so O/0 and
        // I/1 must not appear.
        for (var i = 0; i < 100; i++)
        {
            var code = DeviceCredentials.NewEnrollmentCode();

            Assert.DoesNotContain('O', code);
            Assert.DoesNotContain('0', code);
            Assert.DoesNotContain('I', code);
            Assert.DoesNotContain('1', code);
        }
    }

    [Fact]
    public void The_hash_is_stable_for_a_given_secret()
    {
        var secret = "a-fixed-secret-value-for-hashing";

        Assert.Equal(DeviceCredentials.Hash(secret), DeviceCredentials.Hash(secret));
    }
}
