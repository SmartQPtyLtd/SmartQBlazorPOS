// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Text.Json;
using Microsoft.JSInterop;

namespace Pos.Infrastructure.Storage;

/// <summary>
/// <see cref="ILocalStore"/> backed by IndexedDB through <c>local-store.js</c>.
/// </summary>
/// <remarks>
/// <para>
/// The browser implementation, and therefore the one that runs at a real till. It mirrors
/// <see cref="InMemoryLocalStore"/> exactly, including the atomicity guarantee: the sale,
/// its stock movements, its payloads and its outbox entries are written by a single
/// IndexedDB transaction, so an interrupted commit leaves nothing behind.
/// </para>
/// <para>
/// Marshalling goes through small DTOs with camel-case names because the JavaScript side
/// reads fixed property names. Serialising the C# records directly would tie the browser
/// shim to every future rename of a domain property.
/// </para>
/// </remarks>
public sealed class JsLocalStore(IJSRuntime js) : ILocalStore
{
    private const string ModulePath = "./js/local-store.js";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IJSRuntime _js = js ?? throw new ArgumentNullException(nameof(js));
    private IJSObjectReference? _module;

    private async ValueTask<IJSObjectReference> ModuleAsync(CancellationToken ct) =>
        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", ct, ModulePath).ConfigureAwait(false);

    public async Task<StorageInfo> InitialiseAsync(CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        var info = await module.InvokeAsync<StorageInfoDto>("init", ct).ConfigureAwait(false);

        return new StorageInfo(info.Name, info.Version, info.Persisted);
    }

    public async Task<bool> IsPersistedAsync(CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        return await module.InvokeAsync<bool>("isPersisted", ct).ConfigureAwait(false);
    }

    public async Task<StorageEstimate?> GetStorageEstimateAsync(CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        var estimate = await module
            .InvokeAsync<StorageEstimateDto?>("storageEstimate", ct)
            .ConfigureAwait(false);

        return estimate is null ? null : new StorageEstimate(estimate.Usage, estimate.Quota);
    }

    public async Task<SaleCommit> CommitSaleAsync(
        StoredSale sale,
        string salePayload,
        IReadOnlyList<StockMovement> movements,
        IReadOnlyList<string> movementPayloads,
        string terminalId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(salePayload);
        ArgumentNullException.ThrowIfNull(movements);
        ArgumentNullException.ThrowIfNull(movementPayloads);

        if (movements.Count != movementPayloads.Count)
        {
            throw new ArgumentException(
                "Each movement must have exactly one payload.", nameof(movementPayloads));
        }

        var module = await ModuleAsync(ct).ConfigureAwait(false);

        // Records are serialised here rather than in JavaScript so the shim stays a
        // storage layer and never needs to know a sale's shape.
        var result = await module.InvokeAsync<SaleCommitDto>(
            "commitSale",
            ct,
            JsonSerializer.SerializeToElement(sale, Json),
            salePayload,
            JsonSerializer.SerializeToElement(movements, Json),
            movementPayloads,
            terminalId).ConfigureAwait(false);

        return new SaleCommit(result.SaleId, result.Queued, result.OutboxSeq);
    }

    public async Task<StoredSale?> GetSaleAsync(string saleId, CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        var row = await module
            .InvokeAsync<JsonElement?>("get", ct, "sales", saleId)
            .ConfigureAwait(false);

        return Deserialize<StoredSale>(row);
    }

    public async Task<StoredSale?> FindSaleByNumberAsync(string saleNumber, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(saleNumber))
        {
            return null;
        }

        var module = await ModuleAsync(ct).ConfigureAwait(false);
        var row = await module
            .InvokeAsync<JsonElement?>("findSaleByNumber", ct, saleNumber.Trim())
            .ConfigureAwait(false);

