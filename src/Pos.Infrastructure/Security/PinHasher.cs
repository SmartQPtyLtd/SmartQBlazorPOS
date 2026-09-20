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
using Pos.Core.Domain;

namespace Pos.Infrastructure.Security;

/// <summary>
/// Hashes operator PINs for storage.
/// </summary>
/// <remarks>
/// <para>
/// The PIN is for accountability, not security, so this is deliberately not a password system.
/// But two properties still matter:
/// </para>
/// <list type="number">
/// <item><b>Per-employee salting.</b> Staff routinely share a PIN like 1234. Without a salt, one
/// leaked hash would reveal that several employees use the same code, and a single precomputed
/// table would break every till in the estate at once.</item>
/// <item><b>Key stretching.</b> A four-digit PIN has only ten thousand possibilities, so a raw
/// hash is trivially reversed even with a salt. Iterating costs a few milliseconds at sign-in —
/// once per shift — and makes enumeration across a whole staff roster impractical.</item>
/// </list>
/// <para>
/// The salt is the employee id, which is stable and already unique. That avoids storing a separate
/// salt column while still giving every employee a distinct hash for the same PIN.
/// </para>
/// </remarks>
public static class PinHasher
{
    /// <summary>
    /// Iteration count.
    /// </summary>
    /// <remarks>
    /// Tuned so a single verification is imperceptible (a few milliseconds on a till) while
    /// exhausting the ten-thousand-value PIN space takes long enough to be pointless. Higher would
    /// be better on a server; this runs on a shop terminal that also has to ring up sales.
    /// </remarks>
    public const int Iterations = 20_000;

    /// <summary>
    /// Hashes a PIN for a specific employee.
    /// </summary>
    /// <param name="employeeId">Stable per-employee value, used as the salt.</param>
    /// <param name="pin">The operator's PIN.</param>
    public static string Hash(EmployeeId employeeId, string pin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pin);

        return Hash(employeeId.ToString(), pin);
    }

    /// <summary>
    /// Hashes a PIN against an arbitrary salt. Exposed for verification before an id is known.
    /// </summary>
    public static string Hash(string salt, string pin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(salt);
        ArgumentException.ThrowIfNullOrWhiteSpace(pin);

        var saltBytes = Encoding.UTF8.GetBytes(salt);
        var pinBytes = Encoding.UTF8.GetBytes(pin);

        // PBKDF2 rather than a repeated raw hash: it is the standard construction for exactly this
        // case and is implemented in the platform, so it behaves the same in the browser and on
        // the server.
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            pinBytes,
            saltBytes,
            Iterations,
            HashAlgorithmName.SHA256,
            outputLength: 32);

        return Convert.ToHexString(derived);
    }

    /// <summary>
    /// Checks a PIN against a stored hash.
    /// </summary>
    /// <remarks>
    /// Constant-time, so a PIN cannot be recovered by timing how long a rejection takes.
    /// </remarks>
    public static bool Verify(EmployeeId employeeId, string pin, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(pin) || string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        var computed = Hash(employeeId, pin);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed),
            Encoding.ASCII.GetBytes(storedHash.ToUpperInvariant()));
    }

    /// <summary>
    /// Validates that a PIN is acceptable before it is hashed.
    /// </summary>
    /// <remarks>
    /// Rejects a PIN that is trivially guessable. The PIN is not a security control, but 0000 on
    /// every till makes the accountability trail worthless: any sale could have been rung by
    /// anyone.
    /// </remarks>
    public static bool IsAcceptable(string pin, out string reason)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            reason = "A PIN is required.";
            return false;
        }

        if (pin.Length is < 4 or > 8 || !pin.All(char.IsAsciiDigit))
        {
            reason = "A PIN must be 4 to 8 digits.";
            return false;
        }

        // Four identical digits, e.g. 1111.
        if (pin.Distinct().Count() == 1)
        {
            reason = "A PIN of identical digits is too easy to guess.";
            return false;
        }

        // A straight run up or down, e.g. 1234 or 4321.
        if (IsSequential(pin))
        {
            reason = "A PIN of sequential digits is too easy to guess.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsSequential(string pin)
    {
        var ascending = true;
        var descending = true;

        for (var i = 1; i < pin.Length; i++)
        {
            var step = pin[i] - pin[i - 1];

            if (step != 1)
            {
                ascending = false;
            }

            if (step != -1)
            {
                descending = false;
            }
        }

        return ascending || descending;
    }
}
