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

/// <summary>Request to exchange an enrolment code for a device credential.</summary>
/// <param name="Code">Short-lived, single-use code issued by head office.</param>
/// <param name="Label">Operator-facing name for the till, e.g. "Front counter 2".</param>
/// <param name="DeviceSecret">
/// Generated on the terminal and never transmitted again. The hub stores only its hash,
/// so a database dump yields no usable credential.
/// </param>
/// <param name="DeviceRefreshToken">
/// The second half of the credential, also generated on the terminal and also stored only as a hash.
/// Sent when the credential is rotated and at no other time, so it never appears in a request log
/// alongside the secret it belongs to.
/// </param>
public sealed record EnrollRequest(
    string Code,
    string Label,
    string DeviceSecret,
    string DeviceRefreshToken);

/// <summary>Result of a successful enrolment.</summary>
/// <param name="DeviceId">Identity of the enrolled till.</param>
/// <param name="StoreId">Store the credential is scoped to.</param>
/// <param name="StoreCode">Receipt code for the store.</param>
/// <param name="Currency">Store currency.</param>
/// <param name="TaxMode">Store tax mode.</param>
/// <param name="RotateAfter">When this credential should be replaced.</param>
public sealed record EnrollResponse(
    string DeviceId,
    string StoreId,
    string StoreCode,
    string Currency,
    string TaxMode,
    DateTimeOffset RotateAfter);

/// <summary>Request to create a store and its first enrolment code.</summary>
/// <param name="Code">Short store code printed on receipts, e.g. "CT01".</param>
/// <param name="Name">Store name.</param>
/// <param name="Currency">ISO 4217 currency code.</param>
/// <param name="TaxMode">"Inclusive" or "Exclusive".</param>
/// <param name="DefaultTaxRate">Fractional rate, e.g. 0.15.</param>
/// <param name="TaxName">Display name for the tax, e.g. "VAT".</param>
public sealed record ProvisionStoreRequest(
    string Code,
    string Name,
    string Currency,
    string TaxMode = "Inclusive",
    decimal DefaultTaxRate = 0.15m,
    string TaxName = "VAT");

/// <summary>Enrolment and device lifecycle endpoints.</summary>
/// <remarks>
/// <para>
/// Everything here except <c>enroll</c> is a head-office operation and requires the head-office
/// token. That split is the security boundary of the whole design, not a convenience.
/// </para>
/// <para>
/// An enrolment code is the bootstrap for a device credential. Any caller able to mint one can
/// enrol an unlimited number of devices of its own and write into the estate indefinitely — so a
/// device credential must never be sufficient to reach it. A till holds a device credential, and a
/// till may sit in a shop that is not physically secure, which is exactly why its authority stops
/// at its own store's books.
/// </para>
/// <para>
/// <c>enroll</c> stays open by necessity: the terminal calling it has no credential yet. It is
/// protected instead by the code itself, which is single-use, short-lived, and only mintable by
/// head office.
/// </para>
/// </remarks>
public static class EnrollmentEndpoints
{
    /// <summary>How long an enrolment code stays valid.</summary>
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromHours(24);

    public static void MapEnrollmentEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/enrollment");

        group.MapPost("/stores", ProvisionStoreAsync);
        group.MapPost("/codes/{storeId}", IssueCodeForStoreAsync);
        group.MapPost("/enroll", EnrollAsync);

