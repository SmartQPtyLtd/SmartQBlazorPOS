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

namespace Pos.Infrastructure.Tests;

/// <summary>
/// Catalogue and stock ledger tests.
/// </summary>
/// <remarks>
/// Stock accuracy is won or lost on corrections, so these concentrate on the ways a count could be
/// recorded wrongly: writing the count instead of the difference, an empty correction that makes a
/// level look better-evidenced than it is, and a level that differs depending on which code path
/// computed it.
/// </remarks>
public sealed class StockServiceTests
{
    private const string Zar = "ZAR";
    private static readonly TaxRate Vat15 = new("VAT", 0.15m);

    private sealed class FixedTerminal(string id) : ITerminalIdentity
    {
        public string TerminalId { get; } = id;
    }

    private sealed class Terminal
    {
        public InMemoryLocalStore Store { get; } = new();

        public StockService Stock { get; }

        public Store Shop { get; } = new()
        {
            Id = StoreId.New(),
            Name = "CORNER STORE",
            Code = "CT01",
            Currency = Zar,
            TaxMode = TaxMode.Inclusive,
            DefaultTaxRate = Vat15,
        };

        public StoredProduct Cola { get; }

        public Terminal()
        {
            Stock = new StockService(Store, new FixedTerminal("TILL-1"));
            Cola = NewProduct(Shop, "6001000000017", "Cola 500ml", 15.00m);
        }

        public async Task SeedAsync(params StoredProduct[] products) =>
            await Store.UpsertProductsAsync(products.Length == 0 ? [Cola] : products);

        public async Task<decimal> LevelOfAsync(string productId)
        {
            var movements = await Store.GetStockMovementsAsync(Shop.Id.ToString(), productId);
            return movements.Sum(m => m.QtyDelta);
        }

        public static StoredProduct NewProduct(Store shop, string barcode, string name, decimal price) => new()
        {
            Id = ProductId.New().ToString(),
            StoreId = shop.Id.ToString(),
            Barcode = barcode,
            Name = name,
            UnitPrice = price,
            TaxName = "VAT",
            TaxRate = 0.15m,
        };
    }

    // ----------------------------------------------------------------------- catalogue

    [Fact]
    public async Task The_catalogue_lists_products_with_no_history_as_untracked()
    {
        // Distinguishing "never moved" from "zero" matters: a product that has never been stocked
        // is not the same as one that has sold out.
        var terminal = new Terminal();
        await terminal.SeedAsync();

        var items = await terminal.Stock.GetCatalogueAsync(terminal.Shop.Id.ToString());

        var item = Assert.Single(items);
        Assert.True(item.Stock.HasNoHistory);
        Assert.Equal(0, item.Stock.MovementCount);
    }

    [Fact]
    public async Task The_catalogue_can_be_searched_by_name_barcode_or_sku()
    {
        var terminal = new Terminal();
        var bread = Terminal.NewProduct(terminal.Shop, "6001000000031", "White Bread", 18.99m);

        await terminal.SeedAsync(terminal.Cola, bread);

        Assert.Single(await terminal.Stock.GetCatalogueAsync(terminal.Shop.Id.ToString(), "cola"));
        Assert.Single(await terminal.Stock.GetCatalogueAsync(terminal.Shop.Id.ToString(), "6001000000031"));
        Assert.Single(await terminal.Stock.GetCatalogueAsync(terminal.Shop.Id.ToString(), "bread"));
        Assert.Equal(2, (await terminal.Stock.GetCatalogueAsync(terminal.Shop.Id.ToString())).Count);
    }

    [Fact]
    public async Task A_withdrawn_product_is_hidden_from_the_till_and_shown_to_management()
    {
        // The till must not offer it; the management screen must, or it could never be brought back.
        var terminal = new Terminal();
        await terminal.SeedAsync();

        await terminal.Stock.UpdateLocalProductAsync(
            terminal.Cola,
            new ProductEdit(terminal.Cola.Name, terminal.Cola.UnitPrice, IsActive: false));

        var visible = await terminal.Stock.GetCatalogueAsync(terminal.Shop.Id.ToString());
        var all = await terminal.Stock.GetCatalogueAsync(
            terminal.Shop.Id.ToString(), includeInactive: true);

        Assert.Empty(visible);
        Assert.Single(all);
        Assert.True(all[0].IsWithdrawn);
    }

