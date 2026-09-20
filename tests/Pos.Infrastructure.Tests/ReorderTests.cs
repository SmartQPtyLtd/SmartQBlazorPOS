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
/// Tests for the reorder point, end to end from a sale.
/// </summary>
/// <remarks>
/// <para>
/// This is the chain the whole stock design exists for: a sale appends a movement, the level is derived
/// from the movements, and the reorder point is compared against that derived level. Nothing is stored
/// in between, so there is no counter to drift out of step with the ledger it summarises and no rebuild
/// step anybody has to remember.
/// </para>
/// <para>
/// The tests therefore drive a real sale through <see cref="CheckoutRecordingService"/> rather than
/// writing a movement by hand. A hand-written movement would prove the comparison works; it would not
/// prove that selling something moves the needle, which is the part a shop depends on.
/// </para>
/// </remarks>
public sealed class ReorderTests
{
    [Fact]
    public async Task Selling_stock_below_the_reorder_point_puts_the_item_on_the_list()
    {
        var terminal = new Terminal();

        var cola = terminal.Product(reorderLevel: 10m);

        await terminal.SeedAsync(cola);

        // Twelve in, so the level is known rather than merely absent.
        await terminal.Stock.RecordGoodsReceiptAsync(cola.Id, 12m, "Delivery note 4471");

        Assert.Empty(await terminal.Stock.GetReorderListAsync(terminal.StoreId));

        // Five out through a real sale: 7 left, which is under the reorder point of 10.
        await terminal.SellAsync(cola, quantity: 5m);

        var due = Assert.Single(await terminal.Stock.GetReorderListAsync(terminal.StoreId));

        Assert.Equal(cola.Id, due.Product.Id);
        Assert.Equal(7m, due.Stock.Quantity);
        Assert.True(due.NeedsReorder);

        // Three short of the reorder point, which is what somebody would actually order.
        Assert.Equal(3m, due.ReorderShortfall);
    }

    [Fact]
    public async Task An_item_that_is_counted_but_has_no_history_is_not_reordered()
    {
        // A derived level of zero for a product nobody has ever counted is the absence of information,
        // not an empty shelf. Reordering against it is how a shop ends up with a pallet of something it
        // already had in the back.
        var terminal = new Terminal();

        var cola = terminal.Product(reorderLevel: 10m);
        await terminal.SeedAsync(cola);

        var item = Assert.Single(await terminal.Stock.GetCatalogueAsync(terminal.StoreId));

        Assert.True(item.Stock.HasNoHistory);
        Assert.Equal(0m, item.Stock.Quantity);
        Assert.False(item.NeedsReorder);
        Assert.Empty(await terminal.Stock.GetReorderListAsync(terminal.StoreId));
    }

    [Fact]
    public async Task An_item_the_shop_does_not_count_is_never_reordered()
    {
        // A service, a carrier bag, a newspaper: things that return to no shelf. The movement is still
        // recorded — the flag decides what is reported, never what is written.
        var terminal = new Terminal();

        var bag = terminal.Product(reorderLevel: 100m, tracksStock: false);
        await terminal.SeedAsync(bag);

        await terminal.Stock.RecordGoodsReceiptAsync(bag.Id, 5m, "Opening");
        await terminal.SellAsync(bag, quantity: 5m);

        var item = Assert.Single(await terminal.Stock.GetCatalogueAsync(terminal.StoreId));

        Assert.Equal(0m, item.Stock.Quantity);
        Assert.False(item.NeedsReorder);
        Assert.Empty(await terminal.Stock.GetReorderListAsync(terminal.StoreId));
    }

