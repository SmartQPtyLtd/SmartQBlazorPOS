// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Globalization;
using Pos.Infrastructure.Storage;
namespace Pos.Infrastructure.Storage;

/// <summary>
/// An in-memory <see cref="ILocalStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// Used by unit tests and by the Blazor development host, where no IndexedDB exists.
/// It implements the same atomicity contract as the browser implementation, including
/// the rollback behaviour if a commit fails part way.
/// </para>
/// <para>
/// This is a deliberate design choice rather than a test double: the checkout flow can be
/// exercised end to end on a build agent with no browser, which is what makes the till
/// testable in CI.
/// </para>
/// </remarks>
public sealed class InMemoryLocalStore : ILocalStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StoredSale> _sales = [];
    private readonly Dictionary<string, StoredProduct> _products = [];
    private readonly Dictionary<string, StockMovement> _movements = [];
    private readonly Dictionary<string, OutboxEntry> _outbox = [];
    private readonly Dictionary<string, string> _cursors = [];
    private readonly Dictionary<string, long> _sequences = [];

    private long _outboxSeq;

    /// <summary>
    /// Highest terminal sequence handed out, as the browser implementation keeps in IndexedDB.
    /// </summary>
    /// <remarks>
    /// One counter for every record type, because they share one ordered stream.
    /// </remarks>
    private long _terminalSeq;

    /// <summary>Whether durable storage is reported as granted.</summary>
    public bool Persisted { get; set; } = true;

    /// <summary>Simulates the browser refusing a commit, to exercise rollback.</summary>
    public Exception? FailNextCommit { get; set; }

    public Task<StorageInfo> InitialiseAsync(CancellationToken ct = default) =>
        Task.FromResult(new StorageInfo("memory", 1, Persisted));

    public Task<bool> IsPersistedAsync(CancellationToken ct = default) => Task.FromResult(Persisted);

    public Task<StorageEstimate?> GetStorageEstimateAsync(CancellationToken ct = default) =>
        Task.FromResult<StorageEstimate?>(new StorageEstimate(0, 0));

    /// <summary>Serialised payloads awaiting sync, keyed by entity id.</summary>
    private readonly Dictionary<string, string> _payloads = [];

    /// <summary>Refunds, keyed by refund id.</summary>
    private readonly Dictionary<string, StoredReturn> _returns = [];

    /// <summary>Stock transfers, keyed by transfer id.</summary>
    private readonly Dictionary<string, StoredStockTransfer> _transfers = [];

    public Task<ReturnCommit> CommitReturnAsync(
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

        lock (_gate)
        {
            if (FailNextCommit is { } failure)
            {
                FailNextCommit = null;
                throw failure;
            }

            // Snapshot so a failure mid-commit leaves nothing behind, matching the guarantee
            // IndexedDB gives us in the browser.
            var outboxSeqBefore = _outboxSeq;
            var addedOutbox = new List<string>(movements.Count + 1);
            var addedMovements = new List<string>(movements.Count);
            var addedPayloads = new List<string>(movements.Count + 1);

            try
            {
                _returns[salesReturn.Id] = salesReturn;
                _payloads[salesReturn.Id] = returnPayload;
                addedPayloads.Add(salesReturn.Id);

                _outboxSeq++;
                var returnEntry = new OutboxEntry
                {
                    Id = $"return:{salesReturn.Id}",
                    EnqueuedSeq = _outboxSeq,
                    EntityType = "salesReturn",
                    EntityId = salesReturn.Id,
                    TerminalId = terminalId,
                    TerminalSeq = salesReturn.TerminalSeq,
                    EnqueuedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    Status = OutboxStatus.Pending,
                    Attempts = 0,
                };

                _outbox[returnEntry.Id] = returnEntry;
                addedOutbox.Add(returnEntry.Id);

                for (var i = 0; i < movements.Count; i++)
                {
                    var movement = movements[i];

                    _movements[movement.Id] = movement;
                    _payloads[movement.Id] = movementPayloads[i];
                    addedMovements.Add(movement.Id);
                    addedPayloads.Add(movement.Id);

                    _outboxSeq++;
                    var entry = new OutboxEntry
                    {
                        Id = $"movement:{movement.Id}",
                        EnqueuedSeq = _outboxSeq,
                        EntityType = "stockMovement",
                        EntityId = movement.Id,
                        TerminalId = terminalId,
                        TerminalSeq = movement.TerminalSeq,
                        EnqueuedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                        Status = OutboxStatus.Pending,
                        Attempts = 0,
                    };

                    _outbox[entry.Id] = entry;
                    addedOutbox.Add(entry.Id);
                }
            }
            catch
            {
                _returns.Remove(salesReturn.Id);

                foreach (var id in addedOutbox)
                {
                    _outbox.Remove(id);
                }

                foreach (var id in addedMovements)
                {
                    _movements.Remove(id);
                }

                foreach (var id in addedPayloads)
                {
                    _payloads.Remove(id);
                }

                _outboxSeq = outboxSeqBefore;
                throw;
            }

            return Task.FromResult(new ReturnCommit(salesReturn.Id, movements.Count + 1, _outboxSeq));
        }
    }

    public Task<StoredReturn?> GetReturnAsync(string returnId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_returns.TryGetValue(returnId, out var value) ? value : null);
        }
    }

    public Task<IReadOnlyList<StoredReturn>> GetReturnsForSaleAsync(
        string originalSaleId,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<StoredReturn> result = _returns.Values
                .Where(r => string.Equals(r.OriginalSaleId, originalSaleId, StringComparison.Ordinal))
                .OrderBy(r => r.CompletedAt, StringComparer.Ordinal)
                .ToArray();

            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<StoredReturn>> GetReturnsForDateAsync(
        string storeId,
        DateOnly businessDate,
        CancellationToken ct = default)
    {
        var date = SaleMapper.FormatBusinessDate(businessDate);

        lock (_gate)
        {
            IReadOnlyList<StoredReturn> result = _returns.Values
                .Where(r => r.StoreId == storeId && r.BusinessDate == date)
                .OrderBy(r => r.CompletedAt, StringComparer.Ordinal)
                .ToArray();

            return Task.FromResult(result);
        }
    }

    public Task<long> NextReturnSequenceAsync(
        string storeId,
        DateOnly businessDate,
        CancellationToken ct = default)
    {
        // Namespaced away from the sale counter so a refund can never inherit a receipt number.
        var key = $"return:{storeId}:{SaleMapper.FormatBusinessDate(businessDate)}";

        lock (_gate)
        {
            _sequences.TryGetValue(key, out var current);
            var next = current + 1;
            _sequences[key] = next;

            return Task.FromResult(next);
        }
    }

    public Task<SaleCommit> CommitSaleAsync(
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

        lock (_gate)
        {
            if (FailNextCommit is { } failure)
            {
                FailNextCommit = null;
                throw failure;
            }

            // Snapshot the mutable state so a failure mid-commit leaves nothing behind,
            // matching the all-or-nothing guarantee IndexedDB gives us in the browser.
            var outboxSeqBefore = _outboxSeq;
            var addedOutbox = new List<string>(movements.Count + 1);
            var addedMovements = new List<string>(movements.Count);
            var addedPayloads = new List<string>(movements.Count + 1);

            try
            {
                _sales[sale.Id] = sale;
                _payloads[sale.Id] = salePayload;
                addedPayloads.Add(sale.Id);

                _outboxSeq++;
                var saleEntry = new OutboxEntry
                {
                    Id = $"sale:{sale.Id}",
                    EnqueuedSeq = _outboxSeq,
                    EntityType = "sale",
                    EntityId = sale.Id,
                    TerminalId = terminalId,
                    TerminalSeq = sale.TerminalSeq,
                    EnqueuedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    Status = OutboxStatus.Pending,
                    Attempts = 0,
                };

                _outbox[saleEntry.Id] = saleEntry;
                addedOutbox.Add(saleEntry.Id);

                for (var i = 0; i < movements.Count; i++)
                {
                    var movement = movements[i];

                    _movements[movement.Id] = movement;
                    _payloads[movement.Id] = movementPayloads[i];
                    addedMovements.Add(movement.Id);
                    addedPayloads.Add(movement.Id);

                    _outboxSeq++;
                    var entry = new OutboxEntry
                    {
                        Id = $"movement:{movement.Id}",
                        EnqueuedSeq = _outboxSeq,
                        EntityType = "stockMovement",
                        EntityId = movement.Id,
                        TerminalId = terminalId,
                        TerminalSeq = movement.TerminalSeq,
                        EnqueuedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                        Status = OutboxStatus.Pending,
                        Attempts = 0,
                    };

                    _outbox[entry.Id] = entry;
                    addedOutbox.Add(entry.Id);
                }
            }
            catch
            {
                // Roll back exactly what this commit added.
                _sales.Remove(sale.Id);

                foreach (var id in addedOutbox)
                {
                    _outbox.Remove(id);
                }

                foreach (var id in addedMovements)
                {
                    _movements.Remove(id);
                }

                foreach (var id in addedPayloads)
                {
                    _payloads.Remove(id);
                }

                _outboxSeq = outboxSeqBefore;
                throw;
            }

            return Task.FromResult(new SaleCommit(sale.Id, movements.Count + 1, _outboxSeq));
        }
    }

    public Task<string?> GetSyncPayloadAsync(string entityId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_payloads.TryGetValue(entityId, out var payload) ? payload : null);
        }
    }

    public Task<StoredSale?> GetSaleAsync(string saleId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_sales.TryGetValue(saleId, out var sale) ? sale : null);
        }
    }

    public Task<StoredSale?> FindSaleByNumberAsync(string saleNumber, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(saleNumber))
        {
            return Task.FromResult<StoredSale?>(null);
        }

        var needle = saleNumber.Trim();

        lock (_gate)
        {
            // Case-insensitive and whitespace-tolerant: the number is read off a printed slip and
            // typed by hand, so an exact-match requirement would cause needless failed lookups.
            var match = _sales.Values.FirstOrDefault(s =>
                string.Equals(s.Number, needle, StringComparison.OrdinalIgnoreCase));

            return Task.FromResult(match);
        }
    }

    public Task<IReadOnlyList<StoredSale>> FindRecentSalesAsync(
        string storeId,
        DateOnly businessDate,
        int limit = 50,
        CancellationToken ct = default)
    {
        var date = SaleMapper.FormatBusinessDate(businessDate);

        lock (_gate)
        {
            IReadOnlyList<StoredSale> result = _sales.Values
                .Where(s => s.StoreId == storeId && s.BusinessDate == date)
                .OrderByDescending(s => s.CompletedAt, StringComparer.Ordinal)
                .Take(Math.Max(1, limit))
                .ToArray();

            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<StoredSale>> GetSalesForDateAsync(
        string storeId,
        DateOnly businessDate,
        CancellationToken ct = default)
    {
        var date = businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        lock (_gate)
        {
            IReadOnlyList<StoredSale> result = _sales.Values
                .Where(s => s.StoreId == storeId && s.BusinessDate == date)
                .OrderBy(s => s.CompletedAt, StringComparer.Ordinal)
                .ToArray();

            return Task.FromResult(result);
        }
    }

    public Task<StoredProduct?> FindProductByBarcodeAsync(
        string storeId,
        string barcode,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeId);

        lock (_gate)
        {
            var match = _products.Values.FirstOrDefault(
                p => string.Equals(p.StoreId, storeId, StringComparison.Ordinal)
                    && string.Equals(p.Barcode, barcode, StringComparison.Ordinal));

            return Task.FromResult(match);
        }
    }

    public Task<IReadOnlyList<StoredProduct>> SearchProductsByNameAsync(
        string storeId,
        string term,
        int limit = 8,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeId);

        if (string.IsNullOrWhiteSpace(term))
        {
            return Task.FromResult<IReadOnlyList<StoredProduct>>([]);
        }

        lock (_gate)
        {
            IReadOnlyList<StoredProduct> result = _products.Values
                .Where(p => string.Equals(p.StoreId, storeId, StringComparison.Ordinal))
                .Where(p => p.IsActive && !p.IsDeleted
                    && p.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, limit))
                .ToArray();

            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<StoredProduct>> GetProductsAsync(
        string storeId,
        bool includeInactive = false,
        int limit = 500,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<StoredProduct> result = _products.Values
                .Where(p => p.StoreId == storeId)

                // A head-office deletion is authoritative and is never returned, not even to the
                // management screen — otherwise the next catalogue pull would resurrect it. A local
                // deactivation is different: it is reversible, so the management screen can ask for
                // it.
                .Where(p => !p.IsDeleted)
                .Where(p => includeInactive || p.IsActive)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, limit))
                .ToArray();

            return Task.FromResult(result);
        }
    }

    public Task<StoredProduct?> GetProductAsync(string productId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_products.TryGetValue(productId, out var product) ? product : null);
        }
    }

    // -------------------------------------------------------------------------- stock

    public Task<IReadOnlyList<StockMovement>> GetStockMovementsAsync(
        string storeId,
        string? productId = null,
        int limit = 200,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<StockMovement> result = _movements.Values
                .Where(m => m.StoreId == storeId)
                .Where(m => productId is null || m.ProductId == productId)
                .OrderByDescending(m => m.OccurredAt, StringComparer.Ordinal)
                .Take(Math.Max(1, limit))
                .ToArray();

            return Task.FromResult(result);
        }
    }

    public Task<StockCommit> RecordStockMovementAsync(
        StockMovement movement,
        string movementPayload,
        string terminalId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(movement);
        ArgumentNullException.ThrowIfNull(movementPayload);

        lock (_gate)
        {
            if (FailNextCommit is { } failure)
            {
                FailNextCommit = null;
                throw failure;
            }

            _movements[movement.Id] = movement;
            _payloads[movement.Id] = movementPayload;

            _outboxSeq++;
            _outbox[$"movement:{movement.Id}"] = new OutboxEntry
            {
                Id = $"movement:{movement.Id}",
                EnqueuedSeq = _outboxSeq,
                EntityType = "stockMovement",
                EntityId = movement.Id,
                TerminalId = terminalId,
                TerminalSeq = movement.TerminalSeq,
                EnqueuedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Status = OutboxStatus.Pending,
                Attempts = 0,
            };

            return Task.FromResult(new StockCommit(movement.Id, _outboxSeq));
        }
    }

    /// <summary>
    /// Derives stock levels from the movement ledger.
    /// </summary>
    /// <remarks>
    /// Computed on demand rather than stored. A stored level would be a second source of truth
    /// that could disagree with the ledger, and the ledger is what an audit would examine.
    /// </remarks>
    public IReadOnlyDictionary<string, StockLevel> DeriveStockLevels(string storeId)
    {
        lock (_gate)
        {
            return _movements.Values
                .Where(m => m.StoreId == storeId)
                .GroupBy(m => m.ProductId, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => new StockLevel(
                        ProductId: g.Key,
                        Quantity: g.Sum(m => m.QtyDelta),
                        MovementCount: g.Count(),
                        LastMovementAt: g
                            .Select(m => DateTimeOffset.TryParse(
                                m.OccurredAt, CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind, out var at) ? at : (DateTimeOffset?)null)
                            .Where(at => at is not null)
                            .Max()),
                    StringComparer.Ordinal);
        }
    }

    public Task UpsertProductsAsync(IReadOnlyList<StoredProduct> products, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(products);

        lock (_gate)
        {
            foreach (var product in products)
            {
                _products[product.Id] = product;
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxEntry>> PeekOutboxAsync(int limit = 50, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<OutboxEntry> result = _outbox.Values
                .Where(e => e.Status == OutboxStatus.Pending)
                .OrderBy(e => e.EnqueuedSeq)
                .Take(limit)
                .ToArray();

            return Task.FromResult(result);
        }
    }

    public Task AcknowledgeOutboxAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        lock (_gate)
        {
            foreach (var id in ids)
            {
                _outbox.Remove(id);
            }
        }

        return Task.CompletedTask;
    }

    public Task MarkOutboxFailedAsync(string id, string failureDetail, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_outbox.TryGetValue(id, out var entry))
            {
                var attempts = entry.Attempts + 1;

                // Parked after repeated failures so one poison record cannot block the
                // rest of the queue indefinitely.
                _outbox[id] = entry with
                {
                    Attempts = attempts,
                    LastError = failureDetail,
                    Status = attempts >= 8 ? OutboxStatus.Dead : OutboxStatus.Pending,
                };
            }
        }

        return Task.CompletedTask;
    }

    public Task<OutboxSummary> GetOutboxSummaryAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var pending = _outbox.Values.Count(e => e.Status == OutboxStatus.Pending);
            var dead = _outbox.Values.Count(e => e.Status == OutboxStatus.Dead);

            return Task.FromResult(new OutboxSummary(pending, dead, _outbox.Count));
        }
    }

    public Task<string?> GetCursorAsync(string stream, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_cursors.TryGetValue(stream, out var cursor) ? cursor : null);
        }
    }

    public Task SetCursorAsync(string stream, string cursor, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _cursors[stream] = cursor;
        }

        return Task.CompletedTask;
    }

    public Task<long> NextSaleSequenceAsync(
        string storeId,
        DateOnly businessDate,
        CancellationToken ct = default)
    {
        var key = $"{storeId}:{businessDate:yyyy-MM-dd}";

        lock (_gate)
        {
            _sequences.TryGetValue(key, out var current);
            var next = current + 1;
            _sequences[key] = next;

            return Task.FromResult(next);
        }
    }

    public Task<long> ReserveTerminalSequenceAsync(int count = 1, CancellationToken ct = default)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count), count, "A sequence reservation must cover at least one number.");
        }

        lock (_gate)
        {
            var first = _terminalSeq + 1;
            _terminalSeq = first + count - 1;

            return Task.FromResult(first);
        }
    }

    // ------------------------------------------------------------- stock transfers

    public Task<TransferCommit> CommitTransferAsync(
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

        lock (_gate)
        {
            // Everything is staged first and only applied if none of it fails, so a commit that
            // throws part way leaves the ledger and the documents as they were. The browser store
            // gets this from a single IndexedDB transaction; here it is done by hand.
            var outboxSeqBefore = _outboxSeq;
            var enqueuedBefore = _outbox.Keys.ToArray();
            var payloadsBefore = _payloads.Keys.ToArray();

            try
            {
                if (FailNextCommit is { } failure)
                {
                    FailNextCommit = null;
                    throw failure;
                }

                if (_transfers.TryGetValue(transfer.Id, out var existing) &&
                    string.Equals(existing.Status, transfer.Status, StringComparison.Ordinal))
                {
                    // A transfer advances exactly once per event. Re-committing the same state
                    // would take the stock out — or book it in — a second time.
                    throw new InvalidOperationException(
                        $"That transfer has already been recorded as {transfer.Status.ToLowerInvariant()}.");
                }

                _transfers[transfer.Id] = transfer;
                _payloads[transfer.Id] = transferPayload;

                _outbox[$"stockTransfer:{transfer.Id}"] = NewOutboxEntry(
                    "stockTransfer", transfer.Id, terminalId, 0, ++_outboxSeq);

                foreach (var (movement, payload) in movements.Zip(movementPayloads))
                {
                    _movements[movement.Id] = movement;
                    _payloads[movement.Id] = payload;

                    _outbox[$"movement:{movement.Id}"] = NewOutboxEntry(
                        "stockMovement", movement.Id, terminalId, movement.TerminalSeq, ++_outboxSeq);
                }

                return Task.FromResult(new TransferCommit(transfer.Id, movements.Count + 1, _outboxSeq));
            }
            catch
            {
                RollBack(outboxSeqBefore, enqueuedBefore, payloadsBefore);
                throw;
            }
        }
    }

    private static OutboxEntry NewOutboxEntry(
        string entityType,
        string entityId,
        string terminalId,
        long terminalSeq,
        long enqueuedSeq) => new()
        {
            Id = $"{entityType}:{entityId}",
            EnqueuedSeq = enqueuedSeq,
            EntityType = entityType,
            EntityId = entityId,
            TerminalId = terminalId,
            TerminalSeq = terminalSeq,
            EnqueuedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Status = OutboxStatus.Pending,
            Attempts = 0,
        };

    private void RollBack(long outboxSeq, string[] enqueued, string[] payloads)
    {
        _outboxSeq = outboxSeq;

        foreach (var key in _outbox.Keys.Where(k => !enqueued.Contains(k, StringComparer.Ordinal)).ToArray())
        {
            _outbox.Remove(key);
        }

        foreach (var key in _payloads.Keys.Where(k => !payloads.Contains(k, StringComparer.Ordinal)).ToArray())
        {
            _payloads.Remove(key);
        }
    }

    public Task SaveTransferAsync(StoredStockTransfer transfer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transfer);

        lock (_gate)
        {
            // Deliberately no outbox entry: this document came from the hub, and queueing it would
            // push it back as though this store had raised it.
            _transfers[transfer.Id] = transfer;
        }

        return Task.CompletedTask;
    }

    public Task<StoredStockTransfer?> GetTransferAsync(string transferId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_transfers.TryGetValue(transferId, out var transfer) ? transfer : null);
        }
    }

    public Task<IReadOnlyList<StoredStockTransfer>> GetTransfersAsync(
        string storeId,
        TransferDirection direction = TransferDirection.Outgoing,
        int limit = 100,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<StoredStockTransfer> result =
            [
                .. _transfers.Values
                    .Where(t => direction == TransferDirection.Outgoing
                        ? t.FromStoreId == storeId
                        : t.ToStoreId == storeId)
                    .OrderByDescending(t => t.CreatedAt, StringComparer.Ordinal)
                    .Take(Math.Max(1, limit)),
            ];

            return Task.FromResult(result);
        }
    }

    public Task WipeAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            _sales.Clear();
            _products.Clear();
            _movements.Clear();
            _outbox.Clear();
            _cursors.Clear();
            _sequences.Clear();
            _payloads.Clear();
            _returns.Clear();
            _outboxSeq = 0;
            _terminalSeq = 0;
        }

        return Task.CompletedTask;
    }

    /// <summary>All stock movements recorded, for assertions about the ledger.</summary>
    public IReadOnlyList<StockMovement> Movements
    {
        get
        {
            lock (_gate)
            {
                return _movements.Values.ToArray();
            }
        }
    }

    /// <summary>
    /// Every sale committed, in terminal sequence order.
    /// </summary>
    /// <remarks>
    /// Read by <see cref="InMemoryShiftStore"/> when it is linked to this ledger. In the browser
    /// the two stores are one database, so a cash-up finds real sales without anything being
    /// handed to it; an in-memory pair that could not do the same would let the shift lifecycle
    /// pass its tests while the cash-up was wrong in the only place it matters.
    /// </remarks>
    public IReadOnlyList<StoredSale> Sales
    {
        get
        {
            lock (_gate)
            {
                return [.. _sales.Values.OrderBy(s => s.TerminalSeq)];
            }
        }
    }

    /// <summary>Every refund committed, in terminal sequence order.</summary>
    public IReadOnlyList<StoredReturn> Returns
    {
        get
        {
            lock (_gate)
            {
                return [.. _returns.Values.OrderBy(r => r.TerminalSeq)];
            }
        }
    }
}
