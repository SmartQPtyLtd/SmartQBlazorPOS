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
/// Tests for the stock transfer lifecycle.
/// </summary>
/// <remarks>
/// Moving stock between shops is one of the few operations that makes goods disappear from one set
/// of books and appear in another, which makes it a documented route for shrinkage. The rules here
/// are what stop a transfer from creating stock, destroying it, or counting it twice.
/// </remarks>
public sealed class StockTransferTests
{
    private static readonly StoreId CapeTown = StoreId.New();
    private static readonly StoreId Johannesburg = StoreId.New();
    private static readonly DateTimeOffset Noon = new(2026, 3, 25, 12, 0, 0, TimeSpan.Zero);

    private static StockTransfer NewTransfer(params (string ProductId, decimal Quantity)[] lines) => new()
    {
        Id = Guid.CreateVersion7().ToString("N"),
        FromStoreId = CapeTown,
        ToStoreId = Johannesburg,
        Reference = "TR-CT01-JN01-0001",
        CreatedAt = Noon,
        CreatedByEmployeeId = "emp-1",
        Lines =
        [
            .. lines.Select(l => new StockTransferLine(l.ProductId, "6001000000017", $"Product {l.ProductId}", l.Quantity)),
        ],
    };

    private static StockTransfer Valid() => NewTransfer(("p1", 10m), ("p2", 5m));

    // ------------------------------------------------------------------ validation

    [Fact]
    public void A_valid_transfer_passes_validation()
    {
        var transfer = Valid();

        transfer.Validate();

        Assert.Equal(StockTransferStatus.Draft, transfer.Status);
    }

