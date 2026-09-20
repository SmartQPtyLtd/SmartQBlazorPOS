// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pos.Infrastructure.Sync;
using Pos.Sync.Server.Auth;
using Pos.Sync.Server.Data;
using Pos.Sync.Server.Endpoints;

namespace Pos.Sync.Server.Tests;

/// <summary>
/// Tests for rotating a device credential.
/// </summary>
/// <remarks>
/// <para>
/// Rotation exists because a credential that never changes is valid forever once it leaks, and the
/// secret travels on every sync request. It is only worth having if it is safe over a connection that
/// drops, so the cases below are mostly about the two ways a rotation can go wrong: a response that
/// never arrives, and a token that has ended up in two places.
/// </para>
/// <para>
/// The rules are driven directly rather than through HTTP: <see cref="DeviceRotation"/> is the
/// production code the endpoint calls, and the live endpoint is covered by
/// <c>tools/verify-head-office.ps1</c> against a running hub.
/// </para>
/// </remarks>
public sealed class DeviceRotationTests
{
    private const string CurrentSecret = "current-secret-that-is-long-enough-0001";
    private const string CurrentRefresh = "current-refresh-that-is-long-enough-001";

    [Fact]
    public async Task A_rotation_replaces_the_credential_and_accepts_the_replacement()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (device, _) = await EnrollAsync(db);

        var replacement = NewPair();

        var result = await RotateAsync(db, device, CurrentRefresh, replacement);

        Assert.Equal(RotationOutcome.Rotated, result.Outcome);

        await using var fresh = db.NewContext();
        var stored = await fresh.Devices.SingleAsync(d => d.Id == device.Id);

