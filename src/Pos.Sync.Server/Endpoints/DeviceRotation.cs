// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Infrastructure.Sync;
using Pos.Sync.Server.Auth;
using Pos.Sync.Server.Data;

namespace Pos.Sync.Server.Endpoints;

/// <summary>What happened when a rotation was submitted.</summary>
public enum RotationOutcome
{
    /// <summary>The device's credential was replaced.</summary>
    Rotated = 0,

    /// <summary>The same rotation was submitted again because its response was lost.</summary>
    Retried = 1,

    /// <summary>The presented refresh token belongs to no rotation this device ever made.</summary>
    WrongToken = 2,

    /// <summary>A superseded token was used to mint different credentials. The device is revoked.</summary>
    Reuse = 3,

    /// <summary>The replacement credential is too short to be worth adopting.</summary>
    TooWeak = 4,
}

/// <summary>Result of a rotation attempt.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="RotateAfter">When the next rotation falls due, when the credential changed.</param>
/// <param name="Detail">Operator-facing explanation.</param>
public readonly record struct RotationResult(
    RotationOutcome Outcome,
    DateTimeOffset? RotateAfter = null,
    string? Detail = null)
{
    /// <summary>True when the credential is as the caller asked for it.</summary>
    public bool Succeeded => Outcome is RotationOutcome.Rotated or RotationOutcome.Retried;
}

/// <summary>
/// The rules for replacing a device credential.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from the endpoint and public so the tests drive these rules rather than a hand-written
/// copy of them. The same reasoning as <see cref="SyncIngest"/>: a test that mirrors the rules stays
/// green while the endpoint diverges, and what diverges here is the difference between a till that
/// rotates its credential and a till that revokes itself.
/// </para>
/// <para>
/// The three cases are separated by what the presented refresh token is: the device's current token
/// (a rotation), the token that rotation replaced together with the identical replacement (a retry
/// after a lost response), or that same superseded token with a different replacement (two holders).
/// </para>
/// </remarks>
public static class DeviceRotation
{
    /// <summary>
    /// Applies a rotation to a device, or explains why it could not be applied.
    /// </summary>
    /// <param name="device">Tracked device entity, already authenticated.</param>
    /// <param name="presentedRefreshToken">The refresh token the caller presented.</param>
    /// <param name="replacement">The pair the caller wants adopted, generated on the terminal.</param>
    /// <param name="now">Current time, passed in so the grace window is testable.</param>
    /// <param name="db">Context, so the audit row joins the same save.</param>
    /// <param name="logger">Hub log.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<RotationResult> ApplyAsync(
        DeviceEntity device,
        string presentedRefreshToken,
        DeviceCredentialPair replacement,
        DateTimeOffset now,
        SyncDbContext db,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(logger);

        if (replacement.Secret.Length < SyncCredentialPolicy.MinimumSecretLength ||
            replacement.RefreshToken.Length < SyncCredentialPolicy.MinimumSecretLength)
        {
            return new RotationResult(
                RotationOutcome.TooWeak,
                Detail: $"Both halves of a replacement must be at least "
                    + $"{SyncCredentialPolicy.MinimumSecretLength} characters.");
        }

        var presented = DeviceCredentials.Hash(presentedRefreshToken);
        var newSecret = DeviceCredentials.Hash(replacement.Secret);
        var newToken = DeviceCredentials.Hash(replacement.RefreshToken);

        if (string.Equals(presented, device.RefreshTokenHash, StringComparison.OrdinalIgnoreCase))
        {
            return await RotateAsync(db, device, newSecret, newToken, now, logger, ct).ConfigureAwait(false);
        }

        if (device.PreviousRefreshTokenHash is not { } superseded ||
            !string.Equals(presented, superseded, StringComparison.OrdinalIgnoreCase))
        {
            // A token this device never held. Refused without revoking: there is nothing here to say
            // the real till has been compromised, and revoking on a wrong guess costs a site visit.
            return new RotationResult(
                RotationOutcome.WrongToken,
                Detail: "That refresh token is not valid for this device.");
        }

        if (string.Equals(newSecret, device.SecretHash, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(newToken, device.RefreshTokenHash, StringComparison.OrdinalIgnoreCase))
        {
            // The identical request, submitted again. This is the lost-response path: the hub applied
            // the rotation and the answer never arrived, so the till is still holding the superseded
            // refresh token and the pair it asked for.
            ServerLog.CredentialRotationRetried(logger, device.Id);

            await AuditAsync(
                db,
                device,
                "refresh-retried",
                "The same rotation was submitted again, so its response did not reach the till.",
                now,
                ct).ConfigureAwait(false);

            return new RotationResult(
                RotationOutcome.Retried,
                (device.CredentialIssuedAt ?? now) + SyncCredentialPolicy.RotationInterval);
        }

        // A superseded token minting different credentials. Only one party can be the till.
        device.RevokedAt = now;
        device.RevokedReason = "refresh token reuse";

        ServerLog.CredentialReuseDetected(logger, device.Id);

        await AuditAsync(
            db,
            device,
            "revoked-token-reuse",
            "A superseded refresh token was used to mint different credentials.",
            now,
            ct).ConfigureAwait(false);

        return new RotationResult(
            RotationOutcome.Reuse,
            Detail: "This device has been revoked because a superseded refresh token was reused. "
                + "It must be enrolled again with a code from head office.");
    }

    private static async Task<RotationResult> RotateAsync(
        SyncDbContext db,
        DeviceEntity device,
        string newSecretHash,
        string newTokenHash,
        DateTimeOffset now,
        ILogger logger,
        CancellationToken ct)
    {
        var previousValidUntil = now + SyncCredentialPolicy.PreviousSecretGrace;

        device.PreviousSecretHash = device.SecretHash;
        device.PreviousSecretValidUntil = previousValidUntil;
        device.PreviousRefreshTokenHash = device.RefreshTokenHash;
        device.SecretHash = newSecretHash;
        device.RefreshTokenHash = newTokenHash;
        device.CredentialIssuedAt = now;

        ServerLog.CredentialRotated(logger, device.Id, previousValidUntil);

        await AuditAsync(
            db,
            device,
            "refreshed",
            $"Credential rotated; the previous secret is accepted until {previousValidUntil:u}.",
            now,
            ct).ConfigureAwait(false);

        return new RotationResult(RotationOutcome.Rotated, now + SyncCredentialPolicy.RotationInterval);
    }

    private static async Task AuditAsync(
        SyncDbContext db,
        DeviceEntity device,
        string action,
        string detail,
        DateTimeOffset now,
        CancellationToken ct)
    {
        db.DeviceAudits.Add(new DeviceAuditEntity
        {
            DeviceId = device.Id,
            StoreId = device.StoreId,
            Action = action,
            Detail = detail,
            OccurredAt = now,
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
