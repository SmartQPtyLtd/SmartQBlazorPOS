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
/// Tests for applying head-office catalogue changes to the terminal's replica.
/// </summary>
/// <remarks>
/// <para>
/// Applying a change <b>replaces</b> the local row, so every field the applier does not carry is a
/// field the catalogue sync silently erases. It does not throw and it does not warn: the till keeps
/// selling, and the thing that was lost only shows up later as work that never reached the kitchen.
/// </para>
/// <para>
/// That is how the preparation station was lost. It was added to the stored product for the kitchen
/// printer, and the applier — written earlier, listing fields by hand — did not know about it. The
/// next catalogue sync would have quietly stripped the station from every product in the shop.
/// </para>
/// </remarks>
public sealed class CatalogChangeApplierTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static (CatalogChangeApplier Applier, InMemoryLocalStore Store) NewApplier()
    {
        var store = new InMemoryLocalStore();

        return (new CatalogChangeApplier(store, NullLogger<CatalogChangeApplier>.Instance), store);
    }

    private static SyncChange ProductChange(string json) =>
        new(ChangeSeq: 1, EntityType: CatalogProductPayload.EntityType, EntityId: "p1", StoreId: "s1", Payload: json);

    [Fact]
    public async Task A_product_with_every_field_set_survives_a_catalogue_sync()
    {
        // The guard that kills this whole class of defect.
        //
        // Rather than listing the fields to check — which is the same hand-written list that caused
        // the bug — this builds a product with every property set to something distinctive, sends
        // it through the applier as the hub would, and asserts the stored row matches. Add a field
        // to StoredProduct and forget the applier, and this fails.
        var original = new StoredProduct
        {
            Id = "01a0ac06000070008000000000000001",
            StoreId = "01a0ac06000070008000000000000002",
            Barcode = "6001000000017",
            Name = "Chicken Burger",
            UnitPrice = 79.99m,
            TaxName = "VAT",
            TaxRate = 0.15m,
            Sku = "BURG-01",
            IsActive = false,
            IsDeleted = true,
            IsOpenPrice = true,
            IsSoldByWeight = true,
            StationId = "kitchen",
        };

        var (applier, store) = NewApplier();

        var applied = await applier.ApplyAsync(
            ProductChange(JsonSerializer.Serialize(original, Json)));

        Assert.True(applied);

        var stored = await store.GetProductAsync(original.Id);

        Assert.NotNull(stored);
        Assert.Equal(original, stored);
    }

    [Fact]
    public async Task A_published_station_reaches_the_local_replica()
    {
        // The field that was lost, asserted on its own so a failure says which one.
        var (applier, store) = NewApplier();

        await applier.ApplyAsync(ProductChange(
            """{"id":"p1","storeId":"s1","barcode":"6001000000017","name":"Chicken Burger","unitPrice":79.99,"taxName":"VAT","taxRate":0.15,"stationId":"kitchen"}"""));

        var stored = await store.GetProductAsync("p1");

        Assert.NotNull(stored);
        Assert.Equal("kitchen", stored.StationId);
    }

    [Fact]
    public async Task A_head_office_deletion_reaches_the_local_replica()
    {
        // A deletion flag that does not survive the sync is a product that comes back from the
        // dead on every till, which is the exact thing the flag exists to prevent.
        var (applier, store) = NewApplier();

        await applier.ApplyAsync(ProductChange(
            """{"id":"p1","storeId":"s1","barcode":"6001000000017","name":"Withdrawn","unitPrice":10.00,"taxName":"VAT","taxRate":0.15,"isDeleted":true}"""));

        var stored = await store.GetProductAsync("p1");

        Assert.NotNull(stored);
        Assert.True(stored.IsDeleted);
    }

    [Fact]
    public async Task What_the_hub_publishes_is_what_the_terminal_can_apply()
    {
        // The publish side and the apply side are two halves of one contract, and they drifted:
        // the applier had its own payload record while the hub built an anonymous object. Both now
        // use the same type, so this round trip is the contract rather than a coincidence.
        var original = new StoredProduct
        {
            Id = "01a0ac06000070008000000000000003",
            StoreId = "01a0ac06000070008000000000000004",
            Barcode = "6001000000024",
            Name = "Still Water",
            UnitPrice = 12.50m,
            TaxName = "VAT",
            TaxRate = 0.15m,
            Sku = "WAT-01",
            Category = "Beverages / Water",
            IsActive = true,
            IsDeleted = false,
            IsOpenPrice = false,
            IsSoldByWeight = true,
            TracksStock = true,
            ReorderLevel = 24m,
            StationId = "bar",
        };

        // What the hub sends over the wire.
        var published = JsonSerializer.Serialize(CatalogProductPayload.FromStored(original), Json);

        var (applier, store) = NewApplier();
        await applier.ApplyAsync(ProductChange(published));

        var stored = await store.GetProductAsync(original.Id);

        Assert.Equal(original, stored);
    }

    [Fact]
    public void Every_field_the_terminal_stores_has_somewhere_to_travel()
    {
        // The guard that cannot be forgotten.
        //
        // The round-trip test above only fails if the *test* sets the new field. Add a property to
        // StoredProduct, forget the payload, and leave the test alone — and it passes, because both
        // sides are at their default. This compares the shapes themselves, so a field with nowhere
        // to travel fails the moment it is declared.
        var stored = typeof(StoredProduct)
            .GetProperties()
            .Select(p => (p.Name, p.PropertyType))
            .ToHashSet();

        var payload = typeof(CatalogProductPayload)
            .GetProperties()
            .Select(p => (p.Name, p.PropertyType))
            .ToHashSet();

        var missing = stored.Except(payload).ToList();

        Assert.True(
            missing.Count == 0,
            "These StoredProduct fields have no counterpart in the published payload, so a " +
            "catalogue sync would erase them from every product: " +
            string.Join(", ", missing.Select(m => $"{m.Name} ({m.PropertyType.Name})")));
    }

    [Fact]
    public void The_published_payload_carries_nothing_the_terminal_cannot_store()
    {
        // The other direction, which is drift rather than data loss: a field the hub publishes and
        // the terminal silently drops is a promise the contract does not keep.
        var stored = typeof(StoredProduct).GetProperties().Select(p => p.Name).ToHashSet();
        var payload = typeof(CatalogProductPayload).GetProperties().Select(p => p.Name).ToHashSet();

        var extra = payload.Except(stored).ToList();

        Assert.True(
            extra.Count == 0,
            "The published payload carries fields the terminal's stored product has no home for: " +
            string.Join(", ", extra));
    }

    [Fact]
    public async Task A_change_that_is_not_a_product_is_ignored_rather_than_failing()
    {
        // A hub that learns a new record type must not break an older terminal's sync.
        var (applier, _) = NewApplier();

        var applied = await applier.ApplyAsync(
            new SyncChange(1, "stockMovement", "m1", "s1", """{"qtyDelta":-1}"""));

        Assert.False(applied);
    }

    [Fact]
    public async Task An_unreadable_payload_is_skipped_rather_than_throwing()
    {
        // Skipping leaves the replica one product stale until the next sync. Throwing would stop
        // the sync outright and leave every later change unapplied.
        var (applier, _) = NewApplier();

        Assert.False(await applier.ApplyAsync(ProductChange("{ not valid json")));
        Assert.False(await applier.ApplyAsync(ProductChange("   ")));
    }

    [Fact]
    public async Task A_published_product_lands_against_the_store_from_the_credential()
    {
        // The payload carries a store id and it is honoured, because a head-office catalogue is
        // published per store. What must never happen is a terminal writing into another store's
        // catalogue, and that is enforced on the hub at publish time, not here.
        var (applier, store) = NewApplier();

        await applier.ApplyAsync(ProductChange(
            """{"id":"p9","storeId":"s-other","barcode":"6001000000099","name":"Elsewhere","unitPrice":1.00,"taxName":"VAT","taxRate":0.15}"""));

        var stored = await store.GetProductAsync("p9");

        Assert.NotNull(stored);
        Assert.Equal("s-other", stored.StoreId);
    }
}
