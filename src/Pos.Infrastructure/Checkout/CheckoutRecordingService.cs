// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Globalization;
using System.Text.Json;
using Pos.Core.Domain;
using Pos.Infrastructure.Storage;

namespace Pos.Infrastructure.Checkout;

/// <summary>
/// Outcome of a completed sale.
/// </summary>
/// <param name="Sale">The domain sale that was recorded.</param>
/// <param name="Stored">Its persisted form, ready for reprinting or reporting.</param>
/// <param name="TerminalSeq">The per-terminal sequence assigned to it.</param>
/// <param name="MovementCount">How many stock movements the sale appended to the ledger.</param>
public readonly record struct CompletedSale(
    Sale Sale,
    StoredSale Stored,
    long TerminalSeq,
    int MovementCount = 0)
{
    /// <summary>
    /// A printable document for this sale.
    /// </summary>
    /// <remarks>
    /// Exposed rather than printed here: persisting a sale and printing a receipt are
    /// separate concerns, and a printer fault must not roll back a recorded sale.
    /// </remarks>
    public ReceiptDocument ToDocument(
        Store store,
        PosDocumentType type = PosDocumentType.SalesReceipt,
        string? cashierName = null,
        int copyIndex = 0) => new(store, Sale, type, cashierName, copyIndex);
}

/// <summary>
/// Records sales durably before anything else happens.
/// </summary>
/// <remarks>
/// <para>
/// The ordering here is the whole point of an offline-first till: the sale is committed
/// to local storage, atomically with its stock movements and its sync queue entry,
/// <b>before</b> the receipt is printed. A power cut between the two loses a receipt,
/// which is reprintable. The reverse order would lose the sale itself.
/// </para>
/// <para>
/// Terminal sequences come from a monotonic counter on the device. They order the
/// terminal's own stream for sync, which is why they are per-terminal rather than
/// store-wide: several tills share a store.
/// </para>
/// </remarks>
public sealed class CheckoutRecordingService : IDisposable
{
    private readonly ILocalStore _store;
    private readonly ISaleNumberSource _saleNumbers;
    private readonly ITerminalIdentity _terminal;
    private readonly TimeProvider _time;

    /// <summary>
    /// Serialises completion and commit on this till.
    /// </summary>
    /// <remarks>
    /// Position uniqueness no longer depends on this — the store reserves sequence numbers
    /// atomically, which is what actually makes it safe. What remains is that two sales rung at
    /// the same instant do not interleave their numbering, payload serialisation, and outbox
    /// writes, so an interrupted commit is always readable as one sale rather than two halves.
    /// </remarks>
    private readonly SemaphoreSlim _commitGate = new(1, 1);

    private bool _disposed;

    /// <summary>
    /// Serialisation settings for synced payloads.
    /// </summary>
    /// <remarks>
    /// Camel case because the hub reads <c>businessDate</c> and <c>total</c> by name, and
    /// the JavaScript client produces the same casing.
    /// </remarks>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public CheckoutRecordingService(
        ILocalStore store,
        ISaleNumberSource saleNumbers,
        ITerminalIdentity terminal,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _saleNumbers = saleNumbers ?? throw new ArgumentNullException(nameof(saleNumbers));
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Completes a cart and records it durably, returning the sale for printing.
    /// </summary>
    /// <param name="cart">The cart to finalise. Must not be empty.</param>
    /// <param name="tenders">Payments applied. Must settle the balance exactly.</param>
    /// <param name="store">The store, supplying the code used in the receipt number.</param>
    /// <param name="employeeId">Operator recorded against the sale, for audit.</param>
    /// <param name="businessDate">
    /// Trading day to attribute the sale to. Defaults to today; pass an explicit value for
    /// a till still trading after midnight.
    /// </param>
    /// <param name="catalogVersion">Catalogue version the sale was priced against, when known.</param>
    /// <param name="shiftId">
    /// Shift the sale was rung during. Recorded so a cash-up finds its activity by shift rather
    /// than by a time window, which would misattribute a sale taken either side of a handover.
    /// </param>
    public async Task<CompletedSale> RecordSaleAsync(
        Cart cart,
        IReadOnlyList<Tender> tenders,
        Store store,
        string? employeeId = null,
        DateOnly? businessDate = null,
        string? catalogVersion = null,
        string? shiftId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cart);
        ArgumentNullException.ThrowIfNull(tenders);
        ArgumentNullException.ThrowIfNull(store);

        var date = businessDate ?? DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        var checkout = new CheckoutService(_saleNumbers, _time);

        await _commitGate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var sale = await checkout
                .CompleteSaleAsync(cart, tenders, store.Code, employeeId, date, ct)
                .ConfigureAwait(false);

            // Movements are built first, with placeholder positions, so the reservation can be
            // sized exactly. Over-reserving would leave gaps in the terminal's stream, and a gap
            // is the hub's evidence that records were lost — it must mean something.
            var movements = SaleMapper.ToStockMovements(sale, _terminal.TerminalId, firstTerminalSeq: 0);