        return Deserialize<StoredSale>(row);
    }

    public async Task<IReadOnlyList<StoredSale>> FindRecentSalesAsync(
        string storeId,
        DateOnly businessDate,
        int limit = 50,
        CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);

        var rows = await module
            .InvokeAsync<JsonElement[]>(
                "findRecentSales",
                ct,
                storeId,
                SaleMapper.FormatBusinessDate(businessDate),
                limit)
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.Deserialize<StoredSale>(Json)!).Where(s => s is not null)];
    }

    // ------------------------------------------------------------------------ refunds

    public async Task<ReturnCommit> CommitReturnAsync(
        StoredReturn salesReturn,
        string returnPayload,
        IReadOnlyList<StockMovement> movements,
        IReadOnlyList<string> movementPayloads,
        string terminalId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(salesReturn);
        ArgumentNullException.ThrowIfNull(returnPayload);
        ArgumentNullException.ThrowIfNull(movements);
        ArgumentNullException.ThrowIfNull(movementPayloads);

        if (movements.Count != movementPayloads.Count)
        {
            throw new ArgumentException(
                "Each movement must have exactly one payload.", nameof(movementPayloads));
        }

        var module = await ModuleAsync(ct).ConfigureAwait(false);

        var result = await module.InvokeAsync<ReturnCommitDto>(
            "commitReturn",
            ct,
            JsonSerializer.SerializeToElement(salesReturn, Json),
            returnPayload,
            JsonSerializer.SerializeToElement(movements, Json),
            movementPayloads,
            terminalId).ConfigureAwait(false);

        return new ReturnCommit(result.ReturnId, result.Queued, result.OutboxSeq);
    }

    public async Task<StoredReturn?> GetReturnAsync(string returnId, CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        var row = await module
            .InvokeAsync<JsonElement?>("get", ct, "returns", returnId)
            .ConfigureAwait(false);

        return Deserialize<StoredReturn>(row);
    }

    public async Task<IReadOnlyList<StoredReturn>> GetReturnsForSaleAsync(
        string originalSaleId,
        CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);

        var rows = await module
            .InvokeAsync<JsonElement[]>("getReturnsForSale", ct, originalSaleId)
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.Deserialize<StoredReturn>(Json)!).Where(r => r is not null)];
    }

    public async Task<IReadOnlyList<StoredReturn>> GetReturnsForDateAsync(
        string storeId,
        DateOnly businessDate,
        CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);

        var rows = await module
            .InvokeAsync<JsonElement[]>(
                "getReturnsForDate",
                ct,
                storeId,
                SaleMapper.FormatBusinessDate(businessDate))
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.Deserialize<StoredReturn>(Json)!).Where(r => r is not null)];
    }

    public async Task<long> NextReturnSequenceAsync(
        string storeId,
        DateOnly businessDate,
        CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);

        return await module
            .InvokeAsync<long>("nextReturnSequence", ct, storeId, SaleMapper.FormatBusinessDate(businessDate))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredSale>> GetSalesForDateAsync(
        string storeId,
        DateOnly businessDate,
        CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);

        var rows = await module
            .InvokeAsync<JsonElement[]>(
                "getSalesForDate",
                ct,
                storeId,
                SaleMapper.FormatBusinessDate(businessDate))
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.Deserialize<StoredSale>(Json)!).Where(s => s is not null)];
    }

    public async Task<StoredProduct?> FindProductByBarcodeAsync(
        string storeId,
        string barcode,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeId);

        var module = await ModuleAsync(ct).ConfigureAwait(false);
        var row = await module
            .InvokeAsync<JsonElement?>("findProductByBarcode", ct, storeId, barcode)
            .ConfigureAwait(false);

        return Deserialize<StoredProduct>(row);
    }

    public async Task<IReadOnlyList<StoredProduct>> SearchProductsByNameAsync(
        string storeId,
        string term,
        int limit = 8,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeId);

        if (string.IsNullOrWhiteSpace(term))
        {
            return [];
        }

        var module = await ModuleAsync(ct).ConfigureAwait(false);

        var rows = await module
            .InvokeAsync<JsonElement[]>("searchProductsByName", ct, storeId, term, limit)
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.Deserialize<StoredProduct>(Json)!).Where(p => p is not null)];
    }

    public async Task UpsertProductsAsync(IReadOnlyList<StoredProduct> products, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(products);

        if (products.Count == 0)
        {
            return;
        }

        var module = await ModuleAsync(ct).ConfigureAwait(false);
        await module.InvokeVoidAsync(
            "putMany",
            ct,
            "products",
            JsonSerializer.SerializeToElement(products, Json)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredProduct>> GetProductsAsync(
        string storeId,
        bool includeInactive = false,
        int limit = 500,
        CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);

        var rows = await module
            .InvokeAsync<JsonElement[]>("getProducts", ct, storeId, includeInactive, limit)
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.Deserialize<StoredProduct>(Json)!).Where(p => p is not null)];
    }

    public async Task<StoredProduct?> GetProductAsync(string productId, CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);

        var row = await module
            .InvokeAsync<JsonElement?>("get", ct, "products", productId)
            .ConfigureAwait(false);

        return Deserialize<StoredProduct>(row);
    }

    // -------------------------------------------------------------------------- stock

    public async Task<IReadOnlyList<StockMovement>> GetStockMovementsAsync(
        string storeId,
        string? productId = null,
        int limit = 200,
        CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);

        var rows = await module
            .InvokeAsync<JsonElement[]>("getStockMovements", ct, storeId, productId, limit)
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.Deserialize<StockMovement>(Json)!).Where(m => m is not null)];
    }

    public async Task<StockCommit> RecordStockMovementAsync(
        StockMovement movement,
        string movementPayload,
        string terminalId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(movement);
        ArgumentNullException.ThrowIfNull(movementPayload);

        var module = await ModuleAsync(ct).ConfigureAwait(false);

        var result = await module.InvokeAsync<StockCommitDto>(
            "recordStockMovement",
            ct,
            JsonSerializer.SerializeToElement(movement, Json),
            movementPayload,
            terminalId).ConfigureAwait(false);

        return new StockCommit(result.MovementId, result.OutboxSeq);
    }

    public async Task<IReadOnlyList<OutboxEntry>> PeekOutboxAsync(int limit = 50, CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        var rows = await module.InvokeAsync<OutboxEntryDto[]>("peekOutbox", ct, limit).ConfigureAwait(false);

        return
        [
            .. rows.Select(r => new OutboxEntry
            {
                Id = r.Id,
                EnqueuedSeq = r.EnqueuedSeq,
                EntityType = r.EntityType,
                EntityId = r.EntityId,
                TerminalId = r.TerminalId,
                TerminalSeq = r.TerminalSeq,
                EnqueuedAt = r.EnqueuedAt,
                Status = ParseStatus(r.Status),
                Attempts = r.Attempts,
                LastError = r.LastError,
            }),
        ];
    }

    public async Task<string?> GetSyncPayloadAsync(string entityId, CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        return await module.InvokeAsync<string?>("getSyncPayload", ct, entityId).ConfigureAwait(false);
    }

    public async Task AcknowledgeOutboxAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return;
        }

        var module = await ModuleAsync(ct).ConfigureAwait(false);
        await module.InvokeVoidAsync("acknowledgeOutbox", ct, ids).ConfigureAwait(false);
    }

    public async Task MarkOutboxFailedAsync(string id, string failureDetail, CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        await module.InvokeVoidAsync("markOutboxFailed", ct, id, failureDetail).ConfigureAwait(false);
    }

    public async Task<OutboxSummary> GetOutboxSummaryAsync(CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        var summary = await module.InvokeAsync<OutboxSummaryDto>("outboxSummary", ct).ConfigureAwait(false);

        return new OutboxSummary(summary.Pending, summary.Dead, summary.Total);
    }

    public async Task<string?> GetCursorAsync(string stream, CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        return await module.InvokeAsync<string?>("getCursor", ct, stream).ConfigureAwait(false);
    }

    public async Task SetCursorAsync(string stream, string cursor, CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        await module.InvokeVoidAsync("setCursor", ct, stream, cursor).ConfigureAwait(false);
    }

    public async Task<long> NextSaleSequenceAsync(
        string storeId,
        DateOnly businessDate,
        CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);

        return await module
            .InvokeAsync<long>("nextSaleSequence", ct, storeId, SaleMapper.FormatBusinessDate(businessDate))
            .ConfigureAwait(false);
    }

    // ------------------------------------------------------------- stock transfers

    public async Task<TransferCommit> CommitTransferAsync(
        StoredStockTransfer transfer,
        string transferPayload,
        IReadOnlyList<StockMovement> movements,
        IReadOnlyList<string> movementPayloads,
        string terminalId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        ArgumentNullException.ThrowIfNull(movements);
        ArgumentNullException.ThrowIfNull(movementPayloads);

        if (movements.Count != movementPayloads.Count)
        {
            throw new ArgumentException(
                "Each movement must have exactly one payload.", nameof(movementPayloads));
        }

        var module = await ModuleAsync(ct).ConfigureAwait(false);

        var result = await module.InvokeAsync<TransferCommitDto>(
            "commitTransfer",
            ct,
            JsonSerializer.SerializeToElement(transfer, Json),
            transferPayload,
            JsonSerializer.SerializeToElement(movements, Json),
            movementPayloads,
            terminalId).ConfigureAwait(false);

        return new TransferCommit(result.TransferId, result.Queued, result.OutboxSeq);
    }

    public async Task SaveTransferAsync(StoredStockTransfer transfer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transfer);

        var module = await ModuleAsync(ct).ConfigureAwait(false);

        // A plain put into the transfers store: no outbox entry, no movements. Used for a document
        // that arrived from the hub, and for a local draft — neither of which this terminal authored
        // as a fact about stock.
        await module.InvokeVoidAsync(
            "put",
            ct,
            "transfers",
            JsonSerializer.SerializeToElement(transfer, Json)).ConfigureAwait(false);
    }

    public async Task<StoredStockTransfer?> GetTransferAsync(string transferId, CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);

        var row = await module
            .InvokeAsync<JsonElement?>("get", ct, "transfers", transferId)
            .ConfigureAwait(false);

        return Deserialize<StoredStockTransfer>(row);
    }

    public async Task<IReadOnlyList<StoredStockTransfer>> GetTransfersAsync(
        string storeId,
        TransferDirection direction = TransferDirection.Outgoing,
        int limit = 100,
        CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);

        var rows = await module
            .InvokeAsync<JsonElement[]>(
                "getTransfers",
                ct,
                storeId,
                direction == TransferDirection.Incoming ? "incoming" : "outgoing",
                limit)
            .ConfigureAwait(false);

        return [.. rows.Select(r => r.Deserialize<StoredStockTransfer>(Json)!).Where(t => t is not null)];
    }

    public async Task<long> ReserveTerminalSequenceAsync(int count = 1, CancellationToken ct = default)    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count), count, "A sequence reservation must cover at least one number.");
        }

        var module = await ModuleAsync(ct).ConfigureAwait(false);

        return await module
            .InvokeAsync<long>("reserveTerminalSequence", ct, count)
            .ConfigureAwait(false);
    }

    public async Task WipeAsync(CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        await module.InvokeVoidAsync("wipe", ct).ConfigureAwait(false);
    }

    private static T? Deserialize<T>(JsonElement? element)
        where T : class
    {
        // The shim returns null for a miss, which arrives as a JSON null rather than a
        // missing value.
        if (element is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.Deserialize<T>(Json);
    }

    private static OutboxStatus ParseStatus(string status) =>
        string.Equals(status, "dead", StringComparison.OrdinalIgnoreCase)
            ? OutboxStatus.Dead
            : OutboxStatus.Pending;

    private sealed record StorageInfoDto(string Name, int Version, bool Persisted);

    private sealed record StorageEstimateDto(long Usage, long Quota);

    private sealed record SaleCommitDto(string SaleId, int Queued, long OutboxSeq);

    private sealed record ReturnCommitDto(string ReturnId, int Queued, long OutboxSeq);

    private sealed record StockCommitDto(string MovementId, long OutboxSeq);

    private sealed record TransferCommitDto(string TransferId, int Queued, long OutboxSeq);

    private sealed record OutboxSummaryDto(int Pending, int Dead, int Total);

    private sealed record OutboxEntryDto(
        string Id,
        long EnqueuedSeq,
        string EntityType,
        string EntityId,
        string TerminalId,
        long TerminalSeq,
        string EnqueuedAt,
        string Status,
        int Attempts,
        string? LastError);
}
