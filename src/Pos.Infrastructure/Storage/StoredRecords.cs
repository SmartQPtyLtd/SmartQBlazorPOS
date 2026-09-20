// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Globalization;
using Pos.Core.Domain;

namespace Pos.Infrastructure.Storage;

/// <summary>
/// How a stock movement came about.
/// </summary>
/// <remarks>
/// Movements are append-only and their levels are derived by summing them, never by
/// writing a level. A sale, a goods receipt, and a stock count are all movements; the
/// reason code is what makes the ledger auditable.
/// </remarks>
public enum StockMovementReason
{
    /// <summary>Sold at the till. Negative quantity.</summary>
    Sale = 0,

    /// <summary>Goods received from a supplier. Positive quantity.</summary>
    GoodsReceipt = 1,

    /// <summary>Manual correction after a count. Signed.</summary>
    Adjustment = 2,

    /// <summary>Sent to another store. Negative at the sending store.</summary>
    TransferOut = 3,

    /// <summary>Received from another store. Positive at the receiving store.</summary>
    TransferIn = 4,

    /// <summary>Damaged, spoiled, or written off. Negative.</summary>
    Shrinkage = 5,

    /// <summary>Returned by a customer. Positive.</summary>
    CustomerReturn = 6,
}

/// <summary>
/// A persisted sale line.
/// </summary>
/// <remarks>
/// Every monetary value is stored as charged, never as a reference to the catalogue.
/// If a price or tax rate changes next month, a reprint must still show what the
/// customer actually paid.
/// </remarks>
public sealed record StoredSaleLine
{
    public required string ProductId { get; init; }

    public required string Barcode { get; init; }

    public required string Name { get; init; }

    public required decimal Quantity { get; init; }

    public required decimal UnitPrice { get; init; }

    public required string TaxName { get; init; }

    public required decimal TaxRate { get; init; }

    public required decimal DiscountAmount { get; init; }

    public required decimal TaxableAmount { get; init; }

    public required decimal TaxAmount { get; init; }

    /// <summary>
    /// Free-text modifier, e.g. "No onions". Persisted because a kitchen reprint must still
    /// show it.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>
    /// Station that prepared this line, or null when it was handed over at the till.
    /// </summary>
    /// <remarks>
    /// Stored on the line rather than looked up from the catalogue, because a reprint must route
    /// the way the product was configured when it was sold. Reading the current catalogue would
    /// send a reprint of last week's order to wherever the product sits now.
    /// </remarks>
    public string? StationId { get; init; }
}

/// <summary>
/// A persisted payment against a sale.
/// </summary>
/// <remarks>
/// Record-only. No card number, expiry, or track data is ever stored; the type and an
/// optional non-sensitive reference are all that is kept, which is what keeps the system
/// out of PCI DSS scope.
/// </remarks>
public sealed record StoredTender
{
    public required string Type { get; init; }

    public required decimal Amount { get; init; }

    public decimal? Tendered { get; init; }

    public string? Reference { get; init; }
}

/// <summary>
/// A completed sale as persisted on the terminal.
/// </summary>
public sealed record StoredSale
{
    /// <summary>Client-generated UUIDv7, also the dedupe key on the hub.</summary>
    public required string Id { get; init; }

    public required string StoreId { get; init; }

    /// <summary>Human-readable receipt number, e.g. <c>CT01-20260325-0042</c>.</summary>
    public required string Number { get; init; }

    /// <summary>
    /// Monotonic per-terminal sequence.
    /// </summary>
    /// <remarks>
    /// Sync ordering uses <c>(terminalId, terminalSeq)</c> rather than a timestamp or a
    /// store-wide counter: several terminals share a store, and wall clocks drift.
    /// </remarks>
    public required long TerminalSeq { get; init; }

    public required string TerminalId { get; init; }

    public required string CompletedAt { get; init; }

    public required string BusinessDate { get; init; }

    /// <summary>
    /// Hour of the day, 0-23, in the store's local time when the sale was taken.
    /// </summary>
    /// <remarks>
    /// Captured at the point of sale rather than derived when a report runs. Deriving it would
    /// make the same sale land in different hours depending on the timezone of whatever machine
    /// produced the report, so an hourly sales report could not be reproduced or reconciled.
    /// </remarks>
    public int LocalHour { get; init; }

