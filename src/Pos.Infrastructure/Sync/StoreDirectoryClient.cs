// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Net.Http.Json;
using System.Text.Json;

namespace Pos.Infrastructure.Sync;

/// <summary>
/// The estate's store directory as this terminal last saw it.
/// </summary>
/// <param name="OwnStoreId">The calling terminal's own store, from the hub.</param>
/// <param name="Stores">Every store known to the hub.</param>
public sealed record StoreDirectory(string OwnStoreId, IReadOnlyList<SyncStoreSummary> Stores)
{
    /// <summary>An empty directory, for a terminal that has never reached a hub.</summary>
    public static StoreDirectory Empty { get; } = new(string.Empty, []);

    /// <summary>
    /// Stores this terminal could legitimately transfer stock to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Excludes the terminal's own store — a transfer to yourself is meaningless and the hub would
    /// reject it — and excludes stores that have stopped trading, because sending stock into a
    /// closed branch is a mistake an operator should not be able to make from a dropdown.
    /// </para>
    /// <para>
    /// "Its own store" is decided by identity, not by text. The hub reports its own id in the form it
    /// minted, and the terminal holds that same value rendered by the domain type; comparing the
    /// strings would leave the till cheerfully offering to send stock to itself.
    /// </para>
    /// </remarks>
    public IEnumerable<SyncStoreSummary> TransferTargets =>
        Stores.Where(s => s.IsActive && !StoreIdFormat.Same(s.Id, OwnStoreId));
}

/// <summary>
/// Fetches the estate's store directory from the hub.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="ISyncTransport"/> on purpose. The transport moves records and is
/// implemented by simulations that have no network; the directory is a plain read of
/// reference data, and folding it into the transport would force every fake to answer a
/// question it has no opinion about.
/// </para>
/// <para>
/// Never throws. A till that cannot reach the hub must still sell, so an unreachable
/// directory degrades to "no known destinations" and the transfers screen says so, rather
/// than failing the page. The caller decides whether that is fatal; for raising a transfer
/// it merely means the destination list is empty until the next successful sync.
/// </para>
/// </remarks>
public sealed class StoreDirectoryClient(HttpClient http, SyncEndpoint endpoint)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly SyncEndpoint _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

    /// <summary>The endpoint this client reads from, for diagnostics.</summary>
    public SyncEndpoint Endpoint => _endpoint;

    /// <summary>
    /// Reads the directory, or <see cref="StoreDirectory.Empty"/> if it cannot be read.
    /// </summary>
    public async Task<StoreDirectory> FetchAsync(CancellationToken ct = default)
    {
        if (!_endpoint.IsConfigured)
        {
            return StoreDirectory.Empty;
        }

        try
        {
            using var message = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(_endpoint.BaseAddress, "api/sync/stores"));

            message.Headers.TryAddWithoutValidation(
                SyncHeaders.Authorization,
                $"Bearer {_endpoint.DeviceSecret}");
            message.Headers.TryAddWithoutValidation(SyncHeaders.StoreId, _endpoint.StoreId);

            using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return StoreDirectory.Empty;
            }

            var directory = await response.Content
                .ReadFromJsonAsync<SyncStoreDirectoryResponse>(Json, ct)
                .ConfigureAwait(false);

            return directory is null
                ? StoreDirectory.Empty
                : new StoreDirectory(directory.StoreId, directory.Stores ?? []);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            // Offline, hub down, DNS gone, or a captive portal answering with HTML. None of
            // these should stop a shop trading, so they all mean the same thing here.
            return StoreDirectory.Empty;
        }
    }
}
