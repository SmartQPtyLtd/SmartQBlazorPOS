// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Globalization;

namespace Pos.Core.Domain;

/// <summary>Where a stock transfer has got to.</summary>
public enum StockTransferStatus
{
    /// <summary>Being prepared. Nothing has moved, and nothing should.</summary>
    Draft = 0,

    /// <summary>
    /// The goods have left the sending store and have not arrived.
    /// </summary>
    /// <remarks>
    /// A real state, not a gap between two others. While a transfer is in transit the goods are in
    /// neither store's sellable stock, which is exactly what stops both shops selling the same
    /// case — and what makes "where did that pallet go" answerable.
    /// </remarks>
    Dispatched = 1,

    /// <summary>The receiving store has booked the goods in.</summary>
    Received = 2,

    /// <summary>Abandoned before dispatch. Nothing moved, so there is nothing to reverse.</summary>
    Cancelled = 3,
}

/// <summary>One product on a stock transfer, and what became of it.</summary>
/// <param name="ProductId">Product moved.</param>
/// <param name="Barcode">Barcode, so the line is identifiable on paper without the catalogue.</param>
/// <param name="Name">Product name as it was when the transfer was raised.</param>
/// <param name="QuantitySent">What left the sending store.</param>
/// <param name="QuantityCounted">
/// What the receiving store counted in, or null while the transfer is still in transit.
/// </param>
public readonly record struct StockTransferLine(
    string ProductId,
    string Barcode,
    string Name,
    decimal QuantitySent,
    decimal? QuantityCounted = null)
{
    /// <summary>True once the receiving store has counted this line.</summary>
    public bool IsCounted => QuantityCounted is not null;

    /// <summary>
    /// Units that left but did not arrive. Positive means stock was lost in transit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The number this document exists to produce. A transfer that recorded only what was sent
    /// would show stock leaving and never show it failing to arrive; one that recorded only what
    /// was counted would lose what the sending store put on the van. Both, and the difference is
    /// how shrinkage between shops is found rather than absorbed.
    /// </para>
    /// <para>
    /// Zero while the transfer is in transit, because nothing has been counted yet — which is not
    /// the same as "nothing was lost", and callers that need to tell the two apart check
    /// <see cref="IsCounted"/>.
    /// </para>
    /// </remarks>
    public decimal Discrepancy => QuantityCounted is { } counted ? QuantitySent - counted : 0m;

    /// <summary>True when less arrived than was sent.</summary>
    public bool IsShort => IsCounted && Discrepancy > 0m;
}

/// <summary>
/// Stock moving from one store to another.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two events, not one.</b> Dispatching moves stock out of the sending store; receiving moves it
/// into the destination. They are separate acts, performed by different people at different tills,
/// possibly days apart and possibly with no network between them. Collapsing them into one would
/// mean either the goods leave before anyone has packed them, or they arrive before anyone has
/// counted them.
/// </para>
/// <para>
/// Movement is recorded at <b>dispatch</b>, not at creation. A transfer being prepared has not moved
/// anything, and taking stock off the shelf because somebody opened a form is how a shop comes to
/// sell something it still has.
/// </para>
/// <para>
/// The lines record what was <em>sent</em>; what was counted in is held alongside them. Keeping
/// them separate is what makes the difference between the two a number rather than an assumption.
/// </para>
/// <para>
/// Nothing here writes to stock. The movements are produced by this type and committed by the
/// service that owns the ledger, so a transfer cannot append a movement the ledger would refuse.
/// </para>
/// </remarks>
public sealed class StockTransfer
{
    private readonly Dictionary<string, decimal> _counted = new(StringComparer.Ordinal);

    /// <summary>Client-generated identity, stable from the moment the form is opened.</summary>
    public required string Id { get; init; }

    /// <summary>Store the goods are leaving.</summary>
    public required StoreId FromStoreId { get; init; }

    /// <summary>Store the goods are going to.</summary>
    public required StoreId ToStoreId { get; init; }