    [Fact]
    public async Task A_product_deleted_by_head_office_never_reappears()
    {
        // A deletion is authoritative. Without the distinction from a local deactivation, a deleted
        // product would come back on the till the next time the catalogue synced.
        var terminal = new Terminal();
        await terminal.SeedAsync();

        await terminal.Store.UpsertProductsAsync([terminal.Cola with { IsDeleted = true }]);

        var visible = await terminal.Stock.GetCatalogueAsync(terminal.Shop.Id.ToString());
        var all = await terminal.Stock.GetCatalogueAsync(
            terminal.Shop.Id.ToString(), includeInactive: true);

        Assert.Empty(visible);
        Assert.Empty(all);
    }

    [Fact]
    public async Task Updating_a_product_changes_only_what_a_shop_owns()
    {
        var terminal = new Terminal();
        await terminal.SeedAsync();

        await terminal.Stock.UpdateLocalProductAsync(
            terminal.Cola,
            new ProductEdit("Cola 440ml", 16.50m, IsActive: true));

        var updated = await terminal.Store.GetProductAsync(terminal.Cola.Id);

        Assert.NotNull(updated);
        Assert.Equal("Cola 440ml", updated.Name);
        Assert.Equal(16.50m, updated.UnitPrice);

        // The barcode and tax rate are head-office data and untouched.
        Assert.Equal(terminal.Cola.Barcode, updated.Barcode);
        Assert.Equal(terminal.Cola.TaxRate, updated.TaxRate);
    }