        Assert.Equal(DeviceCredentials.Hash(replacement.Secret), stored.SecretHash);
        Assert.Equal(DeviceCredentials.Hash(replacement.RefreshToken), stored.RefreshTokenHash);
    }

    [Fact]
    public async Task The_secret_a_rotation_replaced_keeps_working_inside_the_grace_window()
    {
        // The property that makes rotation safe to run over a connection that drops. A till whose
        // response was lost is still holding this secret, and it must not be shut out of its own books
        // while it retries.
        await using var db = await TestDatabase.CreateAsync();
        var (device, _) = await EnrollAsync(db);

        var now = DateTimeOffset.UtcNow;

        await RotateAsync(db, device, CurrentRefresh, NewPair(), now);

        await using var fresh = db.NewContext();
        var stillAccepted = await AuthenticateAsync(
            fresh,
            CurrentSecret,
            now + TimeSpan.FromDays(1));

        Assert.NotNull(stillAccepted);
        Assert.True(stillAccepted!.Value.UsedPreviousSecret);
    }

    [Fact]
    public async Task The_secret_a_rotation_replaced_stops_working_after_the_grace_window()
    {
        // The other half of the bargain: the old secret is a grace period, not a second permanent
        // credential. Without this the credential never really expires at all.
        await using var db = await TestDatabase.CreateAsync();
        var (device, _) = await EnrollAsync(db);

        var now = DateTimeOffset.UtcNow;

        await RotateAsync(db, device, CurrentRefresh, NewPair(), now);

        await using var fresh = db.NewContext();
        var expired = await AuthenticateAsync(
            fresh,
            CurrentSecret,
            now + SyncCredentialPolicy.PreviousSecretGrace + TimeSpan.FromMinutes(1));

        Assert.Null(expired);
    }

    [Fact]
    public async Task An_identical_retry_is_recognised_rather_than_treated_as_reuse()
    {
        // The lost-response path, which is the reason the terminal generates the replacement instead of
        // the hub. Without this the hub would revoke a till for retrying a request it never got an
        // answer to, which turns a dropped packet into a site visit.
        await using var db = await TestDatabase.CreateAsync();
        var (device, _) = await EnrollAsync(db);

        var replacement = NewPair();

        var first = await RotateAsync(db, device, CurrentRefresh, replacement);
        var second = await RotateAsync(db, device, CurrentRefresh, replacement);

        Assert.Equal(RotationOutcome.Rotated, first.Outcome);
        Assert.Equal(RotationOutcome.Retried, second.Outcome);
        Assert.True(second.Succeeded);

        await using var fresh = db.NewContext();
        var stored = await fresh.Devices.SingleAsync(d => d.Id == device.Id);

        Assert.Null(stored.RevokedAt);
        Assert.Equal(DeviceCredentials.Hash(replacement.Secret), stored.SecretHash);
    }

    [Fact]
    public async Task A_superseded_token_minting_different_credentials_revokes_the_device()
    {
        // Two parties hold this device's credentials, and only one of them can be the till.
        await using var db = await TestDatabase.CreateAsync();
        var (device, _) = await EnrollAsync(db);

        await RotateAsync(db, device, CurrentRefresh, NewPair());

        var attack = await RotateAsync(db, device, CurrentRefresh, NewPair());

        Assert.Equal(RotationOutcome.Reuse, attack.Outcome);

        await using var fresh = db.NewContext();
        var stored = await fresh.Devices.SingleAsync(d => d.Id == device.Id);

        Assert.NotNull(stored.RevokedAt);
        Assert.Equal("refresh token reuse", stored.RevokedReason);
    }

    [Fact]
    public async Task A_revoked_device_cannot_authenticate_at_all()
    {
        // Revocation has to mean something: the attacker's replacement is already stored, so refusing
        // only the attacker's next rotation would leave them with a working credential.
        await using var db = await TestDatabase.CreateAsync();
        var (device, _) = await EnrollAsync(db);

        var attacker = NewPair();
        await RotateAsync(db, device, CurrentRefresh, NewPair());
        await RotateAsync(db, device, CurrentRefresh, attacker);

        await using var fresh = db.NewContext();

        Assert.Null(await AuthenticateAsync(fresh, CurrentSecret, DateTimeOffset.UtcNow));
        Assert.Null(await AuthenticateAsync(fresh, attacker.Secret, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_refresh_token_this_device_never_held_is_refused_without_revoking_anything()
    {
        // A wrong guess must not cost a site visit. There is nothing in an unknown token that says the
        // real till has been compromised.
        await using var db = await TestDatabase.CreateAsync();
        var (device, _) = await EnrollAsync(db);

        var result = await RotateAsync(db, device, "a-token-this-device-never-had-000001", NewPair());

        Assert.Equal(RotationOutcome.WrongToken, result.Outcome);

        await using var fresh = db.NewContext();
        var stored = await fresh.Devices.SingleAsync(d => d.Id == device.Id);

        Assert.Null(stored.RevokedAt);
        Assert.Equal(DeviceCredentials.Hash(CurrentSecret), stored.SecretHash);
    }

    [Fact]
    public async Task A_weak_replacement_is_refused_and_changes_nothing()
    {
        // A rotation is the one moment a device's own credential is replaced, so it is the worst place
        // to accept something guessable.
        await using var db = await TestDatabase.CreateAsync();
        var (device, _) = await EnrollAsync(db);

        var result = await RotateAsync(db, device, CurrentRefresh, new DeviceCredentialPair("short", "short"));

        Assert.Equal(RotationOutcome.TooWeak, result.Outcome);

        await using var fresh = db.NewContext();
        var stored = await fresh.Devices.SingleAsync(d => d.Id == device.Id);

        Assert.Equal(DeviceCredentials.Hash(CurrentSecret), stored.SecretHash);
        Assert.Null(stored.PreviousSecretHash);
    }

    [Fact]
    public async Task Every_rotation_outcome_is_written_to_the_audit_trail()
    {
        // The audit trail is the only place a reuse detection is visible after the fact, and it is what
        // an operator reads when a till has to be enrolled again.
        await using var db = await TestDatabase.CreateAsync();
        var (device, _) = await EnrollAsync(db);

        var replacement = NewPair();

        await RotateAsync(db, device, CurrentRefresh, replacement);
        await RotateAsync(db, device, CurrentRefresh, replacement);
        await RotateAsync(db, device, CurrentRefresh, NewPair());

        await using var fresh = db.NewContext();

        var actions = await fresh.DeviceAudits
            .Where(a => a.DeviceId == device.Id)
            .Select(a => a.Action)
            .ToListAsync();

        Assert.Contains("refreshed", actions);
        Assert.Contains("refresh-retried", actions);
        Assert.Contains("revoked-token-reuse", actions);
    }

    [Fact]
    public async Task A_second_rotation_from_the_new_credential_moves_the_grace_window_forward()
    {
        // A till that rotates every week must not accumulate credentials: each rotation supersedes the
        // last, so at most two secrets ever work.
        await using var db = await TestDatabase.CreateAsync();
        var (device, _) = await EnrollAsync(db);

        var first = NewPair();
        var second = NewPair();
        var now = DateTimeOffset.UtcNow;

        await RotateAsync(db, device, CurrentRefresh, first, now);
        await RotateAsync(db, device, first.RefreshToken, second, now + TimeSpan.FromDays(7));

        await using var fresh = db.NewContext();
        var stored = await fresh.Devices.SingleAsync(d => d.Id == device.Id);

        Assert.Equal(DeviceCredentials.Hash(second.Secret), stored.SecretHash);
        Assert.Equal(DeviceCredentials.Hash(first.Secret), stored.PreviousSecretHash);

        // The enrolment secret is two generations back and no longer anywhere in the record.
        Assert.Null(await AuthenticateAsync(fresh, CurrentSecret, now + TimeSpan.FromDays(7)));
        Assert.NotNull(await AuthenticateAsync(fresh, first.Secret, now + TimeSpan.FromDays(7)));
    }

    /// <summary>Registers a store and a device holding a known credential pair.</summary>
    private static async Task<(DeviceEntity Device, string StoreId)> EnrollAsync(TestDatabase db)
    {
        var store = new StoreEntity
        {
            Id = Guid.CreateVersion7().ToString("N"),
            Code = "CT01",
            Name = "CT01 Store",
            Currency = "ZAR",
            TaxMode = "Inclusive",
            DefaultTaxRate = 0.15m,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var device = new DeviceEntity
        {
            Id = Guid.CreateVersion7().ToString("N"),
            StoreId = store.Id,
            Label = "Till 1",
            SecretHash = DeviceCredentials.Hash(CurrentSecret),
            RefreshTokenHash = DeviceCredentials.Hash(CurrentRefresh),
            CredentialIssuedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.Context.Stores.Add(store);
        db.Context.Devices.Add(device);
        await db.Context.SaveChangesAsync();

        return (device, store.Id);
    }

    /// <summary>
    /// Runs a rotation through the production rules, over a device reloaded from the database.
    /// </summary>
    /// <remarks>
    /// Reloaded each time so that what the second call sees is what a second HTTP request would see,
    /// rather than the tracked entity the first call already mutated.
    /// </remarks>
    private static async Task<RotationResult> RotateAsync(
        TestDatabase db,
        DeviceEntity device,
        string presentedRefreshToken,
        DeviceCredentialPair replacement,
        DateTimeOffset? now = null)
    {
        await using var fresh = db.NewContext();
        var tracked = await fresh.Devices.SingleAsync(d => d.Id == device.Id);

        var result = await DeviceRotation.ApplyAsync(
            tracked,
            presentedRefreshToken,
            replacement,
            now ?? DateTimeOffset.UtcNow,
            fresh,
            NullLogger.Instance);

        return result;
    }

    /// <summary>
    /// Resolves a principal from a bearer secret, the way the sync endpoints do.
    /// </summary>
    /// <remarks>
    /// Drives the production <see cref="SyncAuthentication"/>, so the grace window is tested as the sync
    /// path enforces it rather than as a reimplementation of it. The moment is passed in for the same
    /// reason the endpoint passes the current time: a test cannot wait a fortnight.
    /// </remarks>
    private static Task<SyncPrincipal?> AuthenticateAsync(
        SyncDbContext db,
        string secret,
        DateTimeOffset now)
    {
        var context = new DefaultHttpContext();

        context.Request.Headers[SyncHeaders.Authorization] = $"Bearer {secret}";

        return SyncAuthentication.AuthenticateAsync(context, db, CancellationToken.None, now);
    }

    private static DeviceCredentialPair NewPair() => DeviceCredentialGenerator.NewPair();
}
