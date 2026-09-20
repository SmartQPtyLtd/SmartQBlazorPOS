// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;
using Pos.Devices.EscPos;

namespace Pos.Devices.Tests;

/// <summary>
/// Tests for the printed station ticket.
/// </summary>
/// <remarks>
/// The routing rule is unit-tested in <c>Pos.Core.Tests</c>. What matters here is what reaches the
/// paper, because a kitchen ticket is read at a glance under pressure and the failure that hurts
/// is a cook reading past a line rather than an exception somewhere.
/// </remarks>
public sealed class StationTicketRendererTests
{
    private static readonly TaxRate Vat = new("VAT", 0.15m);

    private static Store NewStore(params (string Id, string Name)[] stations)
    {
        var store = new Store
        {
            Id = StoreId.New(),
            Name = "CORNER STORE",
            Code = "CT01",
            Currency = "ZAR",
            TaxMode = TaxMode.Inclusive,
            DefaultTaxRate = Vat,
            ReceiptColumns = 48,
        };

        store.Stations = [.. stations.Select(s => new PreparationStation(s.Id, s.Name))];

        return store;
    }

    private static Sale NewSale(params (string Name, decimal Qty, string? Station, string? Note)[] lines)
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
                    TaxAmount: 7.50m * l.Qty,
                    Note: l.Note,
                    StationId: l.Station)),
            ],
            Tenders = [new Tender(TenderType.Cash, new Money(100.00m, "ZAR"))],
            Tax = new TaxCalculation(TaxMode.Inclusive, Net: 0m, Tax: 0m, Gross: 0m, Components: []),
            Subtotal = 50.00m * lines.Sum(l => l.Qty),
            TotalDiscount = 0m,
            Total = 50.00m * lines.Sum(l => l.Qty),
        };
    }

    private static EscPosParser Print(Store store, Sale sale, string? stationId = null) =>
        EscPosParser.Parse(ReceiptRenderer.RenderKitchenTicket(new ReceiptDocument(
            Store: store,
            Sale: sale,
            Type: PosDocumentType.KitchenTicket,
            CashierName: "Thandi",
            StationId: stationId)));

    [Fact]
    public void A_station_ticket_carries_only_that_stations_lines()
    {
        // The defect this exists for: handing the renderer a whole sale and printing every line on
        // every station's printer. Printing the bar's drinks on the kitchen's ticket is worse than
        // useless — it is how a kitchen learns to read past what it is handed.
        var store = NewStore(("kitchen", "KITCHEN"), ("bar", "BAR"));
        var sale = NewSale(("Burger", 1m, "kitchen", null), ("Beer", 2m, "bar", null));

        var kitchen = Print(store, sale, "kitchen");
        var slip = string.Join('\n', kitchen.Lines);

        Assert.Contains("Burger", slip, StringComparison.Ordinal);
        Assert.DoesNotContain("Beer", slip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_station_ticket_is_headed_with_the_stations_name()
    {
        var store = NewStore(("kitchen", "KITCHEN"), ("bar", "BAR"));
        var sale = NewSale(("Beer", 1m, "bar", null));

        var slip = string.Join('\n', Print(store, sale, "bar").Lines);

        Assert.Contains("BAR", slip, StringComparison.Ordinal);
        Assert.DoesNotContain("KITCHEN", slip, StringComparison.Ordinal);
    }

    [Fact]
    public void An_undeclared_station_is_headed_with_its_raw_id()
    {
        // A configuration gap, made visible rather than hidden behind a guess.
        var store = NewStore(("kitchen", "KITCHEN"));
        var sale = NewSale(("Mystery", 1m, "grill", null));

        var slip = string.Join('\n', Print(store, sale, "grill").Lines);

        Assert.Contains("GRILL", slip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_item_count_is_the_stations_own()
    {
        // A cook counting items against a total that included the bar's drinks would think
        // something was missing every time.
        var store = NewStore(("kitchen", "KITCHEN"), ("bar", "BAR"));
        var sale = NewSale(("Burger", 2m, "kitchen", null), ("Beer", 3m, "bar", null));

        var kitchen = Print(store, sale, "kitchen");
        var itemsLine = kitchen.Lines.First(l => l.Contains("Items", StringComparison.Ordinal));

        Assert.Contains("2", itemsLine, StringComparison.Ordinal);
        Assert.DoesNotContain("5", itemsLine, StringComparison.Ordinal);
    }

    [Fact]
    public void A_modifier_reaches_the_station_that_has_to_honour_it()
    {
        var store = NewStore(("kitchen", "KITCHEN"));
        var sale = NewSale(("Burger", 1m, "kitchen", "No onions"));

        var slip = string.Join('\n', Print(store, sale, "kitchen").Lines);

        Assert.Contains("No onions", slip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ticket_carries_no_prices_at_all()
    {
        // It is a work instruction, not a transaction. A cook has no use for a total, and printing
        // one wastes paper on a busy pass.
        var store = NewStore(("kitchen", "KITCHEN"));
        var sale = NewSale(("Burger", 2m, "kitchen", null));

        var slip = string.Join('\n', Print(store, sale, "kitchen").Lines);

        Assert.DoesNotContain("100.00", slip, StringComparison.Ordinal);
        Assert.DoesNotContain("50.00", slip, StringComparison.Ordinal);
        Assert.DoesNotContain("VAT", slip, StringComparison.Ordinal);
        Assert.DoesNotContain("Total", slip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_station_ticket_is_partially_cut()
    {
        // Mounted vertically at the pass, a full cut drops the ticket on the floor.
        var store = NewStore(("kitchen", "KITCHEN"));
        var sale = NewSale(("Burger", 1m, "kitchen", null));

        var parser = Print(store, sale, "kitchen");

        Assert.True(parser.Cut);
        Assert.DoesNotContain("CutFull", parser.Commands);
    }

    [Fact]
    public void A_ticket_with_no_station_still_carries_the_whole_sale()
    {
        // A single-printer shop that sends everything to one machine, which is how this behaved
        // before stations existed.
        var store = NewStore(("kitchen", "KITCHEN"), ("bar", "BAR"));
        var sale = NewSale(("Burger", 1m, "kitchen", null), ("Beer", 1m, "bar", null));

        var slip = string.Join('\n', Print(store, sale).Lines);

        Assert.Contains("Burger", slip, StringComparison.Ordinal);
        Assert.Contains("Beer", slip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ticket_for_a_station_with_no_work_prints_nothing_to_make()
    {
        // Reachable when a sale is reprinted after the catalogue moved a product. An empty ticket
        // is odd but harmless; a ticket showing another station's work would not be.
        var store = NewStore(("kitchen", "KITCHEN"), ("bar", "BAR"));
        var sale = NewSale(("Burger", 1m, "kitchen", null));

        var slip = string.Join('\n', Print(store, sale, "bar").Lines);

        Assert.DoesNotContain("Burger", slip, StringComparison.Ordinal);
        Assert.Contains("Items", slip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_order_number_leads_the_ticket()
    {
        // An expediter matches a ticket to an order by its number, so it is the largest thing on
        // the page.
        var store = NewStore(("kitchen", "KITCHEN"));
        var sale = NewSale(("Burger", 1m, "kitchen", null));

        var slip = Print(store, sale, "kitchen");

        Assert.Contains("#0042", slip.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_ticket_names_who_took_the_order()
    {
        // So a disputed order can be traced to the person who rang it in.
        var store = NewStore(("kitchen", "KITCHEN"));
        var sale = NewSale(("Burger", 1m, "kitchen", null));

        var slip = string.Join('\n', Print(store, sale, "kitchen").Lines);

        Assert.Contains("Thandi", slip, StringComparison.Ordinal);
    }
}
