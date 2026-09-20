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
/// Local persistence tests. The offline-first design rests on two guarantees: a sale is
/// written atomically with its sync queue entry, and stock levels are derived from an
/// append-only ledger rather than stored as a value.
/// </summary>
public sealed class LocalStoreTests
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

    private static Store NewStore(TaxMode mode = TaxMode.Inclusive) => new()
    {
        Id = StoreId.New(),
        Name = "CORNER STORE",
        Code = "CT01",
        Currency = Zar,
        TaxMode = mode,
        DefaultTaxRate = Vat15,
        ReceiptColumns = 48,
    };

    private static Cart NewCartWith(Store store, params (string Name, decimal Price, decimal Qty)[] items)
    {
        var cart = new Cart(store.Id, store.Currency, store.TaxMode);

        foreach (var (name, price, qty) in items)
        {
            cart.Add(ProductId.New(), "1234567890123", name, Vat15, new Money(price, Zar), qty);
        }

        return cart;
    }

    private static (CheckoutRecordingService Service, InMemoryLocalStore Store) NewService(
        InMemoryLocalStore? localStore = null,
        string terminalId = "TILL-1")
    {
        var store = localStore ?? new InMemoryLocalStore();
        var service = new CheckoutRecordingService(
            store,
            new StubNumbers(),
            new FixedTerminal(terminalId));

        return (service, store);
    }

    private static Tender ExactCash(decimal total) =>
        new(TenderType.Cash, new Money(total, Zar), new Money(total, Zar));

    [Fact]
    public async Task A_sale_is_written_to_local_storage_before_anything_else()
    {
        // The terminal is the system of record while trading; nothing may depend on the
        // network being reachable.
        var (service, local) = NewService();
        var store = NewStore();
        var cart = NewCartWith(store, ("Cola", 15.00m, 1m));

        var completed = await service.RecordSaleAsync(cart, [ExactCash(15.00m)], store);

        var reloaded = await local.GetSaleAsync(completed.Stored.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(15.00m, reloaded.Total);
    }

    [Fact]
    public async Task Committing_a_sale_queues_it_for_sync_in_the_same_operation()
    {
        // A sale that is stored but never queued would silently never reach head office.
        var (service, local) = NewService();
        var store = NewStore();
        var cart = NewCartWith(store, ("Cola", 15.00m, 1m));

        await service.RecordSaleAsync(cart, [ExactCash(15.00m)], store);

        var summary = await local.GetOutboxSummaryAsync();
        Assert.True(summary.Pending > 0);

        var pending = await local.PeekOutboxAsync();
        Assert.Contains(pending, e => e.EntityType == "sale");
    }

    [Fact]
    public async Task A_failed_commit_leaves_no_sale_and_no_queue_entry()
    {
        // The all-or-nothing guarantee. A partial write would either lose a sale or
        // leave an unsendable queue entry behind.
        var local = new InMemoryLocalStore { FailNextCommit = new IOException("Disk full.") };
        var (service, _) = NewService(local);
        var store = NewStore();
        var cart = NewCartWith(store, ("Cola", 15.00m, 1m));

        await Assert.ThrowsAsync<IOException>(
            async () => await service.RecordSaleAsync(cart, [ExactCash(15.00m)], store));

        var sales = await local.GetSalesForDateAsync(store.Id.ToString(), DateOnly.FromDateTime(DateTime.Now));
        Assert.Empty(sales);
        Assert.True((await local.GetOutboxSummaryAsync()).IsClear);
    }

    [Fact]
    public async Task A_sale_produces_one_negative_stock_movement_per_line()
    {
        var (service, local) = NewService();
        var store = NewStore();
        var cart = NewCartWith(store, ("Cola", 15.00m, 3m), ("Bread", 20.00m, 1m));

        await service.RecordSaleAsync(cart, [ExactCash(65.00m)], store);

        Assert.Equal(2, local.Movements.Count);
        Assert.All(local.Movements, m => Assert.True(m.QtyDelta < 0));
        Assert.Contains(local.Movements, m => m.QtyDelta == -3m);
    }

    [Fact]
    public async Task Stock_level_is_derived_from_the_ledger_and_accumulates()
    {
        // Two sales of the same product must both be counted. A stored level with
        // last-write-wins would silently lose one.
        var (service, local) = NewService();
        var store = NewStore();
        var productId = ProductId.New();

        // Seed stock by hand so the derived level starts positive.
        var seed = new StockMovement
        {
            Id = Guid.CreateVersion7().ToString("N"),
            StoreId = store.Id.ToString(),
            TerminalId = "TILL-1",
            TerminalSeq = 100,
            ProductId = productId.ToString(),
            QtyDelta = 10m,
            Reason = StockMovementReason.GoodsReceipt.ToString(),
            OccurredAt = DateTimeOffset.UtcNow.ToString("O"),
        };

        var first = new Cart(store.Id, store.Currency, store.TaxMode);
        first.Add(productId, "123", "Cola", Vat15, new Money(15.00m, Zar), 3m);
        await service.RecordSaleAsync(first, [ExactCash(45.00m)], store);

        var second = new Cart(store.Id, store.Currency, store.TaxMode);
        second.Add(productId, "123", "Cola", Vat15, new Money(15.00m, Zar), 4m);
        await service.RecordSaleAsync(second, [ExactCash(60.00m)], store);

        var level = SaleMapper.DeriveStockLevel(
            [seed, .. local.Movements],
            store.Id.ToString(),
            productId.ToString());

        // 10 received, then 3 and 4 sold = 3 remaining.
        Assert.Equal(3m, level);
    }

    [Fact]
    public async Task A_voided_sale_appends_no_stock_movements()
    {
        // The goods never left; a compensating pair would double the ledger for one event.
        var (service, _) = NewService();
        var store = NewStore();
        var cart = NewCartWith(store, ("Cola", 15.00m, 1m));

        var completed = await service.RecordSaleAsync(cart, [ExactCash(15.00m)], store);

        var voided = new Sale
        {
            Id = completed.Sale.Id,
            StoreId = completed.Sale.StoreId,
            Number = completed.Sale.Number,
            CompletedAt = completed.Sale.CompletedAt,
            BusinessDate = completed.Sale.BusinessDate,
            Currency = completed.Sale.Currency,
            TaxMode = completed.Sale.TaxMode,
            Lines = completed.Sale.Lines,
            Tenders = completed.Sale.Tenders,
            Tax = completed.Sale.Tax,
            Subtotal = completed.Sale.Subtotal,
            TotalDiscount = completed.Sale.TotalDiscount,
            Total = completed.Sale.Total,
            Status = SaleStatus.Voided,
        };

        var movements = SaleMapper.ToStockMovements(voided, "TILL-1", 1);

        Assert.Empty(movements);
    }

    [Fact]
    public async Task Terminal_sequences_increase_without_gaps_across_sales()
    {
        // Sync orders by (terminalId, terminalSeq). Duplicated or gapped sequences would
        // make the stream ambiguous.
        var (service, local) = NewService();
        var store = NewStore();

        var first = await service.RecordSaleAsync(
            NewCartWith(store, ("Cola", 15.00m, 1m)), [ExactCash(15.00m)], store);

        var second = await service.RecordSaleAsync(
            NewCartWith(store, ("Cola", 15.00m, 1m)), [ExactCash(15.00m)], store);

        Assert.True(second.TerminalSeq > first.TerminalSeq);

        var sequences = local.Movements.Select(m => m.TerminalSeq).OrderBy(s => s).ToArray();
        Assert.Equal(sequences.Distinct().Count(), sequences.Length);
    }

    [Fact]
    public async Task Sale_numbers_continue_after_a_restart_rather_than_restarting_at_one()
    {
        // Reissuing a number already printed on a receipt would break returns and audits.
        var local = new InMemoryLocalStore();
        var date = DateOnly.FromDateTime(DateTime.Now);

        var first = await local.NextSaleSequenceAsync("store-1", date);
        var second = await local.NextSaleSequenceAsync("store-1", date);

        // A fresh service over the same store resumes the persisted counter.
        var (service, _) = NewService(local);
        var store = NewStore();
        await service.RecordSaleAsync(
            NewCartWith(store, ("Cola", 15.00m, 1m)), [ExactCash(15.00m)], store);

        var third = await local.NextSaleSequenceAsync("store-1", date);

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal(3, third);
    }

    [Fact]
    public async Task Sale_numbers_are_scoped_per_store_and_per_date()
    {
        var local = new InMemoryLocalStore();
        var today = new DateOnly(2026, 3, 25);
        var tomorrow = new DateOnly(2026, 3, 26);

        Assert.Equal(1, await local.NextSaleSequenceAsync("store-1", today));
        Assert.Equal(1, await local.NextSaleSequenceAsync("store-2", today));
        Assert.Equal(1, await local.NextSaleSequenceAsync("store-1", tomorrow));
        Assert.Equal(2, await local.NextSaleSequenceAsync("store-1", today));
    }

    [Fact]
    public async Task Stored_lines_keep_the_prices_as_charged()
    {
        // A later catalogue change must never alter a historic receipt.
        var (service, _) = NewService();
        var store = NewStore();
        var cart = NewCartWith(store, ("Cola", 19.99m, 2m));

        var completed = await service.RecordSaleAsync(cart, [ExactCash(39.98m)], store);

        var line = Assert.Single(completed.Stored.Lines);
        Assert.Equal(19.99m, line.UnitPrice);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(0.15m, line.TaxRate);
        Assert.Equal(39.98m, line.TaxableAmount);
    }

    [Fact]
    public async Task The_stored_total_is_not_recomputed_from_the_lines()
    {
        // Rounding rules can change; a historic receipt must not.
        var (service, _) = NewService();
        var store = NewStore();
        var cart = NewCartWith(store, ("Cola", 115.00m, 1m));

        var completed = await service.RecordSaleAsync(cart, [ExactCash(115.00m)], store);

        Assert.Equal(completed.Sale.Total, completed.Stored.Total);
        Assert.Equal(15.00m, completed.Stored.TaxTotal);
    }

    [Fact]
    public async Task Outbox_entries_drain_in_enqueue_order()
    {
        var (service, local) = NewService();
        var store = NewStore();

        await service.RecordSaleAsync(
            NewCartWith(store, ("Cola", 15.00m, 1m)), [ExactCash(15.00m)], store);

        await service.RecordSaleAsync(
            NewCartWith(store, ("Bread", 20.00m, 1m)), [ExactCash(20.00m)], store);

        var pending = await local.PeekOutboxAsync();
        var sequences = pending.Select(e => e.EnqueuedSeq).ToArray();

        Assert.Equal(sequences.OrderBy(s => s), sequences);
    }

    [Fact]
    public async Task Acknowledging_removes_entries_only_after_the_server_confirms()
    {
        var (service, local) = NewService();
        var store = NewStore();
        await service.RecordSaleAsync(
            NewCartWith(store, ("Cola", 15.00m, 1m)), [ExactCash(15.00m)], store);

        var pending = await local.PeekOutboxAsync();
        var ids = pending.Select(e => e.Id).ToArray();

        await local.AcknowledgeOutboxAsync(ids);

        Assert.True((await local.GetOutboxSummaryAsync()).IsClear);
    }

    [Fact]
    public async Task Repeated_failures_park_a_poison_entry_instead_of_blocking_the_queue()
    {
        // One unsendable record must not stop every later sale from syncing.
        var (service, local) = NewService();
        var store = NewStore();
        await service.RecordSaleAsync(
            NewCartWith(store, ("Cola", 15.00m, 1m)), [ExactCash(15.00m)], store);

        var entry = (await local.PeekOutboxAsync())[0];

        for (var attempt = 0; attempt < 8; attempt++)
        {
            await local.MarkOutboxFailedAsync(entry.Id, "Server rejected the record.");
        }

        var summary = await local.GetOutboxSummaryAsync();

        // A sale commits two queue entries: the sale itself and its stock movement.
        // Only the sale was failed repeatedly, so exactly that one is parked.
        Assert.Equal(1, summary.Dead);
        Assert.Equal(1, summary.Pending);

        var stillPending = await local.PeekOutboxAsync();
        Assert.DoesNotContain(stillPending, e => e.Id == entry.Id);
        Assert.Contains(stillPending, e => e.Id.StartsWith("movement:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_sale_is_not_part_of_the_pending_queue_after_a_successful_push()
    {
        var (service, local) = NewService();
        var store = NewStore();
        await service.RecordSaleAsync(
            NewCartWith(store, ("Cola", 15.00m, 1m)), [ExactCash(15.00m)], store);

        var pending = await local.PeekOutboxAsync();
        await local.AcknowledgeOutboxAsync([.. pending.Select(e => e.Id)]);

        Assert.Empty(await local.PeekOutboxAsync());
    }

    [Fact]
    public async Task Trading_day_totals_separate_takings_from_voids()
    {
        // A cash-up must show what was taken and what was reversed, not a single netted
        // figure that hides the voids.
        var (service, local) = NewService();
        var store = NewStore();
        var date = DateOnly.FromDateTime(DateTime.Now);

        await service.RecordSaleAsync(
            NewCartWith(store, ("Cola", 115.00m, 1m)), [ExactCash(115.00m)], store, businessDate: date);

        await service.RecordSaleAsync(
            NewCartWith(store, ("Bread", 20.00m, 1m)), [ExactCash(20.00m)], store, businessDate: date);

        var totals = await service.GetTradingDayTotalsAsync(store, date);

        Assert.Equal(2, totals.SaleCount);
        Assert.Equal(0, totals.VoidCount);
        Assert.Equal(135.00m, totals.Gross);

        // Inclusive 15% VAT extracted from a 135.00 gross. The bread is not zero-rated in
        // this fixture, so it carries tax too: 115.00 -> 15.00 and 20.00 -> 2.61.
        Assert.Equal(17.61m, totals.Tax);
        Assert.Equal(117.39m, totals.Net);
        Assert.Single(totals.TenderedByType);
        Assert.Equal(135.00m, totals.TenderedByType["Cash"]);
    }

    [Fact]
    public async Task Products_can_be_found_by_barcode_for_scanning()
    {
        var local = new InMemoryLocalStore();
        var storeId = StoreId.New().ToString();

        await local.UpsertProductsAsync(
        [
            new StoredProduct
            {
                Id = ProductId.New().ToString(),
                StoreId = storeId,
                Barcode = "6001234567890",
                Name = "Cola 500ml",
                UnitPrice = 15.00m,
                TaxName = "VAT",
                TaxRate = 0.15m,
            },
        ]);

        var found = await local.FindProductByBarcodeAsync(storeId, "6001234567890");
        Assert.NotNull(found);
        Assert.Equal("Cola 500ml", found.Name);

        Assert.Null(await local.FindProductByBarcodeAsync(storeId, "0000000000000"));

        // Another store's till must not resolve this scan to this store's row, and the barcode alone
        // is not enough to answer: a chain sells the same barcode in every branch.
        Assert.Null(await local.FindProductByBarcodeAsync(StoreId.New().ToString(), "6001234567890"));
    }

    /// <summary>
    /// The same barcode in two stores resolves to the scanning store's own row.
    /// </summary>
    /// <remarks>
    /// The reason the store scope exists, and the reason it costs money if it is missing. Two
    /// branches stock the same tin at different prices, so an unscoped lookup charges whichever row
    /// happened to be stored first — one shop's price at another shop's till.
    /// </remarks>
    [Fact]
    public async Task The_same_barcode_in_two_stores_resolves_to_the_scanning_store()
    {
        var local = new InMemoryLocalStore();
        var home = StoreId.New().ToString();
        var other = StoreId.New().ToString();

        await local.UpsertProductsAsync(
        [
            new StoredProduct
            {
                Id = ProductId.New().ToString(),
                StoreId = other,
                Barcode = "6001234567890",
                Name = "Cola 500ml (their price)",
                UnitPrice = 18.00m,
                TaxName = "VAT",
                TaxRate = 0.15m,
            },
            new StoredProduct
            {
                Id = ProductId.New().ToString(),
                StoreId = home,
                Barcode = "6001234567890",
                Name = "Cola 500ml",
                UnitPrice = 15.00m,
                TaxName = "VAT",
                TaxRate = 0.15m,
            },
        ]);

        var found = await local.FindProductByBarcodeAsync(home, "6001234567890");

        Assert.NotNull(found);
        Assert.Equal(15.00m, found.UnitPrice);
    }

    /// <summary>
    /// A name search is scoped to the store, and hides what the catalogue hides.
    /// </summary>
    /// <remarks>
    /// The fallback for a label that will not scan, and it asked a different question from the
    /// barcode lookup it stands in for: whether the store was the operator's own, and whether a
    /// product head office had deleted was sellable. Both answers have to match the catalogue list.
    /// </remarks>
    [Fact]
    public async Task A_name_search_is_scoped_to_the_store_and_hides_deleted_products()
    {
        var local = new InMemoryLocalStore();
        var home = StoreId.New().ToString();
        var other = StoreId.New().ToString();

        await local.UpsertProductsAsync(
        [
            Product(home, "Cola 500ml", active: true),
            Product(home, "Old Cola", active: false),
            Product(home, "Gone Cola", active: true, deleted: true),
            Product(other, "Other Shop Cola", active: true),
        ]);

        var found = await local.SearchProductsByNameAsync(home, "cola");

        var only = Assert.Single(found);

        Assert.Equal("Cola 500ml", only.Name);
    }

    private static StoredProduct Product(
        string storeId,
        string name,
        bool active,
        bool deleted = false) => new()
        {
            Id = ProductId.New().ToString(),
            StoreId = storeId,
            Barcode = Guid.CreateVersion7().ToString("N")[..13],
            Name = name,
            UnitPrice = 15.00m,
            TaxName = "VAT",
            TaxRate = 0.15m,
            IsActive = active,
            IsDeleted = deleted,
        };

    [Fact]
    public async Task Sync_cursors_persist_per_stream()
    {
        var local = new InMemoryLocalStore();

        Assert.Null(await local.GetCursorAsync("sales"));

        await local.SetCursorAsync("sales", "c-100");
        await local.SetCursorAsync("catalog", "c-7");

        Assert.Equal("c-100", await local.GetCursorAsync("sales"));
        Assert.Equal("c-7", await local.GetCursorAsync("catalog"));
    }

    [Fact]
    public async Task Wiping_removes_everything_including_the_queue()
    {
        // Used when a terminal is revoked: nothing may remain on the device.
        var (service, local) = NewService();
        var store = NewStore();
        await service.RecordSaleAsync(
            NewCartWith(store, ("Cola", 15.00m, 1m)), [ExactCash(15.00m)], store);

        await local.WipeAsync();

        Assert.True((await local.GetOutboxSummaryAsync()).IsClear);
        Assert.Empty(local.Movements);
        Assert.Null(await local.GetSaleAsync("anything"));
        Assert.Equal(1, await local.NextSaleSequenceAsync("store-1", DateOnly.FromDateTime(DateTime.Now)));
    }

    [Fact]
    public async Task A_sale_for_a_previous_trading_day_is_attributed_to_that_day()
    {
        // A till trading past midnight must still report against the day it belongs to.
        var (service, _) = NewService();
        var store = NewStore();
        var yesterday = DateOnly.FromDateTime(DateTime.Now).AddDays(-1);

        var completed = await service.RecordSaleAsync(
            NewCartWith(store, ("Cola", 15.00m, 1m)),
            [ExactCash(15.00m)],
            store,
            businessDate: yesterday);

        Assert.Equal(yesterday, completed.Sale.BusinessDate);
        Assert.Equal(
            yesterday.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            completed.Stored.BusinessDate);

        var totals = await service.GetTradingDayTotalsAsync(store, yesterday);
        Assert.Equal(1, totals.SaleCount);
    }

    [Fact]
    public async Task Concurrent_sales_on_one_till_get_distinct_terminal_sequences()
    {
        // A shared sequence would make the sync stream ambiguous.
        var (service, local) = NewService();
        var store = NewStore();

        var tasks = Enumerable.Range(0, 12).Select(_ => service.RecordSaleAsync(
            NewCartWith(store, ("Cola", 15.00m, 1m)),
            [ExactCash(15.00m)],
            store));

        var results = await Task.WhenAll(tasks);

        var saleSequences = results.Select(r => r.TerminalSeq).ToArray();
        Assert.Equal(saleSequences.Length, saleSequences.Distinct().Count());

        var all = local.Movements.Select(m => m.TerminalSeq)
            .Concat(saleSequences)
            .ToArray();

        Assert.Equal(all.Length, all.Distinct().Count());
    }

    // ------------------------------------------------------- terminal sequence allocator

    [Fact]
    public async Task A_reservation_hands_out_consecutive_numbers()
    {
        var local = new InMemoryLocalStore();

        Assert.Equal(1, await local.ReserveTerminalSequenceAsync(1));
        Assert.Equal(2, await local.ReserveTerminalSequenceAsync(3));

        // 2, 3 and 4 were claimed by the block above, so the next single number is 5.
        Assert.Equal(5, await local.ReserveTerminalSequenceAsync(1));
    }

    [Fact]
    public async Task A_reload_does_not_restart_the_terminal_sequence()
    {
        // The defect this exists for: a counter held in memory restarts at one when the till's
        // page is reloaded. Every record pushed after the reload then collides, on the hub's
        // unique index over (terminalId, terminalSeq), with one already stored — and because a
        // push is one transaction, that collision fails the whole batch and strands a real sale.
        //
        // Simulated here by building fresh services over the same storage, which is exactly what
        // a page reload does, and checking the stream continues rather than restarting.
        var local = new InMemoryLocalStore();
        var store = NewStore();

        var beforeReload = new CheckoutRecordingService(
            local, new StubNumbers(), new FixedTerminal("TILL-1"));

        var first = await beforeReload.RecordSaleAsync(
            NewCartWith(store, ("Cola", 15.00m, 1m)), [ExactCash(15.00m)], store);

        // Reload: new service objects, same durable storage.
        var afterReload = new CheckoutRecordingService(
            local, new StubNumbers(), new FixedTerminal("TILL-1"));

        var second = await afterReload.RecordSaleAsync(
            NewCartWith(store, ("Bread", 18.99m, 1m)), [ExactCash(18.99m)], store);

        Assert.Equal(1, first.TerminalSeq);
        Assert.True(
            second.TerminalSeq > first.TerminalSeq,
            $"A reload restarted the sequence: second sale took position {second.TerminalSeq} " +
            $"when {first.TerminalSeq} was already used.");
    }

    [Fact]
    public async Task Sales_and_drawer_events_share_one_sequence()
    {
        // Both are records this terminal authored, and they share one ordered stream. Two
        // independent counters would both start at one and hand a sale and a drawer event the
        // same position — which is not an edge case but the ordinary first minute of every shift.
        var local = new InMemoryLocalStore();
        var shifts = new InMemoryShiftStore();
        var terminal = new FixedTerminal("TILL-1");
        var store = NewStore();

        var checkout = new CheckoutRecordingService(local, new StubNumbers(), terminal);
        var shiftService = new ShiftService(shifts, local, terminal);

        var cashier = NewEmployee(store, "Thandi", EmployeePermissions.Sell | EmployeePermissions.OpenDrawer);
        await shifts.UpsertEmployeesAsync([StoredEmployee.FromDomain(cashier)]);

        var opened = await shiftService.OpenShiftAsync(cashier, openingFloat: 200m);

        var sale = await checkout.RecordSaleAsync(
            NewCartWith(store, ("Cola", 15.00m, 1m)), [ExactCash(15.00m)], store);

        await shiftService.RecordNoSaleAsync(cashier, "Customer wanted change");

        var events = await shifts.GetDrawerEventsAsync(opened.Id);

        var positions = events
            .Select(e => e.TerminalSeq)
            .Append(sale.TerminalSeq)
            .Concat(local.Movements.Select(m => m.TerminalSeq))
            .ToArray();

        Assert.Equal(positions.Length, positions.Distinct().Count());
    }

    private static Employee NewEmployee(Store store, string name, EmployeePermissions permissions)
    {
        var id = EmployeeId.New();

        return new Employee
        {
            Id = id,
            StoreId = store.Id,
            Name = name,
            PinHash = Pos.Infrastructure.Security.PinHasher.Hash(id, "7391"),
            Permissions = permissions,
        };
    }
}
