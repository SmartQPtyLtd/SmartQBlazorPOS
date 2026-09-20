// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Security.Cryptography;
using System.Text;

namespace Pos.Sync.Server.Auth;

/// <summary>
/// The credential that gates head-office operations on the hub.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a different kind of credential from a device secret, not a flag on one.
/// </para>
/// <para>
/// A device credential is scoped to exactly one store and is held on a till in a shop that may
/// not be physically secure — that is the whole reason the blast radius is bounded to one store's
/// books. The authority to <em>create</em> stores, <em>mint</em> enrolment codes, and
/// <em>revoke</em> other terminals cannot live on a device at all: an enrolment code is the
/// bootstrap for a device credential, so any terminal able to mint one could enrol an unlimited
/// number of its own devices and write into the estate indefinitely.
/// </para>
/// <para>
/// It is supplied out of band through configuration, never through the database, so a dumped
/// database yields nothing that can provision anything. It is <b>not</b> stored as a hash, because
/// there is no row to store it in — the hub holds it as an operator-set secret and compares
/// against it directly.
/// </para>
/// <para>
/// <b>Absent configuration means the endpoints refuse everything.</b> Failing open would be the
/// worst of both worlds: a hub that looks configured, runs, and silently hands out credentials to
/// anyone who asks.
/// </para>
/// </remarks>
public sealed class HeadOfficeCredential
{
    /// <summary>Configuration key holding the operator-set token.</summary>
    public const string ConfigurationKey = "HeadOffice:Token";

    /// <summary>Shortest token the hub will accept as configured.</summary>
    /// <remarks>
    /// A short token is worse than none, because it looks like protection. Thirty-two characters
    /// is the same floor a device secret has to clear.
    /// </remarks>
    public const int MinimumLength = 32;

    private readonly string? _token;

    public HeadOfficeCredential(string? token)
    {
        // A too-short token is treated as unconfigured rather than accepted. Accepting it would
        // give an operator a false sense that head-office endpoints were protected when a token
        // of eight characters is guessable in seconds.
        _token = !string.IsNullOrWhiteSpace(token) && token.Length >= MinimumLength
            ? token
            : null;
    }

    /// <summary>True when a usable token was configured.</summary>
    public bool IsConfigured => _token is not null;

    /// <summary>
    /// Checks a presented token.
    /// </summary>
    /// <remarks>
    /// Constant-time, so the token cannot be recovered by measuring how long a rejection takes.
    /// When nothing is configured this is always false, which is what makes the endpoints fail
    /// closed rather than open.
    /// </remarks>
    public bool Verify(string? presented)
    {
        if (_token is null || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(_token);
        var actual = Encoding.UTF8.GetBytes(presented);

        // FixedTimeEquals requires equal lengths, and the length of a secret is not itself
        // secret in any useful sense — an attacker who can measure it has already lost.
        return expected.Length == actual.Length &&
            CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>Generates a token suitable for configuration.</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}

/// <summary>
/// Resolves and checks the head-office credential for an incoming request.
/// </summary>
public static class HeadOfficeAuthentication
{
    /// <summary>Header carrying the head-office token.</summary>
    public const string HeaderName = "X-Pos-HeadOffice";

    /// <summary>
    /// Authenticates a head-office request.
    /// </summary>
    /// <returns>The refusal to return, or null when the caller is authenticated.</returns>
    /// <remarks>
    /// <para>
    /// The two refusals are deliberately different, unlike the deliberately vague device
    /// enrolment refusal. There, distinguishing "expired" from "already used" would help an
    /// attacker probe which codes exist. Here the caller is an operator standing up a hub: an
    /// unconfigured token is a setup mistake they must be told about, and answering with a plain
    /// "not authorised" would send them hunting for a typo in a token that was never set.
    /// </para>
    /// <para>
    /// Neither message reveals anything secret: one says the hub has no token, the other says the
    /// one presented is wrong.
    /// </para>
    /// </remarks>
    public static IResult? Authorise(HttpContext context, HeadOfficeCredential credential)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(credential);

        if (!credential.IsConfigured)
        {
            return Results.Problem(
                title: "Head-office access is not configured",
                detail:
                    $"Set '{HeadOfficeCredential.ConfigurationKey}' to a token of at least " +
                    $"{HeadOfficeCredential.MinimumLength} characters. Head-office endpoints " +
                    "refuse every request until then, so that an unconfigured hub cannot be used " +
                    "to enrol devices.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var presented = context.Request.Headers[HeaderName].ToString();

        return credential.Verify(presented)
            ? null
            : Results.Problem(
                title: "Head-office authorisation required",
                detail: $"A valid '{HeaderName}' token is required for this endpoint.",
                statusCode: StatusCodes.Status401Unauthorized);
    }
}
