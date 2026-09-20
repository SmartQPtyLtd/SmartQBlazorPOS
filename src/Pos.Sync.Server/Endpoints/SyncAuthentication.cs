// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Microsoft.EntityFrameworkCore;
using Pos.Infrastructure.Sync;
using Pos.Sync.Server.Auth;
using Pos.Sync.Server.Data;

namespace Pos.Sync.Server.Endpoints;

/// <summary>
/// The authenticated caller for a sync request.
/// </summary>
/// <remarks>
/// The store is resolved from the credential, never from the request body. A terminal in
/// a physically insecure shop must not be able to write into another store's books by
/// editing a payload.
/// </remarks>
/// <param name="DeviceId">Enrolled device.</param>
/// <param name="StoreId">Store the credential is scoped to.</param>
/// <param name="UsedPreviousSecret">
/// True when the caller authenticated with the credential a rotation replaced, which is only
/// possible inside the grace window. Worth logging rather than treating as normal: it is how a
/// rotation whose response never arrived shows up on the hub.
/// </param>
public readonly record struct SyncPrincipal(
    string DeviceId,
    string StoreId,
    bool UsedPreviousSecret = false);

/// <summary>Resolves and validates the caller's device credential.</summary>
public static class SyncAuthentication
{
    /// <summary>
    /// Resolves the calling device from the bearer token.
    /// </summary>
    /// <param name="context">The request, for the bearer header.</param>
    /// <param name="db">Hub database.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="now">
    /// The moment the grace window is judged against. Passed in rather than read here, matching the way
    /// an enrolment code is judged, so a test can place a terminal a fortnight past a rotation without
    /// waiting a fortnight.
    /// </param>
    /// <returns>The principal, or null when the credential is missing, wrong, or revoked.</returns>
    public static async Task<SyncPrincipal?> AuthenticateAsync(
        HttpContext context,
        SyncDbContext db,
        CancellationToken ct,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(db);

        var header = context.Request.Headers[SyncHeaders.Authorization].ToString();

        if (string.IsNullOrWhiteSpace(header) ||
            !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var secret = header["Bearer ".Length..].Trim();
        if (secret.Length == 0)
        {
            return null;
        }

        var presented = PresentedSecret.Parse(secret);

        // Looked up by hash so the presented secret is never compared to a stored secret
        // in the database, and so the query cannot be timing-probed. Both columns are searched,
        // because a terminal whose rotation response was lost is still holding the old secret and
        // must not be shut out of its own books while it retries.
        var device = await db.Devices
            .AsNoTracking()
            .FirstOrDefaultAsync(
                d => d.SecretHash == presented.Hash || d.PreviousSecretHash == presented.Hash,
                ct)
            .ConfigureAwait(false);

        if (device is null || !device.IsActive)
        {
            return null;
        }

        if (presented.Matches(device.SecretHash))
        {
            return new SyncPrincipal(device.Id, device.StoreId);
        }

        // The hash matched, so this is the superseded secret. It is only good while the grace window
        // is open, which is what bounds a credential's life even when the till never confirms.
        var stillValid = device.PreviousSecretValidUntil is { } until && until > (now ?? DateTimeOffset.UtcNow);

        return stillValid
            ? new SyncPrincipal(device.Id, device.StoreId, UsedPreviousSecret: true)
            : null;
    }

    /// <summary>Builds the standard unauthorised response.</summary>
    public static IResult Unauthorised(string detail) =>
        Results.Problem(
            title: "Device is not authorised",
            detail: detail,
            statusCode: StatusCodes.Status401Unauthorized);
}

/// <summary>
/// A bearer secret, hashed once so a caller that presents the same secret twice does no
/// duplicate work.
/// </summary>
/// <remarks>
/// A small type rather than two calls to <see cref="DeviceCredentials.Hash"/>, because a hashing
/// mistake is invisible: comparing a hash to a secret, or hashing twice, produces a refusal that looks
/// exactly like a wrong credential.
/// </remarks>
/// <param name="Hash">The presented secret, hashed.</param>
public readonly record struct PresentedSecret(string Hash)
{
    /// <summary>Hashes a presented secret.</summary>
    public static PresentedSecret Parse(string secret) => new(DeviceCredentials.Hash(secret));

    /// <summary>
    /// True when this secret is the one behind <paramref name="storedHash"/>.
    /// </summary>
    /// <remarks>
    /// An ordinary comparison, not a constant-time one, and deliberately: the value being compared was
    /// just used as a primary-key lookup against that very column, so the database has already
    /// answered the question this would be hiding.
    /// </remarks>
    public bool Matches(string? storedHash) =>
        !string.IsNullOrEmpty(storedHash)
        && string.Equals(Hash, storedHash, StringComparison.OrdinalIgnoreCase);
}