        app.MapPost("/api/devices/{deviceId}/revoke", RevokeAsync);
        app.MapGet("/api/devices", ListAsync);
    }

    /// <summary>
    /// Creates a store and issues its first enrolment code.
    /// </summary>
    /// <remarks>
    /// The bootstrap path for standing up a new shop, and the most powerful endpoint on the hub:
    /// it mints the credential that everything else trusts.
    /// </remarks>
    private static async Task<IResult> ProvisionStoreAsync(
        ProvisionStoreRequest request,
        HttpContext context,
        SyncDbContext db,
        HeadOfficeCredential credential,
        CancellationToken ct)
    {
        if (HeadOfficeAuthentication.Authorise(context, credential) is { } refusal)
        {
            return refusal;
        }

        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.Problem(
                title: "Invalid store",
                detail: "A code and a name are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.Currency.Length != 3)
        {
            return Results.Problem(
                title: "Invalid currency",
                detail: "Currency must be a 3-letter ISO 4217 code.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.DefaultTaxRate is < 0m or > 1m)
        {
            return Results.Problem(
                title: "Invalid tax rate",
                detail: "The default tax rate is a fraction between 0 and 1, e.g. 0.15 for 15%.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var exists = await db.Stores.AnyAsync(s => s.Code == request.Code, ct).ConfigureAwait(false);
        if (exists)
        {
            return Results.Problem(
                title: "Store code already in use",
                detail: $"A store with code '{request.Code}' already exists.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var store = new StoreEntity
        {
            Id = Guid.CreateVersion7().ToString("N"),
            Code = request.Code.ToUpperInvariant(),
            Name = request.Name,
            Currency = request.Currency.ToUpperInvariant(),
            TaxMode = request.TaxMode,
            DefaultTaxRate = request.DefaultTaxRate,
            DefaultTaxName = request.TaxName,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.Stores.Add(store);

        var code = await IssueCodeAsync(store.Id, db, ct).ConfigureAwait(false);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Results.Ok(new
        {
            storeId = store.Id,
            storeCode = store.Code,
            enrollmentCode = code.Code,
            expiresAt = code.ExpiresAt,
        });
    }

    /// <summary>Issues a fresh single-use enrolment code for an existing store.</summary>
    private static async Task<IResult> IssueCodeForStoreAsync(
        string storeId,
        HttpContext context,
        SyncDbContext db,
        HeadOfficeCredential credential,
        CancellationToken ct)
    {
        if (HeadOfficeAuthentication.Authorise(context, credential) is { } refusal)
        {
            return refusal;
        }

        var store = await db.Stores
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == storeId, ct)
            .ConfigureAwait(false);

        if (store is null)
        {
            return Results.Problem(
                title: "Unknown store",
                detail: $"No store with id '{storeId}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var code = await IssueCodeAsync(store.Id, db, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Results.Ok(new { enrollmentCode = code.Code, expiresAt = code.ExpiresAt, storeId = store.Id });
    }

    /// <summary>
    /// Enrols a terminal, exchanging a one-time code for a durable device credential.
    /// </summary>
    /// <remarks>
    /// The code is consumed on success, so a code read off a setup sheet cannot be reused
    /// to enrol an attacker's device. The device secret arrives from the terminal and only
    /// its hash is retained.
    /// </remarks>
    private static async Task<IResult> EnrollAsync(
        EnrollRequest request,
        SyncDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Enrollment");

        if (string.IsNullOrWhiteSpace(request.Code) ||
            string.IsNullOrWhiteSpace(request.Label) ||
            string.IsNullOrWhiteSpace(request.DeviceSecret) ||
            string.IsNullOrWhiteSpace(request.DeviceRefreshToken))
        {
            return Results.Problem(
                title: "Incomplete enrolment",
                detail: "A code, a label, a device secret, and a refresh token are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.DeviceSecret.Length < SyncCredentialPolicy.MinimumSecretLength ||
            request.DeviceRefreshToken.Length < SyncCredentialPolicy.MinimumSecretLength)
        {
            // A weak secret would undermine the only credential protecting a store's books, and a weak
            // refresh token would undermine the rotation that bounds it.
            return Results.Problem(
                title: "Device credential too weak",
                detail: "The device secret and refresh token must each be at least "
                    + $"{SyncCredentialPolicy.MinimumSecretLength} characters of high-entropy random data.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var code = request.Code.Trim().ToUpperInvariant();

        var enrollment = await db.EnrollmentCodes
            .FirstOrDefaultAsync(e => e.Code == code, ct)
            .ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;

        if (enrollment is null || !enrollment.IsUsable(now))
        {
            // Deliberately vague: distinguishing "expired" from "already used" would let a
            // caller probe which codes exist.
            return Results.Problem(
                title: "Enrolment code is not valid",
                detail: "The code is unknown, already used, or has expired. Request a new one.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var store = await db.Stores
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == enrollment.StoreId, ct)
            .ConfigureAwait(false);

        if (store is null || !store.IsActive)
        {
            return Results.Problem(
                title: "Store is not active",
                detail: "This store is closed or unknown, so a terminal cannot be enrolled against it.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var device = new DeviceEntity
        {
            Id = Guid.CreateVersion7().ToString("N"),
            StoreId = store.Id,
            Label = request.Label,
            SecretHash = DeviceCredentials.Hash(request.DeviceSecret),
            RefreshTokenHash = DeviceCredentials.Hash(request.DeviceRefreshToken),
            CredentialIssuedAt = now,
            CreatedAt = now,
            LastSeenAt = now,
        };

        enrollment.ConsumedAt = now;
        enrollment.ConsumedByDeviceId = device.Id;

        db.Devices.Add(device);
        db.DeviceAudits.Add(new DeviceAuditEntity
        {
            DeviceId = device.Id,
            StoreId = store.Id,
            Action = "enrolled",
            Detail = $"Label '{request.Label}' enrolled with code {code}.",
            OccurredAt = now,
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        ServerLog.TerminalEnrolled(logger, device.Id, store.Code);

        return Results.Ok(new EnrollResponse(
            device.Id,
            store.Id,
            store.Code,
            store.Currency,
            store.TaxMode,
            now + SyncCredentialPolicy.RotationInterval));
    }

    /// <summary>
    /// Revokes a device.
    /// </summary>
    /// <remarks>
    /// Intended to be called when a till is stolen or decommissioned. The terminal is
    /// expected to wipe its local data when it next sees a 401.
    /// </remarks>
    private static async Task<IResult> RevokeAsync(
        string deviceId,
        HttpContext context,
        SyncDbContext db,
        HeadOfficeCredential credential,
        string? reason,
        CancellationToken ct)
    {
        if (HeadOfficeAuthentication.Authorise(context, credential) is { } refusal)
        {
            return refusal;
        }

        var device = await db.Devices
            .FirstOrDefaultAsync(d => d.Id == deviceId, ct)
            .ConfigureAwait(false);

        if (device is null)
        {
            return Results.Problem(
                title: "Unknown device",
                detail: $"No device with id '{deviceId}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (device.IsActive)
        {
            var now = DateTimeOffset.UtcNow;
            device.RevokedAt = now;
            device.RevokedReason = reason ?? "Revoked by an administrator.";

            db.DeviceAudits.Add(new DeviceAuditEntity
            {
                DeviceId = device.Id,
                StoreId = device.StoreId,
                Action = "revoked",
                Detail = device.RevokedReason,
                OccurredAt = now,
            });

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return Results.Ok(new { deviceId = device.Id, revoked = true });
    }

    /// <summary>Lists enrolled devices, for the head-office device register.</summary>
    private static async Task<IResult> ListAsync(
        HttpContext context,
        SyncDbContext db,
        HeadOfficeCredential credential,
        string? storeId = null,
        CancellationToken ct = default)
    {
        if (HeadOfficeAuthentication.Authorise(context, credential) is { } refusal)
        {
            return refusal;
        }

        var query = db.Devices.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(storeId))
        {
            query = query.Where(d => d.StoreId == storeId);
        }

        var devices = await query
            .OrderBy(d => d.CreatedAt)
            .Select(d => new
            {
                deviceId = d.Id,
                storeId = d.StoreId,
                label = d.Label,
                isActive = d.RevokedAt == null,
                createdAt = d.CreatedAt,
                lastSeenAt = d.LastSeenAt,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Results.Ok(devices);
    }

    private static async Task<EnrollmentCodeEntity> IssueCodeAsync(
        string storeId,
        SyncDbContext db,
        CancellationToken ct)
    {
        _ = ct;

        var code = new EnrollmentCodeEntity
        {
            Code = DeviceCredentials.NewEnrollmentCode(),
            StoreId = storeId,
            ExpiresAt = DateTimeOffset.UtcNow.Add(CodeLifetime),
        };

        db.EnrollmentCodes.Add(code);

        return await Task.FromResult(code).ConfigureAwait(false);
    }
}
