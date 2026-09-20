// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;

namespace Pos.Infrastructure.Storage;

/// <summary>
/// Durable local storage for the terminal.
/// </summary>
/// <remarks>
/// <para>
/// This is the system of record while the store is trading. Everything is written here
/// first and pushed to the hub afterwards, so a network outage never stops a sale.
/// </para>
/// <para>
/// Abstracted so the domain and UI never see IndexedDB, and so the sync logic can be
/// driven against an in-memory implementation in tests without a browser.
/// </para>
/// </remarks>
public interface ILocalStore
{
    /// <summary>Opens the database, creating it on first run.</summary>
    Task<StorageInfo> InitialiseAsync(CancellationToken ct = default);

    /// <summary>Whether the browser granted durable storage.</summary>
    Task<bool> IsPersistedAsync(CancellationToken ct = default);

    /// <summary>Storage usage, or null when the browser cannot report it.</summary>
    Task<StorageEstimate?> GetStorageEstimateAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes a completed sale together with its stock movements and outbox entries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implementations <b>must</b> perform this as a single atomic operation. A sale that
    /// is stored but not queued would never reach head office; a queue entry without its
    /// sale would be unsendable. Neither is recoverable after the fact.
    /// </para>
    /// <para>
    /// The payloads are serialised by the caller rather than by the store, so there is one
    /// serialisation implementation instead of one per storage backend. They are keyed by
    /// entity id and read back at send time by <see cref="GetSyncPayloadAsync"/>.
    /// </para>
    /// </remarks>
    /// <param name="sale">The sale to persist.</param>
    /// <param name="salePayload">The sale serialised for sync.</param>
    /// <param name="movements">Append-only stock movements caused by the sale.</param>
    /// <param name="movementPayloads">Each movement serialised for sync, positionally matching <paramref name="movements"/>.</param>
    /// <param name="terminalId">Identity of the till, used for the ordering key.</param>
    Task<SaleCommit> CommitSaleAsync(
        StoredSale sale,
        string salePayload,
        IReadOnlyList<StockMovement> movements,
        IReadOnlyList<string> movementPayloads,
        string terminalId,
        CancellationToken ct = default);

    /// <summary>Reads a sale by id, or null when it is not present.</summary>
    Task<StoredSale?> GetSaleAsync(string saleId, CancellationToken ct = default);

    /// <summary>
    /// Finds a sale by the receipt number printed on the customer's slip.
    /// </summary>
    /// <remarks>
    /// This is how a refund starts in practice: the customer presents a receipt and the operator
    /// types the number from it. Requiring the internal id instead would mean a refund could only
    /// be processed by someone with database access.
    /// </remarks>
    Task<StoredSale?> FindSaleByNumberAsync(string saleNumber, CancellationToken ct = default);

    /// <summary>
    /// Sales for a store on a trading day, most recent first.
    /// </summary>
    /// <remarks>
    /// The fallback for a customer who has lost the receipt: the operator can find the sale by
    /// approximate time instead of turning the return away.
    /// </remarks>
    Task<IReadOnlyList<StoredSale>> FindRecentSalesAsync(
        string storeId,
        DateOnly businessDate,
        int limit = 50,
        CancellationToken ct = default);

    /// <summary>Sales for one trading day, in completion order.</summary>
    Task<IReadOnlyList<StoredSale>> GetSalesForDateAsync(
        string storeId,
        DateOnly businessDate,
        CancellationToken ct = default);

    /// <summary>
    /// Records a refund together with the stock movements it returns to the ledger.
    /// </summary>
    /// <remarks>
    /// Atomic, for the same reason a sale is: a refund that is stored but never queued would
    /// never reach head office, and the goods would stay missing from stock with no record of
    /// why. Either the refund exists and is queued, or nothing happened.
    /// </remarks>
    /// <param name="salesReturn">The refund to persist.</param>
    /// <param name="returnPayload">The refund serialised for sync.</param>
    /// <param name="movements">Positive stock movements returning goods to the ledger.</param>
    /// <param name="movementPayloads">Each movement serialised, positionally matching.</param>
    /// <param name="terminalId">Identity of the till, for the ordering key.</param>
    Task<ReturnCommit> CommitReturnAsync(
        StoredReturn salesReturn,
        string returnPayload,
        IReadOnlyList<StockMovement> movements,
        IReadOnlyList<string> movementPayloads,
        string terminalId,
        CancellationToken ct = default);

