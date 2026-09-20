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
/// Applies catalogue changes pulled from the hub to the local replica.
/// </summary>
/// <remarks>
/// <para>
/// The terminal's catalogue is a read-only replica of head office data, so applying a
/// change is an upsert. Conflicts are resolved by ownership rather than by merging: head
/// office is the single writer for products and prices, so its version simply wins.
/// </para>
/// <para>
/// A store that wants a different price does not mutate these rows — that is a separate,
/// store-owned override — which is what stops a catalogue pull silently discarding a local
/// pricing decision.
/// </para>
/// <para>
/// <b>The change replaces the local row wholesale</b>, which makes every field the payload does not
/// carry a field this erases. So the shape is not defined here: it is
/// <see cref="CatalogProductPayload"/>, shared with the hub that publishes it. An earlier version
/// kept a private payload record listing the fields by hand, and when the preparation station was
/// added to the stored product the list was not updated — the next catalogue sync would have
/// silently stripped the station from every product, and the kitchen would simply have stopped
/// receiving tickets.
/// </para>
/// </remarks>
public sealed class CatalogChangeApplier(ILocalStore store, ILogger<CatalogChangeApplier> logger)
    : ISyncChangeApplier
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILocalStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ILogger<CatalogChangeApplier> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<bool> ApplyAsync(SyncChange change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        if (!string.Equals(change.EntityType, CatalogProductPayload.EntityType, StringComparison.OrdinalIgnoreCase))
        {
            // Other entity types are not part of the terminal's catalogue replica. Returning
            // false rather than throwing keeps an unfamiliar change from breaking a sync.
            return false;
        }

        if (string.IsNullOrWhiteSpace(change.Payload))
        {
            return false;
        }

        CatalogProductPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CatalogProductPayload>(change.Payload, Json);
        }
        catch (JsonException ex)
        {
            TerminalLog.CatalogueChangeUnparsable(_logger, change.EntityId, ex);
            return false;
        }

        if (payload is null)
        {
            return false;
        }

        await _store.UpsertProductsAsync([payload.ToStored()], ct).ConfigureAwait(false);
        return true;
    }

}
