// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Text.Json;
using Pos.Infrastructure.Storage;
using Pos.Infrastructure.Sync;

namespace Pos.Web.Terminal;

/// <summary>
/// Stores stock transfers relayed to this store by the hub.
/// </summary>
/// <remarks>
/// <para>
/// This is the half of a transfer that arrives. The sending store raised and dispatched it against
/// its own ledger; the hub re-scoped the document to this store; and this puts it where the till
/// can see it and book it in.
/// </para>
/// <para>
/// <b>It does not move stock.</b> The goods are still on a van as far as this store's books are
/// concerned, and they stay that way until somebody counts them in. Applying movements here would
/// make stock appear before anyone had checked it was there, which is the whole thing the receipt
/// event exists to prevent.
/// </para>
/// <para>
/// Nothing is queued for upload either. The document came from the hub, and pushing it back would
/// have every store in the estate accumulating copies of one transfer.
/// </para>
/// </remarks>
public sealed class StockTransferChangeApplier(ILocalStore store, ILogger<StockTransferChangeApplier> logger)
    : ISyncChangeApplier
{
    /// <summary>Record type a stock transfer travels under.</summary>
    public const string EntityType = "stockTransfer";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILocalStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ILogger<StockTransferChangeApplier> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<bool> ApplyAsync(SyncChange change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        if (!string.Equals(change.EntityType, EntityType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(change.Payload))
        {
            return false;
        }

        StoredStockTransfer? transfer;
        try
        {
            transfer = JsonSerializer.Deserialize<StoredStockTransfer>(change.Payload, Json);
        }
        catch (JsonException ex)
        {
            TerminalLog.CatalogueChangeUnparsable(_logger, change.EntityId, ex);
            return false;
        }

        if (transfer is null)
        {
            return false;
        }

        var existing = await _store.GetTransferAsync(transfer.Id, ct).ConfigureAwait(false);

        if (existing is not null && IsFurtherAlong(existing, transfer))
        {
            // The relayed document is older than what this store already holds — most likely the
            // store dispatched and then received, and the hub relayed the dispatch afterwards.
            // Overwriting would walk the transfer backwards to "in transit" after somebody had
            // already counted the goods in, and they would be countable a second time.
            TerminalLog.TransferRelaySuperseded(_logger, transfer.Id, existing.Status, transfer.Status);
            return true;
        }

        await _store.SaveTransferAsync(transfer, ct).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Whether the local copy has advanced further than the arriving one.
    /// </summary>
    /// <remarks>
    /// Ordered by lifecycle position, not by timestamp: the sending store's clock decides when a
    /// transfer was dispatched, and a till whose clock is a minute behind would otherwise be able
    /// to overwrite a receipt with a dispatch.
    /// </remarks>
    private static bool IsFurtherAlong(StoredStockTransfer existing, StoredStockTransfer arriving) =>
        Rank(existing.Status) > Rank(arriving.Status);

    private static int Rank(string? status) => status switch
    {
        "Received" => 3,
        "Dispatched" => 2,
        "Cancelled" => 1,
        _ => 0,
    };
}

/// <summary>
/// Applies a pulled change by handing it to whichever applier recognises it.
/// </summary>
/// <remarks>
/// The sync client holds one applier, but a stream carries several record types and each belongs to
/// a different part of the replica. Routing by entity type keeps the catalogue's rules and the
/// transfer's rules in separate classes rather than in one growing switch.
/// </remarks>
public sealed class TerminalChangeApplier(IEnumerable<ISyncChangeApplier> appliers) : ISyncChangeApplier
{
    private readonly ISyncChangeApplier[] _appliers =
        [.. appliers ?? throw new ArgumentNullException(nameof(appliers))];

    public async Task<bool> ApplyAsync(SyncChange change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        foreach (var applier in _appliers)
        {
            if (await applier.ApplyAsync(change, ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        // Nothing claimed it. Returning false rather than throwing keeps a record type this build
        // has never heard of from stopping the sync — the hub is expected to learn new ones.
        return false;
    }
}
