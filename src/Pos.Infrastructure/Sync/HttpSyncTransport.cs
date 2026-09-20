// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Infrastructure.Sync;

namespace Pos.Infrastructure.Sync;

/// <summary>
/// Where the hub lives and how this terminal authenticates to it.
/// </summary>
/// <param name="BaseAddress">Hub base address.</param>
/// <param name="DeviceSecret">Bearer credential issued at enrolment.</param>
/// <param name="StoreId">Store the credential is scoped to, for diagnostics.</param>
public sealed record SyncEndpoint(Uri BaseAddress, string DeviceSecret, string StoreId)
{
    /// <summary>True when the terminal has been enrolled against a hub.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(DeviceSecret) && BaseAddress is not null;
}

/// <summary>
/// Synchronises with the hub over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately thin: it moves DTOs and translates transport failures into ordinary
/// exceptions. All the judgement — what to send, when to dequeue, how to advance a cursor
/// — belongs to <c>SyncClient</c>, which is testable without a network.
/// </para>
/// <para>
/// Requests are safe to retry because the hub deduplicates by client-generated id, so a
/// timeout on a push that actually succeeded is harmless.
/// </para>
/// </remarks>
public sealed class HttpSyncTransport(HttpClient http, SyncEndpoint endpoint) : ISyncTransport
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly SyncEndpoint _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

    public async Task<SyncPushResponse> PushAsync(SyncPushRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var message = CreateRequest(HttpMethod.Post, "api/sync/push");
        message.Content = JsonContent.Create(request, options: Json);

        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        var result = await response.Content
            .ReadFromJsonAsync<SyncPushResponse>(Json, ct)
            .ConfigureAwait(false);

        return result ?? new SyncPushResponse([], "0");
    }

    public async Task<SyncPullResponse> PullAsync(
        string stream,
        string? cursor,
        int limit,
        CancellationToken ct = default)
    {
        var path = $"api/sync/pull?stream={Uri.EscapeDataString(stream)}&limit={limit}";

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            path += $"&cursor={Uri.EscapeDataString(cursor)}";
        }

        using var message = CreateRequest(HttpMethod.Get, path);
        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);

        // A hub that has trimmed its log behind this cursor cannot serve an incremental
        // pull. Reporting it as a reset tells the client to re-bootstrap rather than
        // silently continue with a hole in its replica.
        if (response.StatusCode == HttpStatusCode.Gone)
        {
            return new SyncPullResponse([], cursor ?? "0", HasMore: false, ResetRequired: true);
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        var result = await response.Content
            .ReadFromJsonAsync<SyncPullResponse>(Json, ct)
            .ConfigureAwait(false);

        return result ?? new SyncPullResponse([], cursor ?? "0", false);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var message = new HttpRequestMessage(method, new Uri(_endpoint.BaseAddress, path));

        message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_endpoint.DeviceSecret}");
        message.Headers.TryAddWithoutValidation(SyncHeaders.StoreId, _endpoint.StoreId);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");

        return message;
    }

    /// <summary>
    /// Turns a non-success response into a useful exception.
    /// </summary>
    /// <remarks>
    /// Shared with every other hub call. See <see cref="SyncHttpResponse"/>: the <c>401</c> case is the
    /// one that must not be reimplemented differently, because it means the terminal has been revoked
    /// and the caller must stop rather than retry.
    /// </remarks>
    private static Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct) =>
        SyncHttpResponse.EnsureSuccessAsync(response, ct);
}

/// <summary>
/// The hub refused this terminal's credential.
/// </summary>
/// <remarks>
/// Distinct from a generic transport failure because the correct response is different:
/// a revoked terminal must stop and be re-enrolled, not retry.
/// </remarks>
public sealed class SyncAuthorisationException(string message) : Exception(message);