    public required string Currency { get; init; }

    public required string TaxMode { get; init; }

    public required string Status { get; init; }

    public required IReadOnlyList<StoredSaleLine> Lines { get; init; }

    public required IReadOnlyList<StoredTender> Tenders { get; init; }

    public required decimal Subtotal { get; init; }

    public required decimal TotalDiscount { get; init; }

    public required decimal TaxTotal { get; init; }

    public required decimal Total { get; init; }

    public string? EmployeeId { get; init; }

    /// <summary>
    /// Shift the sale was rung during, or null when no shift was open.
    /// </summary>
    /// <remarks>
    /// Recorded on the sale so a cash-up finds activity by shift rather than by a time window,
    /// which would misattribute a sale taken seconds either side of a handover.
    /// </remarks>
    public string? ShiftId { get; init; }

    public string? CustomerId { get; init; }

    public string? VoidReason { get; init; }

    /// <summary>
    /// Catalogue version the sale was priced against.
    /// </summary>
    /// <remarks>
    /// Lets head office compare what was charged against what was effective at the time
    /// and raise a price-exception report, instead of silently repricing a completed
    /// sale — which would unbalance the ledger and the tax return.
    /// </remarks>
    public string? CatalogVersion { get; init; }
}

/// <summary>
/// A persisted refund line.
/// </summary>
/// <remarks>
/// Every figure is stored as refunded, never recomputed from the catalogue. The refund price is
/// what the customer actually paid per unit, so a reprinted slip always matches the money that
/// changed hands.
/// </remarks>
public sealed record StoredReturnLine
{
    public required string ProductId { get; init; }

    public required string Barcode { get; init; }

    public required string Name { get; init; }

    public required decimal Quantity { get; init; }

    /// <summary>Amount refunded per unit.</summary>
    public required decimal UnitRefund { get; init; }

    public required string TaxName { get; init; }

    public required decimal TaxRate { get; init; }

    /// <summary>Tax being reversed on this line, which reduces what is owed onward.</summary>
    public required decimal TaxAmount { get; init; }
}

/// <summary>
/// A persisted refund against a completed sale.
/// </summary>
/// <remarks>
/// Append-only, like a sale. A return is a financial event in its own right rather than an edit
/// to the sale it refers to, so the original sale remains exactly as it was recorded.
/// </remarks>
public sealed record StoredReturn
{
    public required string Id { get; init; }

    public required string StoreId { get; init; }

    /// <summary>The sale the goods came from.</summary>
    public required string OriginalSaleId { get; init; }

    /// <summary>Receipt number of the original sale, printed on the refund slip.</summary>
    public required string OriginalSaleNumber { get; init; }

    /// <summary>Human-readable refund number.</summary>
    public required string Number { get; init; }

    /// <summary>Monotonic per-terminal ordering, shared with sales so the stream stays ordered.</summary>
    public required long TerminalSeq { get; init; }

    public required string TerminalId { get; init; }

    public required string CompletedAt { get; init; }

    public required string BusinessDate { get; init; }

    /// <summary>Hour of the day in the store's local time, recorded at the till.</summary>
    public required int LocalHour { get; init; }

    public required string Currency { get; init; }

    public required IReadOnlyList<StoredReturnLine> Lines { get; init; }

    /// <summary>How the money went back, for reconciliation against the drawer.</summary>
    public required IReadOnlyList<StoredTender> Refunds { get; init; }

    public required string Reason { get; init; }

    public string? Note { get; init; }

    public string? EmployeeId { get; init; }

    /// <summary>
    /// Shift the refund was processed during, or null when no shift was open.
    /// </summary>
    /// <remarks>
    /// Cash refunds leave the drawer, so a cash-up that could not attribute them to a shift would
    /// report a false variance on whatever shift happened to be open.
    /// </remarks>
    public string? ShiftId { get; init; }

    /// <summary>Total handed back to the customer.</summary>
    public required decimal TotalRefund { get; init; }

    /// <summary>Tax reversed by this return.</summary>
    public required decimal TaxReversed { get; init; }
}