    [Fact]
    public async Task An_item_with_no_reorder_point_is_never_reordered()
    {
        // Null means nobody has decided. Treating that as zero would put every new article on the list
        // the moment it sold out, and a list that is mostly noise stops being read.
        var terminal = new Terminal();

        var cola = terminal.Product(reorderLevel: null);
        await terminal.SeedAsync(cola);

        await terminal.Stock.RecordGoodsReceiptAsync(cola.Id, 3m, "Opening");
        await terminal.SellAsync(cola, quantity: 3m);

        var item = Assert.Single(await terminal.Stock.GetCatalogueAsync(terminal.StoreId));

        Assert.Equal(0m, item.Stock.Quantity);
        Assert.False(item.NeedsReorder);
    }

    [Fact]
    public async Task An_item_exactly_at_the_reorder_point_is_due()
    {
        // At the point, not below it. A reorder level that only fired once the shelf was emptier than
        // the number somebody chose would be reading a different number than the one they set.
        var terminal = new Terminal();

        var cola = terminal.Product(reorderLevel: 6m);
        await terminal.SeedAsync(cola);

        await terminal.Stock.RecordGoodsReceiptAsync(cola.Id, 10m, "Opening");
        await terminal.SellAsync(cola, quantity: 4m);

        var due = Assert.Single(await terminal.Stock.GetReorderListAsync(terminal.StoreId));

        Assert.Equal(6m, due.Stock.Quantity);
        Assert.True(due.NeedsReorder);
        Assert.Equal(0m, due.ReorderShortfall);
    }

    [Fact]
    public async Task A_withdrawn_item_drops_off_the_list()
    {
        // It will never be reordered however low it runs, and leaving it there is how a list stops
        // being read.
        var terminal = new Terminal();

        var cola = terminal.Product(reorderLevel: 10m);
        await terminal.SeedAsync(cola);

        await terminal.Stock.RecordGoodsReceiptAsync(cola.Id, 4m, "Opening");

        Assert.Single(await terminal.Stock.GetReorderListAsync(terminal.StoreId));

        await terminal.Stock.UpdateLocalProductAsync(
            cola,
            new ProductEdit(cola.Name, cola.UnitPrice, IsActive: false, ReorderLevel: 10m));

        Assert.Empty(await terminal.Stock.GetReorderListAsync(terminal.StoreId));
    }

    [Fact]
    public async Task The_list_is_ordered_by_how_much_is_missing()
    {
        // The order somebody would work down. A list sorted by name would have them walking the shop
        // in alphabetical order instead of ordering what is shortest first.
        var terminal = new Terminal();

        var nearlyFull = terminal.Product(name: "Nearly full", barcode: "6001000000101", reorderLevel: 10m);
        var almostOut = terminal.Product(name: "Almost out", barcode: "6001000000102", reorderLevel: 10m);

        await terminal.SeedAsync(nearlyFull, almostOut);

        await terminal.Stock.RecordGoodsReceiptAsync(nearlyFull.Id, 9m, "Opening");
        await terminal.Stock.RecordGoodsReceiptAsync(almostOut.Id, 1m, "Opening");

        var due = await terminal.Stock.GetReorderListAsync(terminal.StoreId);

        Assert.Equal(2, due.Count);
        Assert.Equal("Almost out", due[0].Product.Name);
        Assert.Equal(9m, due[0].ReorderShortfall);
        Assert.Equal("Nearly full", due[1].Product.Name);
    }

    [Fact]
    public async Task A_refund_puts_stock_back_and_can_take_an_item_off_the_list()
    {
        // The ledger is the truth in both directions. A refund that returned the goods to the shelf
        // must lift the level, or a shop orders replacements for stock it is holding.
        var terminal = new Terminal();

        var cola = terminal.Product(reorderLevel: 10m);
        await terminal.SeedAsync(cola);

        await terminal.Stock.RecordGoodsReceiptAsync(cola.Id, 12m, "Opening");
        var sale = await terminal.SellAsync(cola, quantity: 5m);

        Assert.Single(await terminal.Stock.GetReorderListAsync(terminal.StoreId));

        await terminal.ReturnAsync(sale, 5m);

        Assert.Equal(12m, await terminal.LevelAsync(cola.Id));
        Assert.Empty(await terminal.Stock.GetReorderListAsync(terminal.StoreId));
    }