    /// <summary>Human-readable reference, e.g. <c>TR-CT01-JN01-0007</c>.</summary>
    public required string Reference { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Operator who raised it.</summary>
    public required string CreatedByEmployeeId { get; init; }

    /// <summary>What was sent. Never changes after dispatch.</summary>
    public required IReadOnlyList<StockTransferLine> Lines { get; init; }

    public StockTransferStatus Status { get; private set; } = StockTransferStatus.Draft;

    public DateTimeOffset? DispatchedAt { get; private set; }

    public DateTimeOffset? ReceivedAt { get; private set; }

    /// <summary>Operator who booked the goods in at the destination.</summary>
    public string? ReceivedByEmployeeId { get; private set; }

    /// <summary>Free-text annotation: why it was raised, or why it fell short.</summary>
    public string? Note { get; set; }

    /// <summary>True once the goods have left and have not been booked in.</summary>
    public bool IsInTransit => Status == StockTransferStatus.Dispatched;

    /// <summary>Total units dispatched.</summary>
    public decimal TotalSent => Lines.Sum(l => l.QuantitySent);

    /// <summary>
    /// Total units the receiving store counted in. Zero until it has counted.
    /// </summary>
    /// <remarks>
    /// Read from <see cref="ReceivedLines"/> rather than from <see cref="Lines"/>, because the
    /// latter is what was <em>sent</em> and never changes. Reading the sent lines here was a real
    /// bug: the totals reported a full receipt for a transfer that had arrived short.
    /// </remarks>
    public decimal TotalCounted => Status == StockTransferStatus.Received
        ? ReceivedLines.Sum(l => l.QuantityCounted ?? 0m)
        : 0m;

    /// <summary>
    /// Total units that left but did not arrive.
    /// </summary>
    /// <remarks>
    /// Zero before receipt, because nothing has been counted — not because everything arrived.
    /// </remarks>
    public decimal TotalDiscrepancy => Status == StockTransferStatus.Received
        ? ReceivedLines.Sum(l => l.Discrepancy)
        : 0m;

    /// <summary>True when the receiving store counted in less than was sent.</summary>
    public bool HasDiscrepancy => Status == StockTransferStatus.Received && TotalDiscrepancy != 0m;

    /// <summary>The lines as the receiving store found them.</summary>
    public IReadOnlyList<StockTransferLine> ReceivedLines =>
    [
        .. Lines.Select(l => l with
        {
            QuantityCounted = _counted.TryGetValue(l.ProductId, out var counted)
                ? counted
                : l.QuantityCounted,
        }),
    ];

    /// <summary>
    /// Rebuilds a transfer that has already lived, from its stored form.
    /// </summary>
    /// <remarks>
    /// A factory rather than settable properties, because a transfer's state is not something a
    /// caller should be able to assign — it advances by dispatching and receiving and by nothing
    /// else. Rehydrating is the one case where the state arrives rather than being earned, so it
    /// gets an explicit door of its own.
    /// </remarks>
    public static StockTransfer Restore(
        string id,
        StoreId fromStoreId,
        StoreId toStoreId,
        string reference,
        DateTimeOffset createdAt,
        string createdByEmployeeId,
        IReadOnlyList<StockTransferLine> lines,
        StockTransferStatus status,
        DateTimeOffset? dispatchedAt = null,
        DateTimeOffset? receivedAt = null,
        string? receivedByEmployeeId = null,
        string? note = null)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return new StockTransfer
        {
            Id = id,
            FromStoreId = fromStoreId,
            ToStoreId = toStoreId,
            Reference = reference,
            CreatedAt = createdAt,
            CreatedByEmployeeId = createdByEmployeeId,
            Lines = lines,
            Status = status,
            DispatchedAt = dispatchedAt,
            ReceivedAt = receivedAt,
            ReceivedByEmployeeId = receivedByEmployeeId,
            Note = note,
        };
    }

