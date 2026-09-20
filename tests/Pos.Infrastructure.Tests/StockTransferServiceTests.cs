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
/// Tests for moving stock between two stores.
/// </summary>
/// <remarks>
/// The lifecycle rules are unit-tested in <c>Pos.Core.Tests</c>. What matters here is that the two
/// events reach the ledger and the documents together, and that stock genuinely leaves one store
/// and arrives at the other — the property that makes a transfer auditable rather than a note
/// somebody wrote.
/// </remarks>
public sealed class StockTransferServiceTests
{
    private static readonly TaxRate Vat = new("VAT", 0.15m);

    private sealed class FixedTerminal(string id) : ITerminalIdentity
    {
        public string TerminalId { get; } = id;
    }

    /// <summary>
    /// Two stores sharing one ledger, which is what a single terminal driving both ends looks like
    /// in a test. On real hardware they are separate tills that meet at the hub.
    /// </summary>
    private sealed class Estate
    {
        public Store CapeTown { get; } = NewStore("CT01", "Cape Town");

        public Store Johannesburg { get; } = NewStore("JN01", "Johannesburg");

        public InMemoryLocalStore Ledger { get; } = new();

        public StockTransferService Transfers { get; }

        public Employee Supervisor { get; }

        private const string SupervisorPin = "7395";

        public Estate()
        {
            var terminal = new FixedTerminal("TILL-1");

            Transfers = new StockTransferService(Ledger, new InMemoryShiftStore(), terminal);

            Supervisor = new Employee
            {
                Id = EmployeeId.New(),
                StoreId = CapeTown.Id,
                Name = "Sipho",
                PinHash = "not-used-here",
                Permissions = EmployeePermissions.Supervisor,
            };
        }

        /// <summary>The same operator, signed in at the other store.</summary>
        public Employee AtJohannesburg() => new()
        {
            Id = Supervisor.Id,
            StoreId = Johannesburg.Id,
            Name = Supervisor.Name,
            PinHash = Supervisor.PinHash,
            Permissions = Supervisor.Permissions,
        };

        public decimal Stock(string storeId, string productId) =>
            Ledger.DeriveStockLevels(storeId).TryGetValue(productId, out var level) ? level.Quantity : 0m;

        private static Store NewStore(string code, string name) => new()
        {
            Id = StoreId.New(),
            Name = name,
            Code = code,
            Currency = "ZAR",
            TaxMode = TaxMode.Inclusive,
            DefaultTaxRate = Vat,
            ReceiptColumns = 48,
        };
    }

    private static StockTransferLine Line(string productId, decimal quantity, string name = "Cola 500ml") =>
        new(productId, "6001000000017", name, quantity);

    /// <summary>
    /// Puts a product on the catalogue and opens a stock balance for it.
    /// </summary>
    /// <remarks>
    /// Stock is derived from the movement ledger, so a product alone has none. Seeding a goods
    /// receipt is what gives the sending store something to send.
    /// </remarks>
    private static async Task SeedStockAsync(Estate estate, string productId, decimal quantity, StoreId storeId)
    {
        await estate.Ledger.UpsertProductsAsync(
        [
            new StoredProduct
            {
                Id = productId,
                StoreId = storeId.ToString(),
                Barcode = "6001000000017",
                Name = "Cola 500ml",
                UnitPrice = 15.00m,
                TaxName = Vat.Name,
                TaxRate = Vat.Rate,
            },
        ]);

        await estate.Ledger.RecordStockMovementAsync(
            new StockMovement
            {
                Id = Guid.CreateVersion7().ToString("N"),
                StoreId = storeId.ToString(),
                TerminalId = "TILL-1",
                TerminalSeq = await estate.Ledger.ReserveTerminalSequenceAsync(),
                ProductId = productId,
                QtyDelta = quantity,
                Reason = StockMovementReason.GoodsReceipt.ToString(),
                Reference = "Opening balance",
                OccurredAt = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            },
            "{}",
            "TILL-1");
    }

    // ------------------------------------------------------------------- the round trip

    [Fact]
    public async Task Dispatching_takes_stock_out_of_the_sending_store()
    {
        var estate = new Estate();
        await SeedStockAsync(estate, "p1", 50m, estate.CapeTown.Id);

        var transfer = await estate.Transfers.RaiseAsync(
            estate.Supervisor,
            estate.CapeTown.Id,
            estate.Johannesburg.Id,
            "CT01",
            "JN01",
            [Line("p1", 10m)]);

        await estate.Transfers.DispatchAsync(transfer, estate.Supervisor);

        Assert.Equal(40m, estate.Stock(estate.CapeTown.Id.ToString(), "p1"));

        // And nothing has appeared at the destination yet. The goods are on a van.
        Assert.Equal(0m, estate.Stock(estate.Johannesburg.Id.ToString(), "p1"));
    }

