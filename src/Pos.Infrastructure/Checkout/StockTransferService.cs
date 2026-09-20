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
/// Raises, dispatches, and receives stock transfers.
/// </summary>
/// <remarks>
/// <para>
/// This owns the ledger side of a transfer. The document itself decides <em>which</em> movements
/// an event implies; this decides their identity, their position in the terminal's stream, and that
/// they are committed with the document in one write.
/// </para>
/// <para>
/// Both events reserve a block of terminal sequence numbers sized to the transfer's lines, so a
/// dispatch and its movements take consecutive positions. A gap is the hub's evidence that records
/// were lost, so it must not be produced casually.
/// </para>
/// </remarks>
public sealed class StockTransferService(
    ILocalStore store,
    IShiftStore shifts,
    ITerminalIdentity terminal,
    TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILocalStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IShiftStore _shifts = shifts ?? throw new ArgumentNullException(nameof(shifts));
    private readonly ITerminalIdentity _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Raises a transfer in draft, moving nothing.
    /// </summary>
    /// <param name="employee">Operator raising it. Must hold <see cref="EmployeePermissions.TransferStock"/>.</param>
    /// <param name="fromStoreId">Store the goods are leaving. Must be the operator's own store.</param>
    /// <param name="toStoreId">Store the goods are going to.</param>
    /// <param name="fromStoreCode">Sending store's receipt code, for the reference.</param>
    /// <param name="toStoreCode">Receiving store's receipt code, for the reference.</param>
    /// <param name="lines">What is being moved.</param>
    /// <param name="note">Optional note.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<StockTransfer> RaiseAsync(
        Employee employee,
        StoreId fromStoreId,
        StoreId toStoreId,
        string fromStoreCode,
        string toStoreCode,
        IReadOnlyList<StockTransferLine> lines,
        string? note = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(employee);
        ArgumentNullException.ThrowIfNull(lines);

        RequireTransferAuthority(employee);

        if (employee.StoreId != fromStoreId)
        {
            // A transfer moves stock out of the store whose books it leaves. Letting an operator
            // send goods from a shop they are not in would make the sending store's shortage
            // unattributable.
            throw new InvalidOperationException(
                $"{employee.Name} is signed in at another store, so cannot send stock from this one.");
        }

        var now = _time.GetUtcNow();

        var transfer = new StockTransfer
        {
            Id = Guid.CreateVersion7().ToString("N"),
            FromStoreId = fromStoreId,
            ToStoreId = toStoreId,
            Reference = BuildReference(fromStoreCode, toStoreCode, now),
            CreatedAt = now,
            CreatedByEmployeeId = employee.Id.ToString(),
            Lines = [.. lines],
            Note = string.IsNullOrWhiteSpace(note) ? null : note,
        };

        // Refused before it exists rather than after it is stored: a draft that could never be
        // dispatched is worse than no draft, because somebody will spend time on it.
        transfer.Validate();

        _ = ct;

        return Task.FromResult(transfer);
    }

    /// <summary>
    /// Marks a draft transfer as dispatched and takes the stock out of the sending store.
    /// </summary>
    /// <param name="transfer">The transfer to dispatch. Must be a draft.</param>
    /// <param name="employee">Operator dispatching it.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<StoredStockTransfer> DispatchAsync(
        StockTransfer transfer,
        Employee employee,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        ArgumentNullException.ThrowIfNull(employee);

        RequireTransferAuthority(employee);

        if (employee.StoreId != transfer.FromStoreId)
        {
            throw new InvalidOperationException(
                $"{employee.Name} is signed in at another store, so cannot dispatch from this one.");
        }

        var movements = transfer.Dispatch(_time.GetUtcNow());

        return await CommitAsync(transfer, movements, StockMovementReason.TransferOut, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Books a dispatched transfer in at the receiving store.
    /// </summary>
    /// <param name="transfer">The transfer to receive. Must be in transit.</param>
    /// <param name="employee">Operator counting the goods in.</param>
    /// <param name="counted">
    /// What was counted, keyed by product id. Lines left out are taken as arrived in full.
    /// </param>
    /// <param name="note">Optional note, e.g. explaining a shortage.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<StoredStockTransfer> ReceiveAsync(
        StockTransfer transfer,
        Employee employee,
        IReadOnlyDictionary<string, decimal>? counted = null,
        string? note = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        ArgumentNullException.ThrowIfNull(employee);

        RequireTransferAuthority(employee);

        if (employee.StoreId != transfer.ToStoreId)
        {
            // Only the destination books goods in. The sending store confirming its own dispatch
            // would defeat the point of two events: nobody would ever be accountable for the gap.
            throw new InvalidOperationException(
                $"{employee.Name} is signed in at another store, so cannot receive this transfer.");
        }

        var movements = transfer.Receive(
            employee.Id.ToString(),
            _time.GetUtcNow(),
            counted,
            note);

        return await CommitAsync(transfer, movements, StockMovementReason.TransferIn, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Persists a draft so it survives the screen being closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A draft is staging, and it is written as one: no stock moves, no position is claimed in the
    /// terminal's sequence, and <b>nothing is queued for the hub</b>. That last part is the point.
    /// The destination has not been told about goods that have not left, and a draft that reached
    /// the receiving store's screen would have somebody counting in a van that was never loaded.
    /// </para>
    /// <para>
    /// This exists because it did not, and the screen said "saved" anyway. Raising a transfer only
    /// <em>builds</em> the document — the dispatch is what writes it — so a draft was being created
    /// in memory, reported as raised, and dropped when the page was left. Nothing failed, which is
    /// what made it worth fixing properly rather than papering over.
    /// </para>
    /// </remarks>
    /// <param name="transfer">The draft to keep. Must not have been dispatched.</param>
    /// <param name="employee">Operator saving it. Must hold <see cref="EmployeePermissions.TransferStock"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<StoredStockTransfer> SaveDraftAsync(
        StockTransfer transfer,
        Employee employee,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        ArgumentNullException.ThrowIfNull(employee);

        RequireTransferAuthority(employee);

        if (employee.StoreId != transfer.FromStoreId)
        {
            throw new InvalidOperationException(
                $"{employee.Name} is signed in at another store, so cannot raise a transfer from this one.");
        }

        if (transfer.Status != StockTransferStatus.Draft)
        {
            // Saving over a dispatch would put goods that have already left back on the shelf, and
            // the row would no longer say they had gone.
            throw new InvalidOperationException(
                $"{transfer.Reference} has already been {transfer.Status.ToString().ToLowerInvariant()}, "
                + "so it can no longer be saved as a draft.");
        }

        var stored = StoredStockTransfer.FromDomain(transfer, dispatchTerminalSeq: 0);

        await _store.SaveTransferAsync(stored, ct).ConfigureAwait(false);

        return stored;
    }

    /// <summary>The transfer currently in transit, if the terminal holds one.</summary>
    public async Task<StockTransfer?> GetAsync(string transferId, CancellationToken ct = default)
    {
        var stored = await _store.GetTransferAsync(transferId, ct).ConfigureAwait(false);

        return stored?.ToDomain();
    }

    /// <summary>Transfers this store sent, or is expecting.</summary>
    public Task<IReadOnlyList<StoredStockTransfer>> ListAsync(
        string storeId,
        TransferDirection direction = TransferDirection.Outgoing,
        int limit = 100,
        CancellationToken ct = default) =>
        _store.GetTransfersAsync(storeId, direction, limit, ct);

    /// <summary>
    /// Commits the transfer and the movements it caused in one write.
    /// </summary>
    private async Task<StoredStockTransfer> CommitAsync(
        StockTransfer transfer,
        IReadOnlyList<StockTransfer.StockTransferMovement> implied,
        StockMovementReason reason,
        CancellationToken ct)
    {
        // One reservation covering the movements and the transfer document, so nothing else the
        // till records can land on one of their positions.
        var firstSeq = await _store
            .ReserveTerminalSequenceAsync(implied.Count + 1, ct)
            .ConfigureAwait(false);

        var occurredAt = (_time.GetUtcNow()).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        var movements = implied
            .Select((m, index) => new StockMovement
            {
                Id = Guid.CreateVersion7().ToString("N"),
                StoreId = reason == StockMovementReason.TransferOut
                    ? transfer.FromStoreId.ToString()
                    : transfer.ToStoreId.ToString(),
                TerminalId = _terminal.TerminalId,
                TerminalSeq = firstSeq + index,
                QtyDelta = m.Quantity,
                ProductId = m.ProductId,
                Reason = reason.ToString(),
                Reference = transfer.Id,
                OccurredAt = occurredAt,
            })
            .ToArray();

        // The document takes the position after its movements, so the movements are visible in the
        // stream before the paperwork that explains them.
        var documentSeq = firstSeq + implied.Count;

        var stored = StoredStockTransfer.FromDomain(
            transfer,
            dispatchTerminalSeq: reason == StockMovementReason.TransferOut ? documentSeq : 0,
            receiptTerminalSeq: reason == StockMovementReason.TransferIn ? documentSeq : 0);

        var movementPayloads = movements
            .Select(m => JsonSerializer.Serialize(m, JsonOptions))
            .ToArray();

        await _store.CommitTransferAsync(
            stored,
            JsonSerializer.Serialize(stored, JsonOptions),
            movements,
            movementPayloads,
            _terminal.TerminalId,
            ct).ConfigureAwait(false);

        return stored;
    }

    private static void RequireTransferAuthority(Employee employee)
    {
        if (!employee.Can(EmployeePermissions.TransferStock))
        {
            // Moving goods between shops is one of the few operations that makes stock vanish from
            // one set of books entirely, so it is a supervisor act rather than something anyone who
            // can edit a price also inherits.
            throw new InvalidOperationException(
                $"{employee.Name} is not authorised to transfer stock between stores.");
        }
    }

    private static string BuildReference(string fromCode, string toCode, DateTimeOffset at) =>
        $"TR-{fromCode}-{toCode}-{at.ToUniversalTime():yyyyMMddHHmmss}";
}