/// <summary>
/// A persisted stock transfer.
/// </summary>
/// <remarks>
/// <para>
/// Append-only in effect: a transfer is raised once, dispatched once, and received once, and each
/// of those is a separate commit. Nothing here is edited in place.
/// </para>
/// <para>
/// The receiving quantities are stored on the lines rather than derived, because a receipt that
/// arrived short is the historic fact — recomputing it from the sent quantities would erase the
/// shortage the document exists to record.
/// </para>
/// </remarks>
public sealed record StoredStockTransfer
{
    public required string Id { get; init; }

    public required string FromStoreId { get; init; }

    public required string ToStoreId { get; init; }

    public required string Reference { get; init; }

    public required string Status { get; init; }

    public required string CreatedAt { get; init; }

    public required string CreatedByEmployeeId { get; init; }

    public string? DispatchedAt { get; init; }

    public string? ReceivedAt { get; init; }

    public string? ReceivedByEmployeeId { get; init; }

    public string? Note { get; init; }

    /// <summary>Terminal sequence for the dispatch, which is the change that moves stock.</summary>
    public long DispatchTerminalSeq { get; init; }

    /// <summary>Terminal sequence for the receipt, when there is one.</summary>
    public long ReceiptTerminalSeq { get; init; }

    public required IReadOnlyList<StoredStockTransferLine> Lines { get; init; }

    /// <summary>True while the goods have left and have not been booked in.</summary>
    public bool IsInTransit => string.Equals(Status, "Dispatched", StringComparison.Ordinal);

    /// <summary>
    /// Rebuilds the domain transfer, including where it has got to.
    /// </summary>
    /// <remarks>
    /// The lifecycle state is restored rather than left at the default. An earlier version set only
    /// the identity and the lines, so a received transfer came back as a draft with nothing counted
    /// — which would have made a shortage look like goods that never left, and allowed a dispatch
    /// that had already happened to happen a second time.
    /// </remarks>
    public StockTransfer ToDomain() => StockTransfer.Restore(
        id: Id,
        fromStoreId: Guid.TryParse(FromStoreId, out var from) ? new StoreId(from) : StoreId.New(),
        toStoreId: Guid.TryParse(ToStoreId, out var to) ? new StoreId(to) : StoreId.New(),
        reference: Reference,
        createdAt: ParseTime(CreatedAt) ?? DateTimeOffset.UtcNow,
        createdByEmployeeId: CreatedByEmployeeId,
        lines: [.. Lines.Select(l => l.ToDomain())],
        status: Enum.TryParse<StockTransferStatus>(Status, out var status)
            ? status
            : StockTransferStatus.Draft,
        dispatchedAt: ParseTime(DispatchedAt),
        receivedAt: ParseTime(ReceivedAt),
        receivedByEmployeeId: ReceivedByEmployeeId,
        note: Note);

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;

    /// <summary>Projects a domain transfer into its stored form.</summary>
    /// <param name="transfer">The transfer to store.</param>
    /// <param name="dispatchTerminalSeq">Sequence the dispatch recorded at.</param>
    /// <param name="receiptTerminalSeq">Sequence the receipt recorded at, or zero.</param>
    public static StoredStockTransfer FromDomain(
        StockTransfer transfer,
        long dispatchTerminalSeq,
        long receiptTerminalSeq = 0)
    {
        ArgumentNullException.ThrowIfNull(transfer);

        return new StoredStockTransfer
        {
            Id = transfer.Id,
            FromStoreId = transfer.FromStoreId.ToString(),
            ToStoreId = transfer.ToStoreId.ToString(),
            Reference = transfer.Reference,
            Status = transfer.Status.ToString(),
            CreatedAt = transfer.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            CreatedByEmployeeId = transfer.CreatedByEmployeeId,
            DispatchedAt = transfer.DispatchedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ReceivedAt = transfer.ReceivedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ReceivedByEmployeeId = transfer.ReceivedByEmployeeId,
            Note = transfer.Note,
            DispatchTerminalSeq = dispatchTerminalSeq,
            ReceiptTerminalSeq = receiptTerminalSeq,

            // Read through the received view, so a shortage is persisted rather than recomputed
            // away on the next load.
            Lines = [.. transfer.ReceivedLines.Select(StoredStockTransferLine.FromDomain)],
        };
    }
}

/// <summary>One product on a persisted transfer, and what became of it.</summary>
public sealed record StoredStockTransferLine
{
    public required string ProductId { get; init; }

    public required string Barcode { get; init; }

    public required string Name { get; init; }

    public required decimal QuantitySent { get; init; }

