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
/// Generates and verifies device credentials.
/// </summary>
/// <remarks>
/// Only hashes are stored. A dumped database therefore yields no usable credential, which
/// matters because a hub may be hosted somewhere less protected than a shop's back office.
/// Comparison is constant-time so a token cannot be recovered by timing the response.
/// </remarks>
public static class DeviceCredentials
{
    private const int SecretBytes = 32;

    /// <summary>Creates a new high-entropy device secret, returned once and never stored.</summary>
    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretBytes))
        .Replace('+', '-')
        .Replace('/', '_')
        .TrimEnd('=');

    /// <summary>Creates a short, human-typeable enrolment code.</summary>
    /// <remarks>
    /// Uses an unambiguous alphabet: no O/0 or I/1, because these are read off a screen
    /// and typed by a person setting up a till.
    /// </remarks>
    public static string NewEnrollmentCode(int length = 12)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[length];

        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(chars);
    }

    /// <summary>
    /// Hashes a secret for storage.
    /// </summary>
    /// <remarks>
    /// A single SHA-256 pass is appropriate here, unlike for a user password: the input is
    /// 256 bits of machine-generated randomness, so there is no dictionary to attack and
    /// key stretching would only add latency to every sync push.
    /// </remarks>
    public static string Hash(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(bytes);
    }

    /// <summary>Constant-time comparison of a presented secret against a stored hash.</summary>
    public static bool Verify(string presentedSecret, string storedHash)
    {
        if (string.IsNullOrEmpty(presentedSecret) || string.IsNullOrEmpty(storedHash))
        {
            return false;
        }

        var computed = Hash(presentedSecret);

        // FixedTimeEquals requires equal lengths; comparing the hex strings keeps this
        // simple and the length is not secret.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed),
            Encoding.ASCII.GetBytes(storedHash.ToUpperInvariant()));
    }
}