    /// <summary>Reads a refund by id, or null when it is not present.</summary>
    Task<StoredReturn?> GetReturnAsync(string returnId, CancellationToken ct = default);

    /// <summary>
    /// Every refund recorded against a sale.
    /// </summary>
    /// <remarks>
    /// Needed before accepting a new refund, to work out what remains refundable. Without it a
    /// customer could return the same goods repeatedly and be paid each time.
    /// </remarks>
    Task<IReadOnlyList<StoredReturn>> GetReturnsForSaleAsync(
        string originalSaleId,
        CancellationToken ct = default);

    /// <summary>Refunds recorded for one trading day.</summary>
    Task<IReadOnlyList<StoredReturn>> GetReturnsForDateAsync(
        string storeId,
        DateOnly businessDate,
        CancellationToken ct = default);

    /// <summary>
    /// Allocates the next refund number for a store and business date.
    /// </summary>
    /// <remarks>
    /// A separate counter from sales, so a refund number can never be mistaken for a receipt
    /// number on a document that is explicitly not a receipt.
    /// </remarks>
    Task<long> NextReturnSequenceAsync(string storeId, DateOnly businessDate, CancellationToken ct = default);

    /// <summary>
    /// Finds a product by barcode, scoped to one store.
    /// </summary>
    /// <remarks>
    /// The hot path at the till. The store scope is not a refinement: the local catalogue holds one
    /// row per store per product, and a chain sells the same barcode in every branch, so an unscoped
    /// lookup can resolve a scan to another shop's row and charge that shop's price and tax.
    /// </remarks>
    /// <param name="storeId">Store whose catalogue to look in.</param>
    /// <param name="barcode">The scanned code.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<StoredProduct?> FindProductByBarcodeAsync(
        string storeId,
        string barcode,
        CancellationToken ct = default);

    /// <summary>
    /// Finds products a store may sell whose name contains <paramref name="term"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fallback for when a label will not scan, which happens routinely in a real shop
    /// and would otherwise block a sale. Results are capped because this feeds a type-ahead
    /// list, not a report.
    /// </para>
    /// <para>
    /// Scoped to a store, and it has to be. A terminal holds the whole estate's catalogue after a
    /// sync, so a search that ignored the store would offer one shop's products at another's till —
    /// with a basket about to be charged for them. Products head office has deleted are excluded for
    /// the same reason <see cref="GetProductsAsync"/> excludes them: a deletion is authoritative, and
    /// a search that returned one would resurrect it.
    /// </para>
    /// </remarks>
    /// <param name="storeId">Store whose catalogue to search.</param>
    /// <param name="term">What the operator typed.</param>
    /// <param name="limit">Caps the type-ahead list.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<StoredProduct>> SearchProductsByNameAsync(
        string storeId,
        string term,
        int limit = 8,
        CancellationToken ct = default);

    /// <summary>
    /// Lists a store's catalogue for management.
    /// </summary>
    /// <param name="storeId">Store whose catalogue to list.</param>
    /// <param name="includeInactive">
    /// True to include products withdrawn from sale. Needed by the management screen, which must be
    /// able to bring one back; the till never does.
    /// </param>
    /// <param name="limit">Maximum rows, to bound what is loaded into the browser.</param>
    Task<IReadOnlyList<StoredProduct>> GetProductsAsync(
        string storeId,
        bool includeInactive = false,
        int limit = 500,
        CancellationToken ct = default);

    /// <summary>Reads one product by id, or null when it is not on this terminal.</summary>
    Task<StoredProduct?> GetProductAsync(string productId, CancellationToken ct = default);

    // ------------------------------------------------------------------------ stock

    /// <summary>
    /// Stock movements recorded against a product, most recent first.
    /// </summary>
    /// <remarks>
    /// The ledger behind a derived stock level. Exposed so an operator can see <em>why</em> a level
    /// is what it is, which is the difference between a number that can be trusted and one that
    /// cannot.
    /// </remarks>
    Task<IReadOnlyList<StockMovement>> GetStockMovementsAsync(
        string storeId,
        string? productId = null,
        int limit = 200,
        CancellationToken ct = default);

    /// <summary>
    /// Records a manual stock movement — a count correction, goods receipt, or write-off.
    /// </summary>
    /// <remarks>
    /// Written to the same append-only ledger as sale and refund movements, never as an edit to a
    /// stored level. A correction is itself an event, so the history still explains the current
    /// figure.
    /// </remarks>
    /// <param name="movement">The movement to append.</param>
    /// <param name="movementPayload">The movement serialised for sync.</param>
    /// <param name="terminalId">Identity of the till, for the ordering key.</param>
    Task<StockCommit> RecordStockMovementAsync(
        StockMovement movement,
        string movementPayload,
        string terminalId,
        CancellationToken ct = default);

    /// <summary>Inserts or replaces products, used when pulling a catalogue.</summary>
    Task UpsertProductsAsync(IReadOnlyList<StoredProduct> products, CancellationToken ct = default);

    /// <summary>The oldest pending outbox entries, in drain order.</summary>
    Task<IReadOnlyList<OutboxEntry>> PeekOutboxAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Reads the serialised payload a queued entry refers to.
    /// </summary>
    /// <remarks>
    /// The outbox holds a reference rather than a copy, so the bytes are produced at send
    /// time from the record itself. That keeps one serialisation implementation, in C#,
    /// rather than duplicating the record shapes into the browser shim.
    /// </remarks>
    /// <returns>The JSON payload, or null when the record is gone.</returns>
    Task<string?> GetSyncPayloadAsync(string entityId, CancellationToken ct = default);

    /// <summary>Removes entries the hub has acknowledged.</summary>
    Task AcknowledgeOutboxAsync(IReadOnlyList<string> ids, CancellationToken ct = default);

    // ------------------------------------------------------------- stock transfers

    /// <summary>
    /// Writes a transfer event together with the stock movements it caused, atomically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One commit covers both the dispatch and the receipt, because the two have the same shape: a
    /// transfer whose status advanced without its movements would be stock that vanished from the
    /// ledger while the paperwork said it had moved, and the two could never be reconciled
    /// afterwards.
    /// </para>
    /// <para>
    /// A dispatch carries negative movements out of the sending store; a receipt carries positive
    /// movements into the receiving one. Both are ordinary ledger entries with a document behind
    /// them.
    /// </para>
    /// </remarks>
    /// <param name="transfer">The transfer in its new state.</param>
    /// <param name="transferPayload">The transfer serialised for sync.</param>
    /// <param name="movements">Signed movements the event caused.</param>
    /// <param name="movementPayloads">Each movement serialised, positionally matching.</param>
    /// <param name="terminalId">Identity of the till, for the ordering key.</param>
    Task<TransferCommit> CommitTransferAsync(
        StoredStockTransfer transfer,
        string transferPayload,
        IReadOnlyList<StockMovement> movements,
        IReadOnlyList<string> movementPayloads,
        string terminalId,
        CancellationToken ct = default);

    /// <summary>Reads a transfer by id, or null when it is not on this terminal.</summary>
    Task<StoredStockTransfer?> GetTransferAsync(string transferId, CancellationToken ct = default);

    /// <summary>
    /// Stores a transfer that arrived from the hub, without queueing it for upload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one write that does not enqueue an outbox entry, and deliberately: this document was
    /// authored by another store and relayed by the hub. Queueing it would push it back as though
    /// this store had raised it, and the estate would accumulate copies of one transfer.
    /// </para>
    /// <para>
    /// No stock moves here either. The sending store's movements belong to the sending store; this
    /// one's stock changes when it actually books the goods in.
    /// </para>
    /// </remarks>
    Task SaveTransferAsync(StoredStockTransfer transfer, CancellationToken ct = default);

    /// <summary>
    /// Transfers this store has sent, or is expecting to receive.
    /// </summary>
    /// <param name="storeId">Store to list for.</param>
    /// <param name="direction">Which side of the transfer to list.</param>
    /// <param name="limit">Maximum rows, most recent first.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<StoredStockTransfer>> GetTransfersAsync(
        string storeId,
        TransferDirection direction = TransferDirection.Outgoing,
        int limit = 100,
        CancellationToken ct = default);

    /// <summary>Records a failed push so the entry can be retried or parked.</summary>
    Task MarkOutboxFailedAsync(string id, string failureDetail, CancellationToken ct = default);

    /// <summary>Counts of queued work, for the sync indicator.</summary>
    Task<OutboxSummary> GetOutboxSummaryAsync(CancellationToken ct = default);

    /// <summary>Reads the cursor for a sync stream, or null when it has never synced.</summary>
    Task<string?> GetCursorAsync(string stream, CancellationToken ct = default);

    /// <summary>Persists the cursor for a sync stream.</summary>
    Task SetCursorAsync(string stream, string cursor, CancellationToken ct = default);

    /// <summary>
    /// Allocates the next sale sequence for a store and business date.
    /// </summary>
    /// <remarks>
    /// Persisted, so a terminal that reloads mid-shift resumes the sequence rather than
    /// restarting at 1 and reissuing a number already printed on a customer's receipt.
    /// </remarks>
    Task<long> NextSaleSequenceAsync(string storeId, DateOnly businessDate, CancellationToken ct = default);

    /// <summary>
    /// Reserves a contiguous block of terminal sequence numbers, returning the first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is exactly <b>one</b> of these counters per terminal, and it is persisted. Every
    /// record the till authors — sales, refunds, stock movements, shifts, drawer events — draws
    /// its <c>terminalSeq</c> from it, because they all share a single ordered stream.
    /// </para>
    /// <para>
    /// Both properties are load-bearing. Two independent in-process counters would both start at
    /// one, handing a sale and a drawer event the same position in the stream; and an in-memory
    /// counter restarts at one after a browser refresh, so the first record pushed after a reload
    /// collides on the hub with one already stored. The hub enforces uniqueness on
    /// <c>(terminalId, terminalSeq)</c>, so that collision would leave a legitimate sale stranded
    /// in the outbox.
    /// </para>
    /// <para>
    /// Reserving a block lets a sale and the stock movements it caused take consecutive positions,
    /// which keeps the stream gap-free. A gap is how the hub detects loss in transit, so it must
    /// not be produced casually.
    /// </para>
    /// </remarks>
    /// <param name="count">How many consecutive numbers are needed. Must be at least one.</param>
    /// <returns>The first number in the reserved block.</returns>
    Task<long> ReserveTerminalSequenceAsync(int count = 1, CancellationToken ct = default);

    /// <summary>Erases all local data. Used when a terminal is revoked.</summary>
    Task WipeAsync(CancellationToken ct = default);
}