    [Fact]
    public async Task Receiving_books_the_stock_into_the_destination()
    {
        var estate = new Estate();
        await SeedStockAsync(estate, "p1", 50m, estate.CapeTown.Id);

        var transfer = await estate.Transfers.RaiseAsync(
            estate.Supervisor, estate.CapeTown.Id, estate.Johannesburg.Id, "CT01", "JN01", [Line("p1", 10m)]);

        await estate.Transfers.DispatchAsync(transfer, estate.Supervisor);
        await estate.Transfers.ReceiveAsync(transfer, estate.AtJohannesburg());

        Assert.Equal(40m, estate.Stock(estate.CapeTown.Id.ToString(), "p1"));
        Assert.Equal(10m, estate.Stock(estate.Johannesburg.Id.ToString(), "p1"));
    }

    [Fact]
    public async Task The_whole_transfer_moves_across_the_two_stores()
    {
        // Conservation: what left one side arrived at the other. This is the property that makes a
        // transfer auditable rather than a note somebody wrote.
        var estate = new Estate();
        await SeedStockAsync(estate, "p1", 100m, estate.CapeTown.Id);

        var transfer = await estate.Transfers.RaiseAsync(
            estate.Supervisor,
            estate.CapeTown.Id,
            estate.Johannesburg.Id,
            "CT01",
            "JN01",
            [Line("p1", 30m), Line("p2", 12.5m, "Still Water")]);

        await estate.Transfers.DispatchAsync(transfer, estate.Supervisor);
        await estate.Transfers.ReceiveAsync(transfer, estate.AtJohannesburg());

        Assert.Equal(70m, estate.Stock(estate.CapeTown.Id.ToString(), "p1"));
        Assert.Equal(30m, estate.Stock(estate.Johannesburg.Id.ToString(), "p1"));
        Assert.Equal(12.5m, estate.Stock(estate.Johannesburg.Id.ToString(), "p2"));
    }

    // -------------------------------------------------------------------- shortages

    [Fact]
    public async Task Stock_lost_in_transit_reaches_neither_store()
    {
        // The reason the discrepancy is recorded: the sending store is short by what it sent, the
        // receiving store gains only what arrived, and the difference is a figure somebody has to
        // answer for rather than stock that quietly evaporates.
        var estate = new Estate();
        await SeedStockAsync(estate, "p1", 50m, estate.CapeTown.Id);

        var transfer = await estate.Transfers.RaiseAsync(
            estate.Supervisor, estate.CapeTown.Id, estate.Johannesburg.Id, "CT01", "JN01", [Line("p1", 10m)]);

        await estate.Transfers.DispatchAsync(transfer, estate.Supervisor);

        await estate.Transfers.ReceiveAsync(
            transfer,
            estate.AtJohannesburg(),
            new Dictionary<string, decimal>(StringComparer.Ordinal) { ["p1"] = 8m },
            note: "Two cases crushed");

        Assert.Equal(40m, estate.Stock(estate.CapeTown.Id.ToString(), "p1"));
        Assert.Equal(8m, estate.Stock(estate.Johannesburg.Id.ToString(), "p1"));

        var stored = await estate.Transfers.GetAsync(transfer.Id);
        Assert.NotNull(stored);
        Assert.Equal(2m, stored.TotalDiscrepancy);
        Assert.True(stored.HasDiscrepancy);
        Assert.Equal("Two cases crushed", stored.Note);
    }

    [Fact]
    public async Task A_received_transfer_cannot_be_received_again()
    {
        // Booking it in twice would double the destination's stock for goods that arrived once.
        var estate = new Estate();
        await SeedStockAsync(estate, "p1", 50m, estate.CapeTown.Id);

        var transfer = await estate.Transfers.RaiseAsync(
            estate.Supervisor, estate.CapeTown.Id, estate.Johannesburg.Id, "CT01", "JN01", [Line("p1", 10m)]);

        await estate.Transfers.DispatchAsync(transfer, estate.Supervisor);
        await estate.Transfers.ReceiveAsync(transfer, estate.AtJohannesburg());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await estate.Transfers.ReceiveAsync(transfer, estate.AtJohannesburg()));

