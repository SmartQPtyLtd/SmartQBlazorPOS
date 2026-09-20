// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;

namespace Pos.Core.Tests;

/// <summary>
/// Tests for splitting a sale across preparation stations.
/// </summary>
/// <remarks>
/// The rule under test decides what the kitchen and the bar each receive. Getting it wrong is not
/// a display bug: a missing line is food nobody cooks, and a line sent to the wrong station is
/// food cooked twice or not at all.
/// </remarks>
public sealed class StationTicketRouterTests
{
    private static readonly TaxRate Vat = new("VAT", 0.15m);

    private static Store NewStore(params (string Id, string Name)[] stations) => new()
    {
        Id = StoreId.New(),
        Name = "CORNER STORE",
        Code = "CT01",
        Currency = "ZAR",
        TaxMode = TaxMode.Inclusive,
        DefaultTaxRate = Vat,
        ReceiptColumns = 48,
        Stations = [.. stations.Select(s => new PreparationStation(s.Id, s.Name))],
    };

    private static Sale NewSale(params (string Name, decimal Qty, string? Station)[] lines)
    {
        var store = StoreId.New();

        return new Sale
        {
            Id = SaleId.New(),
            StoreId = store,
            Number = new SaleNumber("CT01", new DateOnly(2026, 3, 25), 42),
            CompletedAt = new DateTimeOffset(2026, 3, 25, 12, 30, 0, TimeSpan.Zero),
            BusinessDate = new DateOnly(2026, 3, 25),
            Currency = "ZAR",
            TaxMode = TaxMode.Inclusive,
            Lines =
            [
                .. lines.Select(l => new SaleLine(
                    ProductId: ProductId.New(),
                    Barcode: "6001000000017",
                    Name: l.Name,
                    Quantity: l.Qty,
                    UnitPrice: new Money(50.00m, "ZAR"),
                    TaxRate: Vat,
                    DiscountAmount: 0m,
                    TaxableAmount: 50.00m * l.Qty,
                    TaxAmount: 0m,
                    StationId: l.Station)),
            ],
            Tenders = [new Tender(TenderType.Cash, new Money(100.00m, "ZAR"))],
            Tax = new TaxCalculation(TaxMode.Inclusive, Net: 0m, Tax: 0m, Gross: 0m, Components: []),
            Subtotal = 50.00m * lines.Sum(l => l.Qty),
            TotalDiscount = 0m,
            Total = 50.00m * lines.Sum(l => l.Qty),
        };
    }

    [Fact]
    public void Lines_are_grouped_by_their_station()
    {
        var store = NewStore(("kitchen", "KITCHEN"), ("bar", "BAR"));
        var sale = NewSale(("Burger", 1m, "kitchen"), ("Beer", 2m, "bar"), ("Fries", 1m, "kitchen"));

        var tickets = StationTicketRouter.Route(store, sale);

        Assert.Equal(2, tickets.Count);
        Assert.Equal(["Burger", "Fries"], tickets[0].Lines.Select(l => l.Name));
        Assert.Equal(["Beer"], tickets[1].Lines.Select(l => l.Name));
    }

    [Fact]
    public void Tickets_are_produced_in_the_stores_declared_order()
    {
        // A shop that prints the kitchen before the bar expects that every time. Dictionary order
        // would shuffle them between runs and make the pass unpredictable.
        var store = NewStore(("kitchen", "KITCHEN"), ("bar", "BAR"));

        // Scanned bar-first, which must not decide the printing order.
        var sale = NewSale(("Beer", 1m, "bar"), ("Burger", 1m, "kitchen"));

        var tickets = StationTicketRouter.Route(store, sale);

        Assert.Equal(["KITCHEN", "BAR"], tickets.Select(t => t.StationName));
    }

    [Fact]
    public void The_station_name_comes_from_the_store_not_the_product()
    {
        // So a ticket reads "BAR" rather than "bar-01". The id is for routing; the name is for the
        // person reading it.
        var store = NewStore(("bar-01", "BAR"));
        var sale = NewSale(("Beer", 1m, "bar-01"));

        var ticket = Assert.Single(StationTicketRouter.Route(store, sale));

        Assert.Equal("BAR", ticket.StationName);
        Assert.Equal("bar-01", ticket.StationId);
    }

    [Fact]
    public void A_product_with_no_station_produces_no_ticket()
    {
        // A bottled drink is handed over at the till. Sending it to a kitchen printer wastes paper
        // and, worse, trains the kitchen to read past the tickets it is given.
        var store = NewStore(("kitchen", "KITCHEN"));
        var sale = NewSale(("Cola", 1m, null));

        Assert.Empty(StationTicketRouter.Route(store, sale));
    }