    /// <summary>
    /// Checks the transfer before anything is written.
    /// </summary>
    /// <remarks>
    /// Called when one is raised, so an unusable document never exists in the first place.
    /// </remarks>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new InvalidOperationException("A transfer must have an identity.");
        }

        if (FromStoreId == ToStoreId)
        {
            // Not a transfer. Recording it would append a matching pair of movements that cancel
            // out, while looking in every report like stock genuinely moved between shops.
            throw new InvalidOperationException("A transfer must be between two different stores.");
        }

        if (Lines.Count == 0)
        {
            throw new InvalidOperationException("A transfer must contain at least one line.");
        }

        if (Lines.Any(l => string.IsNullOrWhiteSpace(l.ProductId)))
        {
            throw new InvalidOperationException("Every transferred line must name a product.");
        }

        if (Lines.Any(l => l.QuantitySent <= 0m))
        {
            throw new InvalidOperationException("Every transferred quantity must be greater than zero.");
        }

        if (Lines.Select(l => l.ProductId).Distinct(StringComparer.Ordinal).Count() != Lines.Count)
        {
            // Two lines for one product would produce two movements for the same goods, and the
            // discrepancy would be reported twice — once per line — for a single shortage.
            throw new InvalidOperationException(
                "A product may appear only once on a transfer. Combine the quantities into one line.");
        }
    }

    /// <summary>
    /// One stock movement a transfer implies.
    /// </summary>
    /// <remarks>
    /// Deliberately not the ledger's own movement type. Sequence numbers come from a counter the
    /// terminal persists, and identity is the ledger's business — a transfer knows what moved and
    /// how much, and nothing about how the ledger records it. Keeping the two apart is also what
    /// stops this file depending on infrastructure.
    /// </remarks>
    /// <param name="ProductId">Product moved.</param>
    /// <param name="Quantity">Signed change: negative out of the sending store, positive into the receiving one.</param>
    public readonly record struct StockTransferMovement(string ProductId, decimal Quantity);

    /// <summary>
    /// Marks the goods as having left, and returns the movements that records.
    /// </summary>
    /// <param name="at">
    /// When they left. Recorded because "in transit since when" is the first question asked when a
    /// transfer does not turn up.
    /// </param>
    /// <returns>Negative movements against the sending store, in line order.</returns>
    public IReadOnlyList<StockTransferMovement> Dispatch(DateTimeOffset at)
    {
        if (Status != StockTransferStatus.Draft)
        {
            throw new InvalidOperationException(
                Status == StockTransferStatus.Cancelled
                    ? "That transfer was cancelled, so it cannot be dispatched."
                    : "That transfer has already been dispatched.");
        }

        Status = StockTransferStatus.Dispatched;
        DispatchedAt = at;

        return [.. Lines.Select(l => new StockTransferMovement(l.ProductId, -l.QuantitySent))];
    }

    /// <summary>
    /// Books the goods in at the destination, and returns the movements that records.
    /// </summary>
    /// <param name="receivedByEmployeeId">Operator who counted the goods in.</param>
    /// <param name="at">When they arrived.</param>
    /// <param name="counted">
    /// What was counted, keyed by product id. Lines absent from the map are taken as arrived in
    /// full, which is the ordinary case where nothing was lost.
    /// </param>
    /// <param name="note">Optional note, e.g. explaining a shortage.</param>
    /// <returns>Positive movements into the receiving store, in line order.</returns>
    public IReadOnlyList<StockTransferMovement> Receive(
        string receivedByEmployeeId,
        DateTimeOffset at,
        IReadOnlyDictionary<string, decimal>? counted = null,
        string? note = null)
    {
        if (Status != StockTransferStatus.Dispatched)
        {
            // Receiving twice would append a second set of movements for goods that arrived once,
            // inflating the destination's stock by the whole transfer. The same rule as closing a
            // shift twice: the first record is the one that was signed off.
            throw new InvalidOperationException(
                Status == StockTransferStatus.Draft
                    ? "That transfer has not been dispatched, so there is nothing to receive."
                    : "That transfer has already been received.");
        }

        ValidateCounted(counted);

        _counted.Clear();

        foreach (var line in Lines)
        {
            _counted[line.ProductId] = counted is not null &&
                counted.TryGetValue(line.ProductId, out var quantity)
                    ? quantity
                    : line.QuantitySent;
        }

        Status = StockTransferStatus.Received;
        ReceivedAt = at;
        ReceivedByEmployeeId = receivedByEmployeeId;
        Note = string.IsNullOrWhiteSpace(note) ? null : note;

        // Positive, and the counted quantity rather than the sent one. Booking in what was sent
        // rather than what was counted is how a shortage in transit becomes invisible stock that
        // the destination never finds.
        return [.. Lines.Select(l => new StockTransferMovement(l.ProductId, _counted[l.ProductId]))];
    }

    /// <summary>Abandons a transfer that has not been dispatched.</summary>
    public void Cancel(string? reason = null)
    {
        if (Status == StockTransferStatus.Dispatched)
        {
            // The goods are on a van somewhere. Cancelling would leave the sending store short with
            // nothing to point at, so the only way back is to receive it — possibly short.
            throw new InvalidOperationException(
                "That transfer has been dispatched, so it cannot be cancelled. Receive it instead, " +
                "recording anything that did not arrive.");
        }

        if (Status == StockTransferStatus.Received)
        {
            throw new InvalidOperationException("That transfer has already been received.");
        }

        Status = StockTransferStatus.Cancelled;
        Note = string.IsNullOrWhiteSpace(reason) ? Note : reason;
    }

    private void ValidateCounted(IReadOnlyDictionary<string, decimal>? counted)
    {
        if (counted is null)
        {
            return;
        }

        foreach (var (productId, quantity) in counted)
        {
            var line = Lines.FirstOrDefault(l => string.Equals(l.ProductId, productId, StringComparison.Ordinal));

            if (line == default)
            {
                throw new InvalidOperationException(
                    $"Product '{productId}' is not on this transfer, so it cannot be received against it.");
            }

            if (quantity < 0m)
            {
                throw new InvalidOperationException($"A counted quantity cannot be negative ({line.Name}).");
            }

            if (quantity > line.QuantitySent)
            {
                // More arriving than left is a data error, not a surplus. Accepting it would create
                // stock out of nothing and destroy the meaning of the discrepancy figure.
                throw new InvalidOperationException(
                    $"More {line.Name} was counted in ({quantity}) than was dispatched " +
                    $"({line.QuantitySent}). Check the count, or raise a separate adjustment.");
            }
        }
    }
}