/// <summary>Result of committing a sale atomically.</summary>
/// <param name="SaleId">The sale that was written.</param>
/// <param name="Queued">How many outbox entries the commit created.</param>
/// <param name="OutboxSeq">The local outbox position after the commit.</param>
public readonly record struct SaleCommit(string SaleId, int Queued, long OutboxSeq);

/// <summary>Result of committing a refund atomically.</summary>
/// <param name="ReturnId">The refund that was written.</param>
/// <param name="Queued">How many outbox entries the commit created.</param>
/// <param name="OutboxSeq">The local outbox position after the commit.</param>
public readonly record struct ReturnCommit(string ReturnId, int Queued, long OutboxSeq);

/// <summary>Result of appending a stock movement.</summary>
/// <param name="MovementId">The movement that was written.</param>
/// <param name="OutboxSeq">The local outbox position after the commit.</param>
public readonly record struct StockCommit(string MovementId, long OutboxSeq);

/// <summary>Which side of a stock transfer to list.</summary>
public enum TransferDirection
{
    /// <summary>Transfers this store dispatched.</summary>
    Outgoing = 0,

    /// <summary>Transfers this store is expecting, or has received.</summary>
    Incoming = 1,
}

/// <summary>
/// A stock level with the movement count behind it.
/// </summary>
/// <param name="ProductId">Product the level belongs to.</param>
/// <param name="Quantity">Derived level: the sum of every movement's delta.</param>
/// <param name="MovementCount">How many movements produced it.</param>
/// <param name="LastMovementAt">When the most recent movement was recorded, if any.</param>
/// <remarks>
/// The movement count is carried alongside the level deliberately. A level derived from two
/// movements and one derived from two hundred look identical as a number, but they mean very
/// different things when someone is deciding whether to trust it.
/// </remarks>
public readonly record struct StockLevel(
    string ProductId,
    decimal Quantity,
    int MovementCount,
    DateTimeOffset? LastMovementAt)
{
    /// <summary>True when no movement has ever been recorded for this product.</summary>
    public bool HasNoHistory => MovementCount == 0;

    /// <summary>True when the derived level is at or below zero.</summary>
    public bool IsOutOfStock => Quantity <= 0m;
}