    /// <summary>What the receiving store counted in, or null while in transit.</summary>
    public decimal? QuantityCounted { get; init; }

    /// <summary>Rebuilds the domain line.</summary>
    public StockTransferLine ToDomain() => new(ProductId, Barcode, Name, QuantitySent, QuantityCounted);

    /// <summary>Projects a domain line into its stored form.</summary>
    public static StoredStockTransferLine FromDomain(StockTransferLine line) => new()
    {
        ProductId = line.ProductId,
        Barcode = line.Barcode,
        Name = line.Name,
        QuantitySent = line.QuantitySent,
        QuantityCounted = line.QuantityCounted,
    };
}

/// <summary>Result of committing a transfer event.</summary>
/// <param name="TransferId">The transfer affected.</param>
/// <param name="Queued">How many outbox entries the commit created.</param>
/// <param name="OutboxSeq">The local outbox position after the commit.</param>
public readonly record struct TransferCommit(string TransferId, int Queued, long OutboxSeq);

/// <summary>
/// A persisted stock movement.
/// </summary>
/// <remarks>
/// There is deliberately no stored stock level. The level is <c>SUM(QtyDelta)</c> per
/// store and product. Two terminals selling the last unit both append a decrement and
/// both are counted; with a stored level one of them would be silently lost.
/// </remarks>
public sealed record StockMovement
{
    public required string Id { get; init; }

    public required string StoreId { get; init; }

    public required string TerminalId { get; init; }

    public required long TerminalSeq { get; init; }

    public required string ProductId { get; init; }

    /// <summary>Signed change. Negative reduces stock.</summary>
    public required decimal QtyDelta { get; init; }

    public required string Reason { get; init; }

    /// <summary>The document that caused this movement, e.g. a sale id.</summary>
    public string? Reference { get; init; }

    public required string OccurredAt { get; init; }
}

/// <summary>State of an entry waiting to be pushed to the hub.</summary>
public enum OutboxStatus
{
    /// <summary>Waiting to be sent.</summary>
    Pending = 0,

    /// <summary>Exceeded the retry limit and parked so it cannot block the queue.</summary>
    Dead = 1,
}

/// <summary>
/// One queued change awaiting upload.
/// </summary>
/// <remarks>
/// Written in the same transaction as the record it describes. Deleting an entry happens
/// only after the hub acknowledges it, so a push whose response was lost is retried
/// rather than dropped — and the hub dedupes it, so the retry is harmless.
/// </remarks>
public sealed record OutboxEntry
{
    public required string Id { get; init; }

    /// <summary>Local insertion order. The drain order.</summary>
    public required long EnqueuedSeq { get; init; }

    public required string EntityType { get; init; }

    public required string EntityId { get; init; }

    public required string TerminalId { get; init; }

    public required long TerminalSeq { get; init; }

    public required string EnqueuedAt { get; init; }

    public required OutboxStatus Status { get; init; }

    public required int Attempts { get; init; }

    public string? LastError { get; init; }
}

/// <summary>Counts of queued work, for the sync indicator in the till UI.</summary>
/// <param name="Pending">Entries still to be sent.</param>
/// <param name="Dead">Entries parked after repeated failures.</param>
/// <param name="Total">All entries, including dead ones.</param>
public readonly record struct OutboxSummary(int Pending, int Dead, int Total)
{
    public static OutboxSummary Empty => new(0, 0, 0);

    /// <summary>True when everything has been uploaded.</summary>
    public bool IsClear => Pending == 0 && Dead == 0;
}

/// <summary>Outcome of opening the local database.</summary>
/// <param name="Name">Database name.</param>
/// <param name="Version">Schema version.</param>
/// <param name="Persisted">
/// Whether the browser granted durable storage. If false, the browser may evict local
/// data under storage pressure, which for a POS means losing a day's trading.
/// </param>
public readonly record struct StorageInfo(string Name, int Version, bool Persisted);

/// <summary>Storage usage figures for the device settings screen.</summary>
/// <param name="Usage">Bytes used.</param>
/// <param name="Quota">Bytes available to this origin.</param>
public readonly record struct StorageEstimate(long Usage, long Quota)
{
    /// <summary>Fraction of the quota consumed, 0 to 1.</summary>
    public double Fraction => Quota <= 0 ? 0 : (double)Usage / Quota;
}
