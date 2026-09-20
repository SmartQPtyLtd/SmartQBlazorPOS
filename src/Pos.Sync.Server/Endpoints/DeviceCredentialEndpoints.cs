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
using Pos.Sync.Server.Data;

namespace Pos.Sync.Server.Endpoints;

/// <summary>
/// The endpoint that rotates a device's credential.
/// </summary>
/// <remarks>
/// <para>
/// A device credential that can never change is a credential that is valid forever once it leaks. The
/// secret travels on every sync request, so it is the value most likely to end up somewhere it should
/// not be: a proxy log, a crash dump, a browser profile copied off a stolen till. This lets a till
/// replace it without a site visit.
/// </para>
/// <para>
/// The refresh token travels in its own header and never in the body, so a secret harvested from a
/// request log cannot be used to rotate the credential it came from. It is not a defence against an
/// attacker who can read the till's storage — they get both — and the honest statement of what
/// rotation buys in a browser is a bounded lifetime and a loud signal when a credential is in two
/// places at once.
/// </para>
/// <para>
/// All the judgement lives in <see cref="DeviceRotation"/>; this maps a request onto it and its result
/// onto a status code.
/// </para>
/// </remarks>
public static class DeviceCredentialEndpoints
{
    public static void MapDeviceCredentialEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/devices/rotate", RotateAsync);
    }

    private static async Task<IResult> RotateAsync(
        DeviceRotateRequest request,
        HttpContext context,
        SyncDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var logger = loggerFactory.CreateLogger("DeviceCredential");

        if (string.IsNullOrWhiteSpace(request.NewSecret) ||
            string.IsNullOrWhiteSpace(request.NewRefreshToken))
        {
            return Results.Problem(
                title: "Incomplete rotation",
                detail: "A replacement secret and a replacement refresh token are both required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Authenticated as an ordinary sync request would be, so a till holding a superseded secret can
        // still rotate — and therefore confirm — rather than being stuck one rotation behind with no
        // way to catch up.
        var principal = await SyncAuthentication.AuthenticateAsync(context, db, ct).ConfigureAwait(false);

        if (principal is not { } caller)
        {
            return SyncAuthentication.Unauthorised(
                "Rotating a credential requires the credential being replaced.");
        }

        var presentedToken = context.Request.Headers[SyncHeaders.RefreshToken].ToString().Trim();

        if (presentedToken.Length == 0)
        {
            return SyncAuthentication.Unauthorised(
                $"A rotation must present the current refresh token in the {SyncHeaders.RefreshToken} header.");
        }

        var device = await db.Devices
            .FirstOrDefaultAsync(d => d.Id == caller.DeviceId, ct)
            .ConfigureAwait(false);

        if (device is null || !device.IsActive)
        {
            return SyncAuthentication.Unauthorised("This device is not enrolled, or has been revoked.");
        }

        var result = await DeviceRotation.ApplyAsync(
            device,
            presentedToken,
            new DeviceCredentialPair(request.NewSecret, request.NewRefreshToken),
            DateTimeOffset.UtcNow,
            db,
            logger,
            ct).ConfigureAwait(false);

        if (result.Succeeded)
        {
            return Results.Ok(new DeviceRotateResponse(device.Id, result.RotateAfter!.Value));
        }

        if (result.Outcome is RotationOutcome.TooWeak)
        {
            return Results.Problem(
                title: "Replacement credential too weak",
                detail: result.Detail,
                statusCode: StatusCodes.Status400BadRequest);
        }

        return SyncAuthentication.Unauthorised(result.Detail ?? "The rotation was refused.");
    }
}