/// <summary>
/// A catalogue product as stored locally.
/// </summary>
/// <remarks>
/// Held as a read-only replica of head-office data. A store may override a price, but
/// that is a separate store-owned record rather than a mutation of this row, so a
/// catalogue pull can never silently discard a local decision.
/// </remarks>
public sealed record StoredProduct
{
    public required string Id { get; init; }

    public required string StoreId { get; init; }

    public required string Barcode { get; init; }

    public required string Name { get; init; }

    public required decimal UnitPrice { get; init; }

    public required string TaxName { get; init; }

    public required decimal TaxRate { get; init; }

    public string? Sku { get; init; }

    /// <summary>Grouping for reporting and filtering, e.g. "Beverages". Free text, head-office data.</summary>
    public string? Category { get; init; }

    /// <summary>
    /// Whether this shop counts this item's stock.
    /// </summary>
    /// <remarks>
    /// Defaults to true so that a catalogue row written before this field existed — or by a hub that
    /// does not send it — is counted rather than silently dropped from every stock report.
    /// </remarks>
    public bool TracksStock { get; init; } = true;

    /// <summary>Quantity at which to reorder, or null when no reorder point has been set.</summary>
    public decimal? ReorderLevel { get; init; }

    public bool IsActive { get; init; } = true;

    /// <summary>
    /// True when head office has withdrawn the product.
    /// </summary>
    /// <remarks>
    /// A separate flag from <see cref="IsActive"/> because the two mean different things and must
    /// not be conflated. A shop may deactivate a product locally — out of season, temporarily
    /// unavailable — and that decision has to survive the next catalogue pull. A deletion from head
    /// office is authoritative and must override it. Without the distinction, a deleted product
    /// would reappear on the till the next time the catalogue synced.
    /// </remarks>
    public bool IsDeleted { get; init; }