    [Fact]
    public async Task A_negative_price_is_refused()
    {
        var terminal = new Terminal();
        await terminal.SeedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Stock.UpdateLocalProductAsync(
                terminal.Cola,
                new ProductEdit(terminal.Cola.Name, -1m, IsActive: true)));
    }

    // ------------------------------------------------------------------------ receipts

    [Fact]
    public async Task A_goods_receipt_adds_to_the_ledger()
    {
        var terminal = new Terminal();
        await terminal.SeedAsync();

        await terminal.Stock.RecordGoodsReceiptAsync(terminal.Cola.Id, 24m, "INV-4471");

        Assert.Equal(24m, await terminal.LevelOfAsync(terminal.Cola.Id));
    }

    [Fact]
    public async Task A_goods_receipt_must_be_positive()
    {
        // Direction is given by the movement type, so a negative receipt would be ambiguous.
        var terminal = new Terminal();
        await terminal.SeedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Stock.RecordGoodsReceiptAsync(terminal.Cola.Id, -5m, "INV-1"));
    }

    [Fact]
    public async Task A_receipt_after_a_shortage_accumulates_rather_than_replacing()
    {
        var terminal = new Terminal();
        await terminal.SeedAsync();

        await terminal.Stock.RecordGoodsReceiptAsync(terminal.Cola.Id, 24m, "INV-1");
        await terminal.Stock.RecordShrinkageAsync(terminal.Cola.Id, 3m, "Damaged in transit");

        Assert.Equal(21m, await terminal.LevelOfAsync(terminal.Cola.Id));
    }

    // ------------------------------------------------------------------------ write-offs

    [Fact]
    public async Task A_write_off_removes_stock()
    {
        var terminal = new Terminal();
        await terminal.SeedAsync();
        await terminal.Stock.RecordGoodsReceiptAsync(terminal.Cola.Id, 10m, "INV-1");

        var movement = await terminal.Stock.RecordShrinkageAsync(terminal.Cola.Id, 2m, "Broken bottles");

        Assert.True(movement.QtyDelta < 0m);
        Assert.Equal(8m, await terminal.LevelOfAsync(terminal.Cola.Id));
    }

    [Fact]
    public async Task A_write_off_requires_a_reason()
    {
        // Unexplained shrinkage is indistinguishable from theft.
        var terminal = new Terminal();
        await terminal.SeedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Stock.RecordShrinkageAsync(terminal.Cola.Id, 1m, "  "));
    }

    // ---------------------------------------------------------------------------- counts

    [Fact]
    public async Task A_count_records_the_difference_and_not_the_count()
    {
        // The critical property. Writing the counted figure as a movement would produce a correct
        // level only if the previous level were already right — which is what a count questions.
        var terminal = new Terminal();
        await terminal.SeedAsync();
        await terminal.Stock.RecordGoodsReceiptAsync(terminal.Cola.Id, 10m, "INV-1");

        // Counted 7, ledger says 10, so the movement must be -3.
        var movement = await terminal.Stock.RecordCountAsync(
            terminal.Cola.Id, 7m, "Thandi", "Weekly stocktake");

        Assert.Equal(-3m, movement.QtyDelta);
        Assert.Equal(7m, await terminal.LevelOfAsync(terminal.Cola.Id));
    }

    [Fact]
    public async Task A_count_that_finds_more_stock_records_a_positive_correction()
    {
        var terminal = new Terminal();
        await terminal.SeedAsync();
        await terminal.Stock.RecordShrinkageAsync(terminal.Cola.Id, 5m, "Assumed lost");

        var movement = await terminal.Stock.RecordCountAsync(
            terminal.Cola.Id, 0m, "Thandi", "Found in the back");

        Assert.Equal(5m, movement.QtyDelta);
        Assert.Equal(0m, await terminal.LevelOfAsync(terminal.Cola.Id));
    }

    [Fact]
    public async Task A_count_that_matches_refuses_to_record_an_empty_movement()
    {
        // An empty correction would clutter the ledger and make a level look better-evidenced than
        // it is.
        var terminal = new Terminal();
        await terminal.SeedAsync();
        await terminal.Stock.RecordGoodsReceiptAsync(terminal.Cola.Id, 10m, "INV-1");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Stock.RecordCountAsync(terminal.Cola.Id, 10m, "Thandi", "Stocktake"));

        Assert.Contains("nothing to correct", exception.Message);
    }

    [Fact]
    public async Task A_count_requires_a_reason()
    {
        var terminal = new Terminal();
        await terminal.SeedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Stock.RecordCountAsync(terminal.Cola.Id, 5m, "Thandi", "   "));
    }

    [Fact]
    public async Task A_negative_count_is_refused()
    {
        var terminal = new Terminal();
        await terminal.SeedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Stock.RecordCountAsync(terminal.Cola.Id, -1m, "Thandi", "Stocktake"));
    }

    [Fact]
    public async Task Counting_an_unknown_product_is_refused()
    {
        var terminal = new Terminal();
        await terminal.SeedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await terminal.Stock.RecordCountAsync("no-such-product", 5m, "Thandi", "Stocktake"));
    }

    [Fact]
    public async Task A_count_records_who_performed_it()
    {
        // A correction is a person changing what the system believes about stock, so it has to be
        // attributable.
        var terminal = new Terminal();
        await terminal.SeedAsync();
        await terminal.Stock.RecordGoodsReceiptAsync(terminal.Cola.Id, 10m, "INV-1");

        var movement = await terminal.Stock.RecordCountAsync(
            terminal.Cola.Id, 8m, "Thandi", "Stocktake");

        Assert.Contains("Thandi", movement.Reference!);
    }

    // -------------------------------------------------------------------------- ledger

    [Fact]
    public async Task Every_stock_change_appears_in_the_history()
    {
        // The ledger is what explains the level. If an event were missing from it, the number would
        // have to be taken on faith.
        var terminal = new Terminal();
        await terminal.SeedAsync();

        await terminal.Stock.RecordGoodsReceiptAsync(terminal.Cola.Id, 24m, "INV-1");
        await terminal.Stock.RecordShrinkageAsync(terminal.Cola.Id, 2m, "Damaged");
        await terminal.Stock.RecordCountAsync(terminal.Cola.Id, 20m, "Thandi", "Stocktake");

        var history = await terminal.Stock.GetHistoryAsync(terminal.Shop.Id.ToString(), terminal.Cola.Id);

        Assert.Equal(3, history.Count);
        Assert.Equal(20m, await terminal.LevelOfAsync(terminal.Cola.Id));
    }

    [Fact]
    public async Task A_stock_movement_is_queued_for_sync()
    {
        var terminal = new Terminal();
        await terminal.SeedAsync();

        await terminal.Stock.RecordGoodsReceiptAsync(terminal.Cola.Id, 5m, "INV-1");

        var pending = await terminal.Store.PeekOutboxAsync();

        Assert.Contains(pending, e => e.EntityType == "stockMovement");
    }

    [Fact]
    public async Task A_stock_movement_carries_a_terminal_sequence_for_ordering()
    {
        // The hub orders by (terminalId, terminalSeq), so a movement without one could not be
        // placed in the stream.
        var terminal = new Terminal();
        await terminal.SeedAsync();

        var first = await terminal.Stock.RecordGoodsReceiptAsync(terminal.Cola.Id, 5m, "INV-1");
        var second = await terminal.Stock.RecordGoodsReceiptAsync(terminal.Cola.Id, 5m, "INV-2");

        Assert.True(second.TerminalSeq > first.TerminalSeq);
        Assert.Equal("TILL-1", first.TerminalId);
    }

    [Fact]
    public async Task Levels_are_derived_identically_by_the_store_and_the_service()
    {
        // A level that differed depending on which path computed it would be worse than no level.
        var terminal = new Terminal();
        await terminal.SeedAsync();

        await terminal.Stock.RecordGoodsReceiptAsync(terminal.Cola.Id, 24m, "INV-1");
        await terminal.Stock.RecordShrinkageAsync(terminal.Cola.Id, 4m, "Damaged");

        var movements = await terminal.Store.GetStockMovementsAsync(
            terminal.Shop.Id.ToString(), terminal.Cola.Id);

        var fromService = StockService.DeriveLevels(movements)[terminal.Cola.Id];
        var fromStore = terminal.Store.DeriveStockLevels(terminal.Shop.Id.ToString())[terminal.Cola.Id];

        Assert.Equal(fromStore.Quantity, fromService.Quantity);
        Assert.Equal(fromStore.MovementCount, fromService.MovementCount);
    }

    [Fact]
    public async Task A_level_reports_how_many_movements_produced_it()
    {
        // A level from two movements and one from two hundred look identical as a number but mean
        // very different things.
        var terminal = new Terminal();
        await terminal.SeedAsync();

        await terminal.Stock.RecordGoodsReceiptAsync(terminal.Cola.Id, 10m, "INV-1");
        await terminal.Stock.RecordShrinkageAsync(terminal.Cola.Id, 1m, "Damaged");

        var items = await terminal.Stock.GetCatalogueAsync(terminal.Shop.Id.ToString());
        var item = Assert.Single(items);

        Assert.Equal(2, item.Stock.MovementCount);
        Assert.Equal(9m, item.Stock.Quantity);
        Assert.False(item.Stock.IsOutOfStock);
    }

    [Fact]
    public async Task A_product_can_sell_out_to_zero()
    {
        var terminal = new Terminal();
        await terminal.SeedAsync();
        await terminal.Stock.RecordGoodsReceiptAsync(terminal.Cola.Id, 3m, "INV-1");
        await terminal.Stock.RecordShrinkageAsync(terminal.Cola.Id, 3m, "All sold");

        var items = await terminal.Stock.GetCatalogueAsync(terminal.Shop.Id.ToString());
        var item = Assert.Single(items);

        Assert.True(item.Stock.IsOutOfStock);
        Assert.False(item.Stock.HasNoHistory);
    }

    [Fact]
    public async Task A_stock_movement_that_fails_to_commit_leaves_no_trace()
    {
        var terminal = new Terminal();
        await terminal.SeedAsync();

        var before = await terminal.Store.GetOutboxSummaryAsync();
        terminal.Store.FailNextCommit = new IOException("Disk full.");

        await Assert.ThrowsAsync<IOException>(async () =>
            await terminal.Stock.RecordGoodsReceiptAsync(terminal.Cola.Id, 5m, "INV-1"));

        var after = await terminal.Store.GetOutboxSummaryAsync();

        Assert.Equal(before.Total, after.Total);
        Assert.Equal(0m, await terminal.LevelOfAsync(terminal.Cola.Id));
    }
}
