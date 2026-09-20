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
/// The station a product is prepared at, carried from the catalogue to a printed ticket.
/// </summary>
/// <remarks>
/// <para>
/// This is an integration test on purpose. The routing rule itself is unit-tested in
/// <c>Pos.Core.Tests</c>, and the ticket layout in <c>Pos.Devices.Tests</c>. What neither can
/// catch is a station being dropped somewhere along the chain — catalogue row, cart line, sale
/// line, stored line, rebuilt sale — because every individual step would still look correct while
/// the kitchen received nothing.
/// </para>
/// <para>
/// A dropped station does not throw and does not warn. It produces a sale with no ticket, which is
/// indistinguishable from a sale of shelf goods until a customer is waiting for food nobody is
/// cooking.
/// </para>
/// </remarks>
public sealed class StationRoutingTests
{
    private static readonly TaxRate Vat = new("VAT", 0.15m);

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
        Currency = "ZAR",
        TaxMode = TaxMode.Inclusive,
        DefaultTaxRate = Vat,
        ReceiptColumns = 48,
        Stations =
        [
            new PreparationStation("kitchen", "KITCHEN"),
            new PreparationStation("bar", "BAR"),
        ],
    };

    private static StoredProduct NewProduct(Store store, string name, decimal price, string? stationId) => new()
    {
        Id = ProductId.New().ToString(),
        StoreId = store.Id.ToString(),
        Barcode = $"600{Math.Abs(name.GetHashCode(StringComparison.Ordinal)) % 1_000_000_000:D9}",
        Name = name,
        UnitPrice = price,
        TaxName = Vat.Name,
        TaxRate = Vat.Rate,
        StationId = stationId,
    };

    private static Product ToDomain(StoredProduct stored, Store store) => new()
    {
        Id = new ProductId(Guid.Parse(stored.Id)),
        StoreId = store.Id,
        Barcode = stored.Barcode,
        Name = stored.Name,
        UnitPrice = new Money(stored.UnitPrice, store.Currency),
        TaxRate = Vat,
        StationId = stored.StationId,
    };

    [Fact]
    public void The_shared_projection_carries_every_field_the_till_needs()
    {
        // The regression this exists for. The till had its own hand-written projection onto a
        // domain product, and it copied the price, name, tax, and barcode while silently skipping
        // the station. A scanned burger therefore produced no kitchen ticket — and nothing failed.
        // The kitchen simply never heard about the order.
        //
        // One shared mapper means a field added to the stored row is carried everywhere, and this
        // test covers every caller of it.
        var store = NewStore();

        var stored = NewProduct(store, "Chicken Burger", 79.99m, "kitchen") with
        {
            Sku = "BURG-01",
            IsActive = false,
            IsOpenPrice = true,
            IsSoldByWeight = true,
        };

        var product = stored.ToDomain(store.Currency);

        Assert.Equal(stored.Id, product.Id.ToString());
        Assert.Equal(store.Id, product.StoreId);
        Assert.Equal("Chicken Burger", product.Name);
        Assert.Equal(79.99m, product.UnitPrice.Amount);
        Assert.Equal(store.Currency, product.UnitPrice.Currency);
        Assert.Equal(Vat.Name, product.TaxRate.Name);
        Assert.Equal(Vat.Rate, product.TaxRate.Rate);
        Assert.False(product.IsActive);
        Assert.True(product.IsOpenPrice);
        Assert.True(product.IsSoldByWeight);

        // The field that was dropped.
        Assert.Equal("kitchen", product.StationId);
    }

    [Fact]
    public void A_projected_product_carries_its_station_onto_the_cart_line()
    {
        // The other half: even a correct projection is useless if the cart is not told about it.
        var store = NewStore();
        var stored = NewProduct(store, "Craft Beer", 45.00m, "bar");

        var cart = new Cart(store.Id, store.Currency, store.TaxMode);
        cart.AddProduct(stored.ToDomain(store.Currency));

        Assert.Equal("bar", Assert.Single(cart.Lines).StationId);
    }

    private static (CheckoutRecordingService Checkout, InMemoryLocalStore Store) NewTerminal()
    {
        var local = new InMemoryLocalStore();

        return (new CheckoutRecordingService(local, new StubNumbers(), new FixedTerminal("TILL-1")), local);
    }

    [Fact]
    public async Task A_prepared_item_reaches_its_station_ticket()
    {
        var store = NewStore();
        var (checkout, local) = NewTerminal();

        var burger = NewProduct(store, "Chicken Burger", 79.99m, "kitchen");
        await local.UpsertProductsAsync([burger]);

        var cart = new Cart(store.Id, store.Currency, store.TaxMode);
        cart.AddProduct(ToDomain(burger, store));

        var completed = await checkout.RecordSaleAsync(
            cart, [new Tender(TenderType.Cash, new Money(79.99m, store.Currency))], store);

        var tickets = StationTicketRouter.Route(store, completed.Sale);

        var ticket = Assert.Single(tickets);
        Assert.Equal("KITCHEN", ticket.StationName);
        Assert.Equal("Chicken Burger", Assert.Single(ticket.Lines).Name);
    }

    [Fact]
    public async Task A_shelf_item_produces_no_ticket()
    {
        var store = NewStore();
        var (checkout, local) = NewTerminal();

        var cola = NewProduct(store, "Cola 500ml", 15.00m, stationId: null);
        await local.UpsertProductsAsync([cola]);

        var cart = new Cart(store.Id, store.Currency, store.TaxMode);
        cart.AddProduct(ToDomain(cola, store));

        var completed = await checkout.RecordSaleAsync(
            cart, [new Tender(TenderType.Cash, new Money(15.00m, store.Currency))], store);

        Assert.Empty(StationTicketRouter.Route(store, completed.Sale));
    }

    [Fact]
    public async Task One_order_splits_across_the_kitchen_and_the_bar()
    {
        var store = NewStore();
        var (checkout, local) = NewTerminal();

        var burger = NewProduct(store, "Chicken Burger", 79.99m, "kitchen");
        var beer = NewProduct(store, "Craft Beer", 45.00m, "bar");
        var cola = NewProduct(store, "Cola 500ml", 15.00m, stationId: null);

        await local.UpsertProductsAsync([burger, beer, cola]);

        var cart = new Cart(store.Id, store.Currency, store.TaxMode);
        cart.AddProduct(ToDomain(burger, store));
        cart.AddProduct(ToDomain(beer, store));
        cart.AddProduct(ToDomain(cola, store));

        var completed = await checkout.RecordSaleAsync(
            cart, [new Tender(TenderType.Cash, new Money(139.99m, store.Currency))], store);

        var tickets = StationTicketRouter.Route(store, completed.Sale);

        Assert.Equal(2, tickets.Count);
        Assert.Equal(["Chicken Burger"], tickets[0].Lines.Select(l => l.Name));
        Assert.Equal(["Craft Beer"], tickets[1].Lines.Select(l => l.Name));
    }

    [Fact]
    public async Task The_station_survives_storage_and_a_reprint()
    {
        // A reprint must route the way the product was configured when it was sold, not where it
        // sits in the catalogue today. A product moved from the kitchen to the bar next week must
        // not send a reprint of last week's order to the wrong printer.
        var store = NewStore();
        var (checkout, local) = NewTerminal();

        var burger = NewProduct(store, "Chicken Burger", 79.99m, "kitchen");
        await local.UpsertProductsAsync([burger]);

        var cart = new Cart(store.Id, store.Currency, store.TaxMode);
        cart.AddProduct(ToDomain(burger, store));

        var completed = await checkout.RecordSaleAsync(
            cart, [new Tender(TenderType.Cash, new Money(79.99m, store.Currency))], store);

        // The product moves to the bar, as a shop reorganising its kitchen would do.
        await local.UpsertProductsAsync([burger with { StationId = "bar" }]);

        var reloaded = await local.GetSaleAsync(completed.Stored.Id);
        Assert.NotNull(reloaded);

        var rebuilt = SaleMapper.ToDomain(reloaded);

        var ticket = Assert.Single(StationTicketRouter.Route(store, rebuilt));
        Assert.Equal("KITCHEN", ticket.StationName);
    }

    [Fact]
    public async Task Two_of_the_same_item_for_different_stations_are_separate_lines()
    {
        // The same product ordered once for the kitchen and once for the bar is different work,
        // and merging them would send the wrong quantity to the wrong place. Reachable in practice
        // when a product is reassigned mid-sale, or when two catalogue entries share a barcode.
        var store = NewStore();
        var (checkout, local) = NewTerminal();

        var forKitchen = NewProduct(store, "Special", 50.00m, "kitchen");
        var forBar = NewProduct(store, "Special", 50.00m, "bar");

        await local.UpsertProductsAsync([forKitchen, forBar]);

        var cart = new Cart(store.Id, store.Currency, store.TaxMode);
        cart.AddProduct(ToDomain(forKitchen, store));
        cart.AddProduct(ToDomain(forBar, store));

        Assert.Equal(2, cart.Lines.Count);

        var completed = await checkout.RecordSaleAsync(
            cart, [new Tender(TenderType.Cash, new Money(100.00m, store.Currency))], store);

        Assert.Equal(2, StationTicketRouter.Route(store, completed.Sale).Count);
    }

    [Fact]
    public async Task A_station_cleared_in_the_catalogue_stops_producing_tickets()
    {
        var store = NewStore();
        var (checkout, local) = NewTerminal();

        var burger = NewProduct(store, "Chicken Burger", 79.99m, "kitchen");
        await local.UpsertProductsAsync([burger]);

        // The shop stops cooking it and sells it as a takeaway item instead.
        await local.UpsertProductsAsync([burger with { StationId = null }]);

        var stored = await local.GetProductAsync(burger.Id);
        Assert.NotNull(stored);

        var cart = new Cart(store.Id, store.Currency, store.TaxMode);
        cart.AddProduct(ToDomain(stored, store));

        var completed = await checkout.RecordSaleAsync(
            cart, [new Tender(TenderType.Cash, new Money(79.99m, store.Currency))], store);

        Assert.Empty(StationTicketRouter.Route(store, completed.Sale));
    }

    [Fact]
    public async Task A_voided_sale_produces_no_ticket_even_though_the_station_is_recorded()
    {
        // The goods were never made, so a ticket is a real cost: someone cooks food nobody pays
        // for.
        var store = NewStore();
        var (checkout, local) = NewTerminal();

        var burger = NewProduct(store, "Chicken Burger", 79.99m, "kitchen");
        await local.UpsertProductsAsync([burger]);

        var cart = new Cart(store.Id, store.Currency, store.TaxMode);
        cart.AddProduct(ToDomain(burger, store));

        var completed = await checkout.RecordSaleAsync(
            cart, [new Tender(TenderType.Cash, new Money(79.99m, store.Currency))], store);

        completed.Sale.Status = SaleStatus.Voided;
        completed.Sale.VoidReason = "Rung in error";

        Assert.Empty(StationTicketRouter.Route(store, completed.Sale));
    }

    [Fact]
    public async Task Weighed_prepared_goods_carry_their_fractional_quantity_to_the_ticket()
    {
        var store = NewStore();
        var (checkout, local) = NewTerminal();

        var ribs = NewProduct(store, "Ribs (per kg)", 189.00m, "kitchen");
        await local.UpsertProductsAsync([ribs]);

        var cart = new Cart(store.Id, store.Currency, store.TaxMode);
        cart.AddProduct(ToDomain(ribs, store), quantity: 0.734m);

        var completed = await checkout.RecordSaleAsync(
            cart, [new Tender(TenderType.Cash, new Money(138.73m, store.Currency))], store);

        var ticket = Assert.Single(StationTicketRouter.Route(store, completed.Sale));

        Assert.Equal(0.734m, ticket.TotalQuantity);
    }
}