    [Fact]
    public void A_transfer_to_the_same_store_is_refused()
    {
        // Not a transfer. It would append a matching pair of movements that cancel out, while
        // looking in every report like stock genuinely moved between shops.
        var transfer = new StockTransfer
        {
            Id = Guid.CreateVersion7().ToString("N"),
            FromStoreId = CapeTown,
            ToStoreId = CapeTown,
            Reference = "TR-CT01-CT01-0001",
            CreatedAt = Noon,
            CreatedByEmployeeId = "emp-1",
            Lines = [new StockTransferLine("p1", "6001", "Cola", 5m)],
        };

        var exception = Assert.Throws<InvalidOperationException>(transfer.Validate);

        Assert.Contains("different stores", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_transfer_with_no_lines_is_refused()
    {
        var exception = Assert.Throws<InvalidOperationException>(NewTransfer().Validate);

        Assert.Contains("at least one line", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_transfer_of_nothing_is_refused(decimal quantity)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            NewTransfer(("p1", quantity)).Validate);

        Assert.Contains("greater than zero", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_product_twice_on_one_transfer_is_refused()
    {
        // Two lines for one product would produce two movements for the same goods, and a single
        // shortage would be reported twice — once per line.
        var exception = Assert.Throws<InvalidOperationException>(
            NewTransfer(("p1", 5m), ("p1", 3m)).Validate);

        Assert.Contains("only once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_with_no_product_is_refused()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            NewTransfer(("", 5m)).Validate);

        Assert.Contains("name a product", exception.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------- dispatch

    [Fact]
    public void Dispatching_moves_stock_out_of_the_sending_store()
    {
        var transfer = Valid();

        var movements = transfer.Dispatch(Noon);

        Assert.Equal(2, movements.Count);
        Assert.Equal(-10m, movements[0].Quantity);
        Assert.Equal(-5m, movements[1].Quantity);
        Assert.Equal(StockTransferStatus.Dispatched, transfer.Status);
        Assert.Equal(Noon, transfer.DispatchedAt);
    }

    [Fact]
    public void A_draft_transfer_has_moved_nothing()
    {
        // The whole reason movement happens at dispatch rather than at creation: taking stock off
        // the shelf because somebody opened a form is how a shop sells something it still has.
        var transfer = Valid();

        Assert.Equal(StockTransferStatus.Draft, transfer.Status);
        Assert.Equal(0m, transfer.TotalCounted);
        Assert.False(transfer.IsInTransit);
    }

    [Fact]
    public void A_dispatched_transfer_is_in_transit_until_it_is_received()
    {
        var transfer = Valid();
        transfer.Dispatch(Noon);

        Assert.True(transfer.IsInTransit);
        Assert.Equal(15m, transfer.TotalSent);
        Assert.Equal(0m, transfer.TotalCounted);

        // Nothing has been counted, so a discrepancy of zero is not a claim that all arrived.
        Assert.Equal(0m, transfer.TotalDiscrepancy);
        Assert.False(transfer.HasDiscrepancy);
    }

    [Fact]
    public void Dispatching_twice_is_refused()
    {
        // The second dispatch would take the stock out a second time.
        var transfer = Valid();
        transfer.Dispatch(Noon);

        var exception = Assert.Throws<InvalidOperationException>(() => transfer.Dispatch(Noon.AddHours(1)));

        Assert.Contains("already been dispatched", exception.Message, StringComparison.Ordinal);
        Assert.Equal(Noon, transfer.DispatchedAt);
    }

    [Fact]
    public void A_cancelled_transfer_cannot_be_dispatched()
    {
        var transfer = Valid();
        transfer.Cancel("Ordered by mistake");

        var exception = Assert.Throws<InvalidOperationException>(() => transfer.Dispatch(Noon));

        Assert.Contains("cancelled", exception.Message, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------------- receive

    [Fact]
    public void Receiving_moves_stock_into_the_receiving_store()
    {
        var transfer = Valid();
        transfer.Dispatch(Noon);

        var movements = transfer.Receive("emp-2", Noon.AddDays(1));

        Assert.Equal(2, movements.Count);
        Assert.Equal(10m, movements[0].Quantity);
        Assert.Equal(5m, movements[1].Quantity);
        Assert.Equal(StockTransferStatus.Received, transfer.Status);
        Assert.Equal("emp-2", transfer.ReceivedByEmployeeId);
    }

    [Fact]
    public void A_transfer_cannot_be_received_before_it_is_dispatched()
    {
        // There is nothing to count in — the goods have not left.
        var transfer = Valid();

        var exception = Assert.Throws<InvalidOperationException>(
            () => transfer.Receive("emp-2", Noon));

        Assert.Contains("has not been dispatched", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_transfer_cannot_be_received_twice()
    {
        // The second receipt would append a second set of movements for goods that arrived once,
        // inflating the destination's stock by the whole transfer.
        var transfer = Valid();
        transfer.Dispatch(Noon);
        transfer.Receive("emp-2", Noon.AddDays(1));

        var exception = Assert.Throws<InvalidOperationException>(
            () => transfer.Receive("emp-2", Noon.AddDays(2)));

        Assert.Contains("already been received", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_received_transfer_cannot_be_cancelled()
    {
        var transfer = Valid();
        transfer.Dispatch(Noon);
        transfer.Receive("emp-2", Noon.AddDays(1));

        Assert.Throws<InvalidOperationException>(() => transfer.Cancel("too late"));
    }

    [Fact]
    public void A_dispatched_transfer_cannot_be_cancelled()
    {
        // The goods are on a van somewhere. Cancelling would leave the sending store short with
        // nothing to point at, so the only way back is to receive it — possibly short.
        var transfer = Valid();
        transfer.Dispatch(Noon);

        var exception = Assert.Throws<InvalidOperationException>(() => transfer.Cancel("changed my mind"));

        Assert.Contains("cannot be cancelled", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Receive it", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_draft_transfer_can_be_cancelled()
    {
        var transfer = Valid();

        transfer.Cancel("Ordered by mistake");

        Assert.Equal(StockTransferStatus.Cancelled, transfer.Status);
        Assert.Equal("Ordered by mistake", transfer.Note);
    }

    // ---------------------------------------------------------------- discrepancy

    [Fact]
    public void Receiving_everything_leaves_no_discrepancy()
    {
        var transfer = Valid();
        transfer.Dispatch(Noon);

        transfer.Receive("emp-2", Noon.AddDays(1));

        Assert.Equal(15m, transfer.TotalCounted);
        Assert.Equal(0m, transfer.TotalDiscrepancy);
        Assert.False(transfer.HasDiscrepancy);
    }

    [Fact]
    public void Stock_lost_in_transit_is_reported_as_a_shortage()
    {
        // The number this document exists to produce. A transfer that recorded only what was sent
        // would show stock leaving and never show it failing to arrive.
        var transfer = Valid();
        transfer.Dispatch(Noon);

        transfer.Receive(
            "emp-2",
            Noon.AddDays(1),
            new Dictionary<string, decimal>(StringComparer.Ordinal) { ["p1"] = 8m, ["p2"] = 5m });

        Assert.Equal(2m, transfer.TotalDiscrepancy);
        Assert.True(transfer.HasDiscrepancy);
        Assert.Equal(13m, transfer.TotalCounted);
    }

    [Fact]
    public void A_shortage_is_attributed_to_the_line_it_happened_on()
    {
        var transfer = Valid();
        transfer.Dispatch(Noon);

        transfer.Receive(
            "emp-2",
            Noon.AddDays(1),
            new Dictionary<string, decimal>(StringComparer.Ordinal) { ["p2"] = 3m });

        var lines = transfer.ReceivedLines;

        var first = lines.Single(l => l.ProductId == "p1");
        var second = lines.Single(l => l.ProductId == "p2");

        // p1 was not mentioned in the count, so it arrived in full — the ordinary case.
        Assert.Equal(10m, first.QuantityCounted);
        Assert.False(first.IsShort);

        Assert.Equal(3m, second.QuantityCounted);
        Assert.True(second.IsShort);
        Assert.Equal(2m, second.Discrepancy);
    }

    [Fact]
    public void Receiving_more_than_was_sent_is_refused()
    {
        // A data error, not a surplus. Accepting it would create stock out of nothing and destroy
        // the meaning of the discrepancy figure.
        var transfer = Valid();
        transfer.Dispatch(Noon);

        var exception = Assert.Throws<InvalidOperationException>(() => transfer.Receive(
            "emp-2",
            Noon.AddDays(1),
            new Dictionary<string, decimal>(StringComparer.Ordinal) { ["p1"] = 11m }));

        Assert.Contains("than was dispatched", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_negative_count_is_refused()
    {
        var transfer = Valid();
        transfer.Dispatch(Noon);

        Assert.Throws<InvalidOperationException>(() => transfer.Receive(
            "emp-2",
            Noon.AddDays(1),
            new Dictionary<string, decimal>(StringComparer.Ordinal) { ["p1"] = -1m }));
    }

    [Fact]
    public void Receiving_a_product_that_is_not_on_the_transfer_is_refused()
    {
        var transfer = Valid();
        transfer.Dispatch(Noon);

        var exception = Assert.Throws<InvalidOperationException>(() => transfer.Receive(
            "emp-2",
            Noon.AddDays(1),
            new Dictionary<string, decimal>(StringComparer.Ordinal) { ["stranger"] = 1m }));

        Assert.Contains("not on this transfer", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_receipt_leaves_the_transfer_untouched()
    {
        // A rejected count must not half-apply: the transfer stays in transit and the goods stay
        // countable, so the operator can correct the figure and try again.
        var transfer = Valid();
        transfer.Dispatch(Noon);

        Assert.Throws<InvalidOperationException>(() => transfer.Receive(
            "emp-2",
            Noon.AddDays(1),
            new Dictionary<string, decimal>(StringComparer.Ordinal) { ["p1"] = 11m }));

        Assert.Equal(StockTransferStatus.Dispatched, transfer.Status);
        Assert.True(transfer.IsInTransit);
        Assert.Null(transfer.ReceivedAt);
    }

    [Fact]
    public void A_shortage_can_be_explained_in_a_note()
    {
        var transfer = Valid();
        transfer.Dispatch(Noon);

        transfer.Receive(
            "emp-2",
            Noon.AddDays(1),
            new Dictionary<string, decimal>(StringComparer.Ordinal) { ["p1"] = 8m },
            note: "One case crushed in transit");

        Assert.Equal("One case crushed in transit", transfer.Note);
    }

    // --------------------------------------------------------------------- totals

    [Fact]
    public void The_totals_add_up_across_the_lifecycle()
    {
        var transfer = NewTransfer(("p1", 2.5m), ("p2", 0.75m));

        Assert.Equal(3.25m, transfer.TotalSent);

        transfer.Dispatch(Noon);

        Assert.Equal(3.25m, transfer.TotalSent);

        transfer.Receive(
            "emp-2",
            Noon.AddDays(1),
            new Dictionary<string, decimal>(StringComparer.Ordinal) { ["p2"] = 0.5m });

        Assert.Equal(3.0m, transfer.TotalCounted);
        Assert.Equal(0.25m, transfer.TotalDiscrepancy);
    }

    [Fact]
    public void Weighed_goods_transfer_with_their_fractional_quantities()
    {
        var transfer = NewTransfer(("p1", 0.734m));
        transfer.Dispatch(Noon);

        var movements = transfer.Receive("emp-2", Noon.AddDays(1));

        Assert.Equal(0.734m, movements[0].Quantity);
    }
}
