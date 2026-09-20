// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Security.Cryptography;

namespace Pos.Infrastructure.Sync;

/// <summary>
/// The two halves of a device credential, generated on the terminal.
/// </summary>
/// <remarks>
/// Never persisted by anything except the till that made it, and never transmitted to anyone except
/// the hub as a hash. Named as a pair because they are issued together and replaced together: a
/// rotation that changed one and not the other would leave a device whose secret and refresh token
/// disagree about which generation they belong to.
/// </remarks>
/// <param name="Secret">The bearer secret presented on every sync request.</param>
/// <param name="RefreshToken">Presented only when rotating, so it does not travel on ordinary calls.</param>
public sealed record DeviceCredentialPair(string Secret, string RefreshToken);

/// <summary>
/// Creates device credentials and refresh tokens.
/// </summary>
/// <remarks>
/// 32 bytes of CSPRNG output, base64url encoded, which is the same construction at enrolment and at
/// every rotation. Deliberately not the hub's job: a credential the hub generated is a credential the
/// hub could regenerate, and generating it here is also what makes a rotation safe to retry, because
/// the till already holds the pair it is asking the hub to adopt.
/// </remarks>
public static class DeviceCredentialGenerator
{
    /// <summary>Bytes of randomness in a credential.</summary>
    private const int SecretBytes = 32;

    /// <summary>One new high-entropy secret.</summary>
    public static string NewSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(SecretBytes);

        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>A new secret and a new refresh token, generated together.</summary>
    public static DeviceCredentialPair NewPair() => new(NewSecret(), NewSecret());
}