    /// <summary>A terminal with one store, one catalogue, and a real checkout.</summary>
    private sealed class Terminal
    {
        private int _saleNumber;

        public InMemoryLocalStore Local { get; } = new();

        public StockService Stock { get; }

        public CheckoutRecordingService Checkout { get; }

        public ReturnRecordingService Returns { get; }

        public Store Shop { get; } = new()
        {
            // Aliased because the property below is also called StoreId, which shadows the type here.
            Id = Pos.Core.Domain.StoreId.New(),
            Name = "CORNER STORE",
            Code = "CT01",
            Currency = "ZAR",
            TaxMode = TaxMode.Inclusive,
            DefaultTaxRate = new TaxRate("VAT", 0.15m),
        };

        public string StoreId => Shop.Id.ToString();

        public Terminal()
        {
            var identity = new FixedTerminal("TILL-1");

            Stock = new StockService(Local, identity);
            Checkout = new CheckoutRecordingService(Local, new StubNumbers(() => ++_saleNumber), identity);
            Returns = new ReturnRecordingService(Local, identity);
        }

        public StoredProduct Product(
            decimal? reorderLevel,
            bool tracksStock = true,
            string name = "Cola 500ml",
            string barcode = "6001000000017") => new()
            {
                Id = ProductId.New().ToString(),
                StoreId = StoreId,
                Barcode = barcode,
                Name = name,
                UnitPrice = 15.00m,
                TaxName = "VAT",
                TaxRate = 0.15m,
                TracksStock = tracksStock,
                ReorderLevel = reorderLevel,
            };

        public Task SeedAsync(params StoredProduct[] products) => Local.UpsertProductsAsync(products);

        /// <summary>Rings up a sale of one product through the real checkout path.</summary>
        /// <remarks>
        /// The cart line carries the catalogue's own product id, which is what makes the resulting stock
        /// movement attribute to the product the reorder list is built from. A sale rung against a
        /// different id would leave the ledger and the catalogue describing two different articles.
        /// </remarks>
        public async Task<StoredSale> SellAsync(StoredProduct product, decimal quantity)
        {
            var cart = new Cart(Shop.Id, Shop.Currency, Shop.TaxMode);

            cart.Add(
                new ProductId(Guid.Parse(product.Id)),
                product.Barcode,
                product.Name,
                new TaxRate(product.TaxName, product.TaxRate),
                new Money(product.UnitPrice, Shop.Currency),
                quantity);

            var total = cart.CalculateTotals().Total;

            var completed = await Checkout.RecordSaleAsync(
                cart,
                [new Tender(TenderType.Cash, new Money(total, Shop.Currency), new Money(total, Shop.Currency))],
                Shop,
                employeeId: "operator-1");

            return completed.Stored;
        }

        /// <summary>Brings goods back through the real refund path.</summary>
        public Task<CompletedReturn> ReturnAsync(StoredSale sale, decimal quantity) =>
            Returns.RecordReturnAsync(
                sale.Id,
                [(sale.Lines[0].Barcode, quantity)],
                ReturnReason.ChangedMind,
                employeeId: "operator-1");

        public async Task<decimal> LevelAsync(string productId)
        {
            var movements = await Local.GetStockMovementsAsync(StoreId, productId);
            return movements.Sum(m => m.QtyDelta);
        }

        private sealed class FixedTerminal(string id) : ITerminalIdentity
        {
            public string TerminalId { get; } = id;
        }

        private sealed class StubNumbers(Func<int> next) : ISaleNumberSource
        {
            public Task<SaleNumber> NextAsync(
                StoreId storeId,
                string storeCode,
                DateOnly businessDate,
                CancellationToken ct = default) =>
                Task.FromResult(new SaleNumber(storeCode, businessDate, next()));
        }
    }
}
