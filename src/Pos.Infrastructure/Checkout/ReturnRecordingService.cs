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
/// Outcome of a recorded refund.
/// </summary>
/// <param name="Return">The domain refund that was recorded.</param>
/// <param name="Stored">Its persisted form, ready for reprinting.</param>
/// <param name="TerminalSeq">The per-terminal sequence assigned to it.</param>
/// <param name="TenderWarnings">
/// Non-fatal concerns, such as refunding cash for a card sale. Surfaced rather than thrown,
/// because there are legitimate reasons, but the operator must see them.
/// </param>
public readonly record struct CompletedReturn(
    SalesReturn Return,
    StoredReturn Stored,
    long TerminalSeq,
    IReadOnlyList<string> TenderWarnings);

/// <summary>
/// Records refunds durably, enforcing the refund policy.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a separate service from <see cref="CheckoutRecordingService"/>. A sale and a
/// refund are different operations with different risks, and keeping them apart makes it
/// obvious at the call site which one is happening — a refund is never something that occurs
/// by accident.
/// </para>
/// <para>
/// The policy is enforced here rather than in the UI. The refund is the most abused operation
/// at a till, so the rules must hold no matter which screen calls this.
/// </para>
/// </remarks>
public sealed class ReturnRecordingService(
    ILocalStore store,
    ITerminalIdentity terminal,
    TimeProvider? timeProvider = null) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILocalStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ITerminalIdentity _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Serialises refund commits on this till.
    /// </summary>
    /// <remarks>
    /// Position uniqueness comes from the store's atomic reservation, not from this. What remains
    /// is that a refund cannot interleave with another one's over-refund check, which reads what
    /// has already been returned and would otherwise be able to race.
    /// </remarks>
    private readonly SemaphoreSlim _commitGate = new(1, 1);

    private bool _disposed;

    /// <summary>
    /// Loads a sale and works out what may still be refunded from it.
    /// </summary>
    /// <param name="saleId">The sale to look up.</param>
    public async Task<(StoredSale Sale, IReadOnlyList<StoredReturn> PreviousReturns, RefundableState State)?>
        PrepareRefundAsync(string saleId, CancellationToken ct = default)
    {
        var stored = await _store.GetSaleAsync(saleId, ct).ConfigureAwait(false);

        if (stored is null)
        {
            return null;
        }

        var previous = await _store.GetReturnsForSaleAsync(saleId, ct).ConfigureAwait(false);

        // Rebuilt as a domain sale so the policy works on the same type it was written against,
        // rather than a second implementation over the stored shape.
        var sale = SaleMapper.ToDomain(stored);
        var state = RefundPolicy.GetRefundableState(sale, [.. previous.Select(SaleMapper.ToDomain)]);

        return (stored, previous, state);
    }

    /// <summary>
    /// Records a refund against a sale.
    /// </summary>
    /// <param name="saleId">The sale the goods came from.</param>
    /// <param name="selections">Which lines, and how many of each, are coming back.</param>
    /// <param name="reason">Why the goods came back.</param>
    /// <param name="refundTenderType">
    /// How the money goes back. Normally the original method; a mismatch is reported as a
    /// warning rather than blocked.
    /// </param>
    /// <param name="employeeId">Operator who processed it, for audit.</param>
    /// <param name="note">Optional free text, e.g. a fault description.</param>
    /// <param name="businessDate">
    /// Trading day to attribute the refund to. Defaults to today; pass an explicit value for a
    /// till still trading after midnight.
    /// </param>
    /// <param name="shiftId">Shift the refund was processed during, so a cash-up can find it.</param>
    /// <param name="authorisedBy">
    /// The operator exercising refund authority, when there is one locally. Refused unless they
    /// hold <see cref="EmployeePermissions.Refund"/>.
    /// </param>
    /// <exception cref="InvalidOperationException">The refund is not permitted.</exception>
    public async Task<CompletedReturn> RecordReturnAsync(
        string saleId,
        IReadOnlyList<(string Barcode, decimal Quantity)> selections,
        ReturnReason reason,
        TenderType refundTenderType = TenderType.Cash,
        string? employeeId = null,
        string? note = null,
        DateOnly? businessDate = null,
        string? shiftId = null,
        Employee? authorisedBy = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selections);

        if (selections.Count == 0)
        {
            throw new InvalidOperationException("A return must contain at least one line.");
        }

        // Refunds are the highest-risk operation at a till: cash leaves the drawer and the goods
        // come back, so it is the classic route for taking money out of a shop. Checking the
        // permission here rather than only in the screen means a caller cannot reach a refund by a
        // route that forgot to ask.
        //
        // Optional only because a refund arriving from another terminal — pulled by sync, with an
        // employee id but no local employee record — has no operator object to check. Every
        // locally-initiated refund names one.
        if (authorisedBy is { } operatorInAuthority &&
            !operatorInAuthority.Can(EmployeePermissions.Refund))
        {
            throw new InvalidOperationException(
                $"{operatorInAuthority.Name} is not authorised to process refunds. " +
                "A supervisor needs to sign in.");
        }

        await _commitGate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var stored = await _store.GetSaleAsync(saleId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("That sale could not be found on this terminal.");

            var previous = await _store.GetReturnsForSaleAsync(saleId, ct).ConfigureAwait(false);

            var sale = SaleMapper.ToDomain(stored);
            var previousReturns = previous.Select(SaleMapper.ToDomain).ToArray();

            // Resolve the requested barcodes against the sale. Matching on barcode rather than
            // line index means a caller cannot reference a line that does not exist.
            var chosen = new List<(SaleLine Line, decimal Quantity)>();

            foreach (var (barcode, quantity) in selections)
            {
                var line = sale.Lines.FirstOrDefault(l =>
                    string.Equals(l.Barcode, barcode, StringComparison.Ordinal));

                if (line == default)
                {
                    throw new InvalidOperationException(
                        $"Barcode '{barcode}' is not on that sale, so it cannot be refunded.");
                }

                chosen.Add((line, quantity));
            }

            var lines = RefundPolicy.BuildLines(sale, chosen, sale.Currency);

            // The policy is the gate. Nothing is written unless it passes.
            RefundPolicy.Validate(sale, lines, previousReturns);

            var date = businessDate ?? DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
            var now = _time.GetUtcNow();
            var sequence = await _store
                .NextReturnSequenceAsync(stored.StoreId, date, ct)
                .ConfigureAwait(false);

            var totalRefund = CartLine.Round(lines.Sum(l => l.LineRefund));

            var salesReturn = new SalesReturn
            {
                Id = ReturnId.New(),
                StoreId = sale.StoreId,
                OriginalSaleId = sale.Id,
                OriginalSaleNumber = sale.Number,
                Number = $"R-{sale.Number.StoreCode}-{date:yyyyMMdd}-{sequence:D4}",
                CompletedAt = now,
                BusinessDate = date,
                Currency = sale.Currency,
                Lines = lines,
                Refunds = [new Tender(refundTenderType, new Money(totalRefund, sale.Currency))],
                Reason = reason,
                Note = note,
                EmployeeId = employeeId,
            };

            // The over-refund check needs the completed refund, so it runs after construction.
            RefundPolicy.ValidateRefundAmount(sale, salesReturn, previousReturns);

            // Refunding cash for a card sale is a classic fraud route. Reported, not blocked,
            // because there are legitimate reasons — but always visible.
            var warnings = RefundPolicy.FindTenderMismatches(sale, salesReturn);

            // Movements first, with placeholder positions, so the reservation is sized exactly.
            // Over-reserving would leave gaps, and a gap is how the hub detects lost records.
            var movements = SaleMapper.ToReturnStockMovements(
                salesReturn, _terminal.TerminalId, firstTerminalSeq: 0);

            // One reservation from the terminal's single persisted counter, covering the refund
            // and the movements that return its goods to the ledger.
            var terminalSeq = await _store
                .ReserveTerminalSequenceAsync(movements.Count + 1, ct)
                .ConfigureAwait(false);

            var storedReturn = SaleMapper.ToStoredReturn(
                salesReturn, _terminal.TerminalId, terminalSeq, shiftId);

            movements = [.. movements.Select((m, i) => m with { TerminalSeq = terminalSeq + 1 + i })];

            var returnPayload = JsonSerializer.Serialize(storedReturn, JsonOptions);
            var movementPayloads = movements
                .Select(m => JsonSerializer.Serialize(m, JsonOptions))
                .ToArray();

            await _store
                .CommitReturnAsync(storedReturn, returnPayload, movements, movementPayloads, _terminal.TerminalId, ct)
                .ConfigureAwait(false);

            return new CompletedReturn(salesReturn, storedReturn, terminalSeq, warnings);
        }
        finally
        {
            _commitGate.Release();
        }
    }

    /// <summary>Refunds recorded for a trading day, for the end-of-day report.</summary>
    public async Task<IReadOnlyList<StoredReturn>> GetTradingDayReturnsAsync(
        StoreId storeId,
        DateOnly businessDate,
        CancellationToken ct = default) =>
        await _store
            .GetReturnsForDateAsync(storeId.ToString(), businessDate, ct)
            .ConfigureAwait(false);

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