            // One reservation covering the sale and its movements, from the terminal's single
            // persisted counter. The sale and its movements therefore take consecutive positions,
            // and a drawer event recorded by another service cannot land on the same one.
            var terminalSeq = await _store
                .ReserveTerminalSequenceAsync(movements.Count + 1, ct)
                .ConfigureAwait(false);

            var stored = SaleMapper.ToStored(
                sale, _terminal.TerminalId, terminalSeq, catalogVersion, shiftId);

            movements = [.. movements.Select((m, i) => m with { TerminalSeq = terminalSeq + 1 + i })];

            // Serialised here rather than in the storage backend, so there is one
            // serialisation implementation rather than one per backend.
            var salePayload = JsonSerializer.Serialize(stored, JsonOptions);
            var movementPayloads = movements
                .Select(m => JsonSerializer.Serialize(m, JsonOptions))
                .ToArray();

            await _store
                .CommitSaleAsync(stored, salePayload, movements, movementPayloads, _terminal.TerminalId, ct)
                .ConfigureAwait(false);

            return new CompletedSale(sale, stored, terminalSeq, movements.Count);
        }
        finally
        {
            _commitGate.Release();
        }
    }

    /// <summary>
    /// Reloads a sale from local storage as a printable document.
    /// </summary>
    /// <remarks>
    /// Used for reprints. The stored record carries the prices and tax as charged, so a
    /// reprint shows what the customer actually paid even if the catalogue has moved on.
    /// </remarks>
    public async Task<StoredSale?> GetForReprintAsync(string saleId, CancellationToken ct = default) =>
        await _store.GetSaleAsync(saleId, ct).ConfigureAwait(false);

    /// <summary>Sales recorded for a trading day, for the end-of-day report.</summary>
    public async Task<IReadOnlyList<StoredSale>> GetTradingDayAsync(
        Store store,
        DateOnly businessDate,
        CancellationToken ct = default) =>
        await _store.GetSalesForDateAsync(store.Id.ToString(), businessDate, ct).ConfigureAwait(false);

    /// <summary>
    /// Totals for a trading day, computed from the recorded sales.
    /// </summary>
    /// <remarks>
    /// Voided sales are reported separately rather than netted off, because a cash-up
    /// needs to show both what was taken and what was reversed.
    /// </remarks>
    public async Task<TradingDayTotals> GetTradingDayTotalsAsync(
        Store store,
        DateOnly businessDate,
        CancellationToken ct = default)
    {
        var sales = await GetTradingDayAsync(store, businessDate, ct).ConfigureAwait(false);

        var completed = sales.Where(s => s.Status == nameof(SaleStatus.Completed)).ToArray();
        var voided = sales.Where(s => s.Status == nameof(SaleStatus.Voided)).ToArray();

        var byTender = completed
            .SelectMany(s => s.Tenders)
            .GroupBy(t => t.Type, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Amount), StringComparer.Ordinal);

        var gross = completed.Sum(s => s.Total);
        var tax = completed.Sum(s => s.TaxTotal);

        return new TradingDayTotals(
            BusinessDate: businessDate,
            SaleCount: completed.Length,
            VoidCount: voided.Length,
            Gross: gross,

            // Net is derived from gross rather than summed from the stored subtotal,
            // because in inclusive tax mode the subtotal is tax-inclusive too. Gross minus
            // tax is unambiguous in both modes and always reconciles to the till.
            Net: gross - tax,
            Tax: tax,
            Discounts: completed.Sum(s => s.TotalDiscount),
            TenderedByType: byTender);
    }

    /// <summary>Formats a business date the way stored records use it.</summary>
    public static string FormatDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _commitGate.Dispose();
    }
}

/// <summary>Aggregated figures for one trading day.</summary>
/// <param name="BusinessDate">The trading day these figures cover.</param>
/// <param name="SaleCount">Number of completed sales.</param>
/// <param name="VoidCount">Number of sales voided during the day.</param>
/// <param name="Gross">Total taken, including tax.</param>
/// <param name="Net">Total excluding tax.</param>
/// <param name="Tax">Tax collected, which is owed onward.</param>
/// <param name="Discounts">Total discount given.</param>
/// <param name="TenderedByType">Amounts split by payment method, for reconciliation.</param>
public readonly record struct TradingDayTotals(
    DateOnly BusinessDate,
    int SaleCount,
    int VoidCount,
    decimal Gross,
    decimal Net,
    decimal Tax,
    decimal Discounts,
    IReadOnlyDictionary<string, decimal> TenderedByType);

/// <summary>
/// Identifies the terminal that took a sale.
/// </summary>
/// <remarks>
/// The terminal id, not the store, is the sync replica identity: several tills share one
/// store, and each has its own ordered stream of records.
/// </remarks>
public interface ITerminalIdentity
{
    /// <summary>Stable identity for this till, generated once and persisted.</summary>
    string TerminalId { get; }
}