    public bool IsOpenPrice { get; init; }

    public bool IsSoldByWeight { get; init; }

    /// <summary>
    /// Station that prepares this product, or null when it is handed over at the till.
    /// </summary>
    /// <remarks>
    /// Head-office data, like the price: it comes from the catalogue rather than being set per
    /// till, so every terminal in a store routes the same product to the same place.
    /// </remarks>
    public string? StationId { get; init; }

    /// <summary>
    /// Rebuilds the catalogue product as a domain product.
    /// </summary>
    /// <param name="currency">Store currency, which the stored row does not carry.</param>
    /// <remarks>
    /// <para>
    /// A mapper rather than a hand-written projection at each call site, because a projection is
    /// easy to get almost right. The till had one that copied the price, name, tax, and barcode and
    /// silently skipped the station — so a scanned burger never produced a kitchen ticket, and
    /// nothing failed. The kitchen simply never heard about the order.
    /// </para>
    /// <para>
    /// One mapper means a field added here is carried everywhere, and a test on this method covers
    /// every caller.
    /// </para>
    /// </remarks>
    public Product ToDomain(string currency) => new()
    {
        Id = Guid.TryParse(Id, out var id) ? new ProductId(id) : ProductId.New(),

        // Aliased because the property below is also called StoreId, which shadows the type
        // inside this body.
        StoreId = Guid.TryParse(StoreId, out var parsedStoreId)
            ? new Pos.Core.Domain.StoreId(parsedStoreId)
            : Pos.Core.Domain.StoreId.New(),
        Barcode = Barcode,
        Name = Name,
        Sku = Sku,
        Category = Category,
        UnitPrice = new Money(UnitPrice, currency),
        TaxRate = new TaxRate(TaxName, TaxRate),
        IsOpenPrice = IsOpenPrice,
        IsSoldByWeight = IsSoldByWeight,
        TracksStock = TracksStock,
        ReorderLevel = ReorderLevel,
        IsActive = IsActive,
        StationId = StationId,
    };
}

/// <summary>
/// Allocates sale numbers from durable local storage.
/// </summary>
/// <remarks>
/// Numbers are assigned locally so a sale is complete and printable while offline. The
/// hub therefore cannot be the authority for them, which is exactly why sync identity
/// uses the client-generated <see cref="SaleId"/> instead of the receipt number.
/// </remarks>
public sealed class LocalSaleNumberSource(ILocalStore store) : ISaleNumberSource
{
    private readonly ILocalStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<SaleNumber> NextAsync(
        StoreId storeId,
        string storeCode,
        DateOnly businessDate,
        CancellationToken ct = default)
    {
        var sequence = await _store
            .NextSaleSequenceAsync(storeId.ToString(), businessDate, ct)
            .ConfigureAwait(false);

        return new SaleNumber(storeCode, businessDate, (int)sequence);
    }
}