        Assert.Equal(10m, estate.Stock(estate.Johannesburg.Id.ToString(), "p1"));
    }

    // --------------------------------------------------------------------- authority

    [Fact]
    public async Task An_operator_without_transfer_authority_cannot_raise_one()
    {
        // Moving goods between shops is one of the few operations that makes stock vanish from one
        // set of books entirely, so it is a supervisor act rather than something anyone who can
        // edit a price also inherits.
        var estate = new Estate();

        var cashier = new Employee
        {
            Id = EmployeeId.New(),
            StoreId = estate.CapeTown.Id,
            Name = "Thandi",
            PinHash = "unused",
            Permissions = EmployeePermissions.Sell,
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await estate.Transfers.RaiseAsync(
                cashier, estate.CapeTown.Id, estate.Johannesburg.Id, "CT01", "JN01", [Line("p1", 1m)]));

        Assert.Contains("not authorised", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_operator_cannot_send_stock_from_a_store_they_are_not_in()
    {
        // The sending store's shortage has to be attributable to somebody who was there.
        var estate = new Estate();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await estate.Transfers.RaiseAsync(
                estate.Supervisor,
                estate.Johannesburg.Id,
                estate.CapeTown.Id,
                "JN01",
                "CT01",
                [Line("p1", 1m)]));

        Assert.Contains("another store", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Only_the_destination_can_book_a_transfer_in()
    {
        // The sending store confirming its own dispatch would defeat the point of two events:
        // nobody would ever be accountable for the gap.
        var estate = new Estate();
        await SeedStockAsync(estate, "p1", 50m, estate.CapeTown.Id);

        var transfer = await estate.Transfers.RaiseAsync(
            estate.Supervisor, estate.CapeTown.Id, estate.Johannesburg.Id, "CT01", "JN01", [Line("p1", 10m)]);

        await estate.Transfers.DispatchAsync(transfer, estate.Supervisor);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await estate.Transfers.ReceiveAsync(transfer, estate.Supervisor));

        Assert.Contains("cannot receive", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ persistence

    [Fact]
    public async Task The_documents_and_their_movements_are_written_together()
    {
        // A transfer whose status advanced without its movements would be stock that vanished from
        // the ledger while the paperwork said it had moved.
        var estate = new Estate();
        await SeedStockAsync(estate, "p1", 50m, estate.CapeTown.Id);

        var before = await estate.Ledger.GetOutboxSummaryAsync();

        var transfer = await estate.Transfers.RaiseAsync(
            estate.Supervisor, estate.CapeTown.Id, estate.Johannesburg.Id, "CT01", "JN01", [Line("p1", 10m)]);

        await estate.Transfers.DispatchAsync(transfer, estate.Supervisor);

        var after = await estate.Ledger.GetOutboxSummaryAsync();

        // One transfer document and one movement, both queued for the hub.
        Assert.Equal(before.Pending + 2, after.Pending);
        Assert.Single(estate.Ledger.Movements, m => m.Reference == transfer.Id);
    }

    [Fact]
    public async Task A_failed_commit_leaves_both_the_document_and_the_ledger_alone()
    {
        var estate = new Estate();
        await SeedStockAsync(estate, "p1", 50m, estate.CapeTown.Id);

        var transfer = await estate.Transfers.RaiseAsync(
            estate.Supervisor, estate.CapeTown.Id, estate.Johannesburg.Id, "CT01", "JN01", [Line("p1", 10m)]);

        var before = await estate.Ledger.GetOutboxSummaryAsync();

        estate.Ledger.FailNextCommit = new IOException("Disk full.");

        await Assert.ThrowsAsync<IOException>(
            async () => await estate.Transfers.DispatchAsync(transfer, estate.Supervisor));

        var after = await estate.Ledger.GetOutboxSummaryAsync();

        Assert.Equal(before.Total, after.Total);
        Assert.Null(await estate.Ledger.GetTransferAsync(transfer.Id));
        Assert.Equal(50m, estate.Stock(estate.CapeTown.Id.ToString(), "p1"));
    }

    [Fact]
    public async Task Transfers_are_listed_by_the_side_of_the_movement_they_represent()
    {
        // The two ends of a transfer are different stores asking different questions: what have we
        // sent, and what are we expecting.
        var estate = new Estate();
        await SeedStockAsync(estate, "p1", 50m, estate.CapeTown.Id);

        var transfer = await estate.Transfers.RaiseAsync(
            estate.Supervisor, estate.CapeTown.Id, estate.Johannesburg.Id, "CT01", "JN01", [Line("p1", 10m)]);

        await estate.Transfers.DispatchAsync(transfer, estate.Supervisor);

        var outgoing = await estate.Transfers.ListAsync(estate.CapeTown.Id.ToString(), TransferDirection.Outgoing);
        var incoming = await estate.Transfers.ListAsync(estate.Johannesburg.Id.ToString(), TransferDirection.Incoming);

        Assert.Single(outgoing);
        Assert.Single(incoming);
        Assert.Equal(transfer.Id, outgoing[0].Id);
        Assert.Equal(transfer.Id, incoming[0].Id);

        // The sending store is not expecting anything; the destination is not sending anything.
        Assert.Empty(await estate.Transfers.ListAsync(estate.CapeTown.Id.ToString(), TransferDirection.Incoming));
        Assert.Empty(await estate.Transfers.ListAsync(estate.Johannesburg.Id.ToString(), TransferDirection.Outgoing));
    }

    [Fact]
    public async Task A_round_trip_through_storage_preserves_the_shortage()
    {
        // A reprint must still show what was counted, not what was sent. Recomputing on load would
        // erase the very figure the document exists to record.
        var estate = new Estate();
        await SeedStockAsync(estate, "p1", 50m, estate.CapeTown.Id);

        var transfer = await estate.Transfers.RaiseAsync(
            estate.Supervisor, estate.CapeTown.Id, estate.Johannesburg.Id, "CT01", "JN01", [Line("p1", 10m)]);

        await estate.Transfers.DispatchAsync(transfer, estate.Supervisor);

        await estate.Transfers.ReceiveAsync(
            transfer,
            estate.AtJohannesburg(),
            new Dictionary<string, decimal>(StringComparer.Ordinal) { ["p1"] = 7m });

        var stored = await estate.Ledger.GetTransferAsync(transfer.Id);
        Assert.NotNull(stored);

        var rebuilt = stored.ToDomain();

        Assert.Equal(StockTransferStatus.Received, rebuilt.Status);
        Assert.Equal(10m, rebuilt.TotalSent);
        Assert.Equal(7m, rebuilt.TotalCounted);
        Assert.Equal(3m, rebuilt.TotalDiscrepancy);
        Assert.Equal(estate.Supervisor.Id.ToString(), rebuilt.ReceivedByEmployeeId);

        // A rebuilt transfer is not a draft: dispatching it again would take the stock out twice.
        Assert.Throws<InvalidOperationException>(() => rebuilt.Dispatch(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_transfer_still_in_transit_comes_back_still_in_transit()
    {
        // The state that matters most to get right on reload: a transfer that looks like a draft
        // can be dispatched a second time, and the stock leaves twice.
        var estate = new Estate();
        await SeedStockAsync(estate, "p1", 50m, estate.CapeTown.Id);

        var transfer = await estate.Transfers.RaiseAsync(
            estate.Supervisor, estate.CapeTown.Id, estate.Johannesburg.Id, "CT01", "JN01", [Line("p1", 10m)]);

        await estate.Transfers.DispatchAsync(transfer, estate.Supervisor);

        var stored = await estate.Ledger.GetTransferAsync(transfer.Id);
        Assert.NotNull(stored);

        var rebuilt = stored.ToDomain();

        Assert.Equal(StockTransferStatus.Dispatched, rebuilt.Status);
        Assert.True(rebuilt.IsInTransit);
        Assert.NotNull(rebuilt.DispatchedAt);
        Assert.Throws<InvalidOperationException>(() => rebuilt.Dispatch(DateTimeOffset.UtcNow));
    }

    // ---------------------------------------------------------------------------- drafts

    /// <summary>
    /// A saved draft survives, and is still a draft.
    /// </summary>
    /// <remarks>
    /// Raising a transfer only assembles the document; the dispatch is what writes it. Without an
    /// explicit save a draft existed only in the screen's memory, so leaving the page lost it — while
    /// the screen reported it as raised.
    /// </remarks>
    [Fact]
    public async Task ASavedDraft_IsKept()
    {
        var estate = new Estate();
        await SeedStockAsync(estate, "p1", 50m, estate.CapeTown.Id);

        var transfer = await estate.Transfers.RaiseAsync(
            estate.Supervisor, estate.CapeTown.Id, estate.Johannesburg.Id, "CT01", "JN01", [Line("p1", 3m)]);

        var saved = await estate.Transfers.SaveDraftAsync(transfer, estate.Supervisor);

        var found = await estate.Ledger.GetTransferAsync(saved.Id);

        Assert.NotNull(found);
        Assert.Equal(StockTransferStatus.Draft, found.ToDomain().Status);
        Assert.Equal(3m, found.Lines.Single().QuantitySent);
    }

    /// <summary>A saved draft moves no stock.</summary>
    [Fact]
    public async Task ASavedDraft_MovesNoStock()
    {
        var estate = new Estate();
        await SeedStockAsync(estate, "p1", 50m, estate.CapeTown.Id);

        var transfer = await estate.Transfers.RaiseAsync(
            estate.Supervisor, estate.CapeTown.Id, estate.Johannesburg.Id, "CT01", "JN01", [Line("p1", 3m)]);

        await estate.Transfers.SaveDraftAsync(transfer, estate.Supervisor);

        Assert.Equal(50m, estate.Stock(estate.CapeTown.Id.ToString(), "p1"));
        Assert.Equal(0m, estate.Stock(estate.Johannesburg.Id.ToString(), "p1"));
    }

    /// <summary>
    /// A saved draft is not queued for the hub.
    /// </summary>
    /// <remarks>
    /// The property that makes staging safe. A draft that reached the hub would be relayed to the
    /// destination, and somebody would be counting in a van that has not been loaded — the goods are
    /// still on this store's shelf, and this store's books still say so.
    /// </remarks>
    [Fact]
    public async Task ASavedDraft_IsNotQueuedForTheHub()
    {
        var estate = new Estate();
        await SeedStockAsync(estate, "p1", 50m, estate.CapeTown.Id);

        var transfer = await estate.Transfers.RaiseAsync(
            estate.Supervisor, estate.CapeTown.Id, estate.Johannesburg.Id, "CT01", "JN01", [Line("p1", 3m)]);

        // Measured as a change, because seeding the shelf already queued a goods receipt.
        var before = await estate.Ledger.GetOutboxSummaryAsync();

        await estate.Transfers.SaveDraftAsync(transfer, estate.Supervisor);

        var after = await estate.Ledger.GetOutboxSummaryAsync();

        Assert.Equal(before.Total, after.Total);
    }

    /// <summary>
    /// A saved draft can still be sent, and sending it is what queues it.
    /// </summary>
    [Fact]
    public async Task ASavedDraft_CanStillBeSent()
    {
        var estate = new Estate();
        await SeedStockAsync(estate, "p1", 50m, estate.CapeTown.Id);

        var raised = await estate.Transfers.RaiseAsync(
            estate.Supervisor, estate.CapeTown.Id, estate.Johannesburg.Id, "CT01", "JN01", [Line("p1", 8m)]);

        var saved = await estate.Transfers.SaveDraftAsync(raised, estate.Supervisor);

        var before = (await estate.Ledger.GetOutboxSummaryAsync()).Total;

        // Rebuilt from storage, the way the screen re-reads it before the operator sends it.
        var reloaded = (await estate.Ledger.GetTransferAsync(saved.Id))!.ToDomain();

        await estate.Transfers.DispatchAsync(reloaded, estate.Supervisor);

        Assert.Equal(42m, estate.Stock(estate.CapeTown.Id.ToString(), "p1"));

        var after = (await estate.Ledger.GetOutboxSummaryAsync()).Total;

        // The movement and the document, each with its payload.
        Assert.Equal(before + 2, after);
    }

    /// <summary>
    /// A draft that has already gone cannot be saved back over its dispatch.
    /// </summary>
    /// <remarks>
    /// Saving a dispatched transfer as a draft would rewrite the row as "not sent" while the goods
    /// are on a van, and the destination would be left expecting nothing.
    /// </remarks>
    [Fact]
    public async Task ADispatchedTransfer_CannotBeSavedAsADraft()
    {
        var estate = new Estate();
        await SeedStockAsync(estate, "p1", 50m, estate.CapeTown.Id);

        var transfer = await estate.Transfers.RaiseAsync(
            estate.Supervisor, estate.CapeTown.Id, estate.Johannesburg.Id, "CT01", "JN01", [Line("p1", 3m)]);

        await estate.Transfers.DispatchAsync(transfer, estate.Supervisor);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => estate.Transfers.SaveDraftAsync(transfer, estate.Supervisor));
    }

    /// <summary>Saving a draft needs the same authority as sending one.</summary>
    [Fact]
    public async Task SavingADraft_NeedsTransferAuthority()
    {
        var estate = new Estate();

        var transfer = await estate.Transfers.RaiseAsync(
            estate.Supervisor, estate.CapeTown.Id, estate.Johannesburg.Id, "CT01", "JN01", [Line("p1", 3m)]);

        var cashier = new Employee
        {
            Id = EmployeeId.New(),
            StoreId = estate.CapeTown.Id,
            Name = "Thandi",
            PinHash = "not-used-here",
            Permissions = EmployeePermissions.Sell,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => estate.Transfers.SaveDraftAsync(transfer, cashier));
    }
}
