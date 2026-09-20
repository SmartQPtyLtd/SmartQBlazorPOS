// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Infrastructure.Sync;

namespace Pos.Web.Terminal;

/// <summary>
/// Replaces this till's device credential when it is due.
/// </summary>
/// <remarks>
/// <para>
/// Runs at the head of every sync pass, because that is the moment the till is talking to the hub and
/// the moment a due rotation can actually be completed. Wiring it into the two buttons that sync and
/// the startup path separately would mean the next place that syncs silently never rotates, and the
/// failure of remembering is invisible: a credential that simply keeps working forever.
/// </para>
/// <para>
/// The order of operations is the safety property. A replacement pair is generated and
/// <b>persisted before it is sent</b>; the hub is asked to adopt it; only a confirmed response swaps the
/// credential in use. So a response lost in transit leaves the till authenticating with the pair the hub
/// already knows, holding the pair it asked for, and retrying the same request — which the hub answers
/// as the rotation it already applied. A till that instead generated a fresh pair on each attempt would
/// present a superseded refresh token with different credentials, which is the one thing the hub reads
/// as a stolen token.
/// </para>
/// <para>
/// A till enrolled before credentials rotated holds no refresh token. It keeps trading and syncing, and
/// cannot rotate until it is enrolled again — which is the honest outcome rather than a refusal to work.
/// </para>
/// </remarks>
public sealed class CredentialRotationPreflight(
    TerminalIdentity identity,
    DeviceRotationClient client,
    ILogger<CredentialRotationPreflight> logger) : ISyncPreflight
{
    private readonly TerminalIdentity _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    private readonly DeviceRotationClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly ILogger<CredentialRotationPreflight> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Rotates the credential if a rotation is due, and does nothing otherwise.
    /// </summary>
    /// <remarks>
    /// A rotation the hub refused outright (401) is rethrown: the device has been revoked or has
    /// presented a refresh token it never held, and the screen that handles a revoked terminal is
    /// reached through exactly one path. Everything else — no network, a hub restarting, a timeout —
    /// is logged and left for the next pass, because a credential inside its grace window still works
    /// and a shop must not stop selling over housekeeping.
    /// </remarks>
    public async Task PrepareAsync(CancellationToken ct = default)
    {
        if (!_identity.RotationDue(DateTimeOffset.UtcNow))
        {
            return;
        }

        if (_identity.RefreshToken is not { Length: > 0 } refreshToken)
        {
            // Enrolled before refresh tokens existed. Nothing safe to rotate with.
            return;
        }

        var replacement = _identity.PendingRotation ?? DeviceCredentialGenerator.NewPair();

        try
        {
            if (_identity.PendingRotation is null)
            {
                // Persist first. See the class remarks: a request that goes out without this being
                // written down is a request the till cannot safely repeat.
                await _identity.StageRotationAsync(replacement, ct).ConfigureAwait(false);
            }

            var response = await _client.RotateAsync(refreshToken, replacement, ct).ConfigureAwait(false);

            await _identity.ConfirmRotationAsync(response.RotateAfter, ct).ConfigureAwait(false);

            TerminalLog.CredentialRotated(_logger, response.RotateAfter);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SyncAuthorisationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TerminalLog.CredentialRotationFailed(_logger, ex);

            // The current credential stays in use and the pending one stays recorded, so the next
            // attempt is the same request rather than a new one.
        }
    }
}