    [Fact]
    public void A_sale_of_only_shelf_goods_produces_nothing_at_all()
    {
        var store = NewStore(("kitchen", "KITCHEN"));
        var sale = NewSale(("Cola", 1m, null), ("Chips", 2m, null));

        Assert.Empty(StationTicketRouter.Route(store, sale));
    }

    [Fact]
    public void A_product_pointing_at_an_undeclared_station_still_gets_a_ticket()
    {
        // The configuration gap is real, but dropping the line silently is the one outcome that
        // loses a customer's order. It prints under its raw id so the gap is visible.
        var store = NewStore(("kitchen", "KITCHEN"));
        var sale = NewSale(("Mystery", 1m, "grill"));

        var ticket = Assert.Single(StationTicketRouter.Route(store, sale));

        Assert.Equal("grill", ticket.StationId);
        Assert.Equal("GRILL", ticket.StationName);
        Assert.Equal(["Mystery"], ticket.Lines.Select(l => l.Name));
    }

    [Fact]
    public void Undeclared_stations_come_after_the_declared_ones_and_in_a_stable_order()
    {
        var store = NewStore(("kitchen", "KITCHEN"));
        var sale = NewSale(("A", 1m, "zebra"), ("B", 1m, "apple"), ("C", 1m, "kitchen"));

        var tickets = StationTicketRouter.Route(store, sale);

        Assert.Equal(["KITCHEN", "APPLE", "ZEBRA"], tickets.Select(t => t.StationName));
    }

    [Fact]
    public void Stations_are_matched_regardless_of_case()
    {
        // The station id is typed by a person on a product form. "Kitchen" and "kitchen" are the
        // same place, and treating them as two would print two tickets for one order.
        var store = NewStore(("kitchen", "KITCHEN"));
        var sale = NewSale(("Burger", 1m, "KITCHEN"));

        var ticket = Assert.Single(StationTicketRouter.Route(store, sale));

        Assert.Equal("KITCHEN", ticket.StationName);
    }

    [Fact]
    public void A_voided_sale_produces_no_tickets()
    {
        // The goods were never made. A ticket for a reversed sale is a real cost: someone cooks
        // food nobody pays for.
        var store = NewStore(("kitchen", "KITCHEN"));

        var sale = NewSale(("Burger", 1m, "kitchen"));
        sale.Status = SaleStatus.Voided;
        sale.VoidReason = "Customer changed their mind";

        Assert.Empty(StationTicketRouter.Route(store, sale));
    }

    [Fact]
    public void An_empty_sale_produces_no_tickets()
    {
        var store = NewStore(("kitchen", "KITCHEN"));
        var sale = NewSale();

        Assert.Empty(StationTicketRouter.Route(store, sale));
    }

    [Fact]
    public void A_station_that_is_only_declared_produces_nothing()
    {
        var store = NewStore(("kitchen", "KITCHEN"), ("bar", "BAR"));
        var sale = NewSale(("Burger", 1m, "kitchen"));

        var ticket = Assert.Single(StationTicketRouter.Route(store, sale));

        Assert.Equal("KITCHEN", ticket.StationName);
    }

    [Fact]
    public void A_ticket_counts_only_its_own_items()
    {
        // A cook counting items on the ticket against a total that included the bar's drinks would
        // think something was missing every time.
        var store = NewStore(("kitchen", "KITCHEN"), ("bar", "BAR"));
        var sale = NewSale(("Burger", 2m, "kitchen"), ("Beer", 3m, "bar"));

        var tickets = StationTicketRouter.Route(store, sale);

        Assert.Equal(2m, tickets[0].TotalQuantity);
        Assert.Equal(3m, tickets[1].TotalQuantity);
    }

    [Fact]
    public void Lines_keep_the_order_they_were_scanned_in()
    {
        // The scan order is the order the customer asked for things, and the order the kitchen
        // would otherwise have to reconstruct from the receipt.
        var store = NewStore(("kitchen", "KITCHEN"));
        var sale = NewSale(("Starter", 1m, "kitchen"), ("Main", 1m, "kitchen"), ("Side", 1m, "kitchen"));

        var ticket = Assert.Single(StationTicketRouter.Route(store, sale));

        Assert.Equal(["Starter", "Main", "Side"], ticket.Lines.Select(l => l.Name));
    }

    [Fact]
    public void A_store_with_no_declared_stations_routes_nothing()
    {
        // A convenience store prepares nothing. It should not be printing tickets at all.
        var store = NewStore();
        var sale = NewSale(("Burger", 1m, "kitchen"));

        var ticket = Assert.Single(StationTicketRouter.Route(store, sale));

        // The undeclared-station rule still applies, so the line is not lost, but it prints under
        // its raw id rather than a name the shop chose.
        Assert.Equal("KITCHEN", ticket.StationName);
    }
}
