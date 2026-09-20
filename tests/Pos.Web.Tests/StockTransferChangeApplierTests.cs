// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pos.Infrastructure.Storage;
using Pos.Infrastructure.Sync;
using Pos.Web.Terminal;

namespace Pos.Web.Tests;

/// <summary>
/// Tests for storing a stock transfer relayed by the hub.
/// </summary>
/// <remarks>
/// This is the half of a transfer that arrives. It must store the document without moving stock and
/// without queueing it for upload — the goods are still on a van, and the document is not this
/// store's to push.
/// </remarks>
public sealed class StockTransferChangeApplierTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static StoredStockTransfer Transfer(
        string id = "transfer-1",
        string status = "Dispatched",
        string from = "store-a",
        string to = "store-b") => new()
        {
            Id = id,
            FromStoreId = from,
            ToStoreId = to,
            Reference = "TR-CT01-JN01-0001",
            Status = status,
            CreatedAt = "2026-03-25T12:00:00.0000000+00:00",
            CreatedByEmployeeId = "emp-1",
            Lines =
            [
                new StoredStockTransferLine
                {
                    ProductId = "p1",
                    Barcode = "6001000000017",
                    Name = "Cola 500ml",
                    QuantitySent = 10m,
                },
            ],
        };

    private static SyncChange Change(StoredStockTransfer transfer) =>
        new(1, StockTransferChangeApplier.EntityType, transfer.Id, transfer.ToStoreId,
            JsonSerializer.Serialize(transfer, Json));

    private static (StockTransferChangeApplier Applier, InMemoryLocalStore Store, TerminalChangeApplier Router)
        NewApplier()
    {
        var store = new InMemoryLocalStore();
        var applier = new StockTransferChangeApplier(store, NullLogger<StockTransferChangeApplier>.Instance);

        var router = new TerminalChangeApplier(
        [
            new CatalogChangeApplier(store, NullLogger<CatalogChangeApplier>.Instance),
            applier,
        ]);

        return (applier, store, router);
    }

    [Fact]
    public async Task A_relayed_transfer_is_stored_for_the_receiving_store()
    {
        var (applier, store, _) = NewApplier();

        var applied = await applier.ApplyAsync(Change(Transfer()));

        Assert.True(applied);

        var stored = await store.GetTransferAsync("transfer-1");
        Assert.NotNull(stored);
        Assert.True(stored.IsInTransit);
    }

    [Fact]
    public async Task A_relayed_transfer_moves_no_stock()
    {
        // The goods are on a van. Applying movements here would make stock appear before anybody
        // had checked it was there, which is the whole thing the receipt event prevents.
        var (applier, store, _) = NewApplier();

        await applier.ApplyAsync(Change(Transfer()));

        // No movement at all for this product — not a zero, an absence.
        Assert.False(store.DeriveStockLevels("store-b").ContainsKey("p1"));
        Assert.Empty(store.Movements);
    }

    [Fact]
    public async Task A_relayed_transfer_is_not_queued_for_upload()
    {
        // It came from the hub. Queueing it would push it back as though this store had raised it,
        // and the estate would accumulate copies of one transfer.
        var (applier, store, _) = NewApplier();

        await applier.ApplyAsync(Change(Transfer()));

        var summary = await store.GetOutboxSummaryAsync();

        Assert.Equal(0, summary.Total);
    }

    [Fact]
    public async Task A_relayed_transfer_is_visible_as_incoming()
    {
        var (applier, store, _) = NewApplier();

        await applier.ApplyAsync(Change(Transfer()));

        var incoming = await store.GetTransfersAsync("store-b", TransferDirection.Incoming);

        Assert.Single(incoming);

        // And it is not something the receiving store sent.
        Assert.Empty(await store.GetTransfersAsync("store-b", TransferDirection.Outgoing));
    }

    [Fact]
    public async Task A_relay_that_arrives_after_the_receipt_does_not_walk_it_backwards()
    {
        // The realistic race: the store dispatches and the destination receives, and the hub relays
        // the dispatch afterwards — a retry, or a queue that drained late. Overwriting would put the
        // transfer back into transit after somebody had already counted the goods in, and they
        // would be countable a second time.
        var (applier, store, _) = NewApplier();

        await store.SaveTransferAsync(Transfer(status: "Received"));
        await store.SaveTransferAsync(Transfer(status: "Received") with
        {
            ReceivedAt = "2026-03-26T09:00:00.0000000+00:00",
            ReceivedByEmployeeId = "emp-2",
        });

        var applied = await applier.ApplyAsync(Change(Transfer(status: "Dispatched")));

        Assert.True(applied);

        var stored = await store.GetTransferAsync("transfer-1");
        Assert.NotNull(stored);

        Assert.Equal("Received", stored.Status);

        // The receipt, with who counted the goods in, survives the stale relay intact.
        Assert.NotNull(stored.ReceivedAt);
        Assert.Equal("emp-2", stored.ReceivedByEmployeeId);
    }

    [Fact]
    public async Task A_relay_of_a_transfer_that_is_still_in_transit_is_applied()
    {
        // The ordinary case: the sending store dispatched and nothing has happened since.
        var (applier, store, _) = NewApplier();

        await store.SaveTransferAsync(Transfer(status: "Dispatched"));

        await applier.ApplyAsync(Change(Transfer(status: "Dispatched")));

        var stored = await store.GetTransferAsync("transfer-1");
        Assert.NotNull(stored);
        Assert.Equal("Dispatched", stored.Status);
    }

    [Fact]
    public async Task A_change_that_is_not_a_transfer_is_not_claimed()
    {
        var (applier, _, _) = NewApplier();

        Assert.False(await applier.ApplyAsync(new SyncChange(1, "sale", "s1", "store-b", "{}")));
        Assert.False(await applier.ApplyAsync(new SyncChange(1, "product", "p1", "store-b", "{}")));
    }

    [Fact]
    public async Task An_unreadable_payload_is_skipped_rather_than_throwing()
    {
        var (applier, _, _) = NewApplier();

        Assert.False(await applier.ApplyAsync(
            new SyncChange(1, StockTransferChangeApplier.EntityType, "t1", "store-b", "{ not valid")));

        Assert.False(await applier.ApplyAsync(
            new SyncChange(1, StockTransferChangeApplier.EntityType, "t1", "store-b", "  ")));
    }

    // ------------------------------------------------------------------- the router

    [Fact]
    public async Task The_router_sends_each_record_type_to_the_applier_that_knows_it()
    {
        // One stream, several record types. The catalogue's rules and the transfer's rules live in
        // separate classes, and the router is what keeps them apart.
        var (_, store, router) = NewApplier();

        var product = JsonSerializer.Serialize(
            new
            {
                id = "p9",
                storeId = "store-b",
                barcode = "6001000000099",
                name = "Relayed product",
                unitPrice = 10.00m,
                taxName = "VAT",
                taxRate = 0.15m,
            },
            Json);

        Assert.True(await router.ApplyAsync(new SyncChange(1, "product", "p9", "store-b", product)));
        Assert.True(await router.ApplyAsync(Change(Transfer())));

        Assert.NotNull(await store.GetProductAsync("p9"));
        Assert.NotNull(await store.GetTransferAsync("transfer-1"));
    }

    [Fact]
    public async Task The_router_reports_a_record_type_nobody_claims_rather_than_failing()
    {
        // A hub that learns a new record type must not stop an older terminal's sync.
        var (_, _, router) = NewApplier();

        Assert.False(await router.ApplyAsync(
            new SyncChange(1, "somethingNew", "x1", "store-b", "{}")));
    }
}
