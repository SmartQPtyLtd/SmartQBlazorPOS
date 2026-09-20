// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

namespace Pos.Infrastructure.Sync;

/// <summary>
/// Compares store ids without caring how they were written down.
/// </summary>
/// <remarks>
/// <para>
/// A store id is a GUID, but a GUID has five common textual forms and three of them are in play
/// here: the hub mints ids as <c>"N"</c> (no dashes), the till's domain type renders them with
/// <c>Guid.ToString()</c> — <c>"D"</c>, with dashes — and anything typed by hand could be either.
/// </para>
/// <para>
/// Comparing them as strings is therefore a bug waiting to happen, and it happened: a transfer
/// addressed to <c>…-…-…</c> was checked against a hub that knew the store as <c>…………</c>, matched
/// nothing, and was quietly stored without ever being relayed to the store expecting the goods. It
/// looked exactly like a transfer that had been sent.
/// </para>
/// <para>
/// The hub is the side that must be tolerant. It issued these ids, so it owns their canonical form,
/// and a client cannot be expected to guess it — especially when the client's own local database is
/// already full of records keyed in a different form.
/// </para>
/// </remarks>
public static class StoreIdFormat
{
    /// <summary>True when two store ids refer to the same store.</summary>
    /// <remarks>
    /// Falls back to an ordinal comparison when either value is not a GUID, so an id that is not a
    /// GUID at all — a hand-written configuration, or a hub that predates this — is still matched
    /// exactly rather than being declared different from itself.
    /// </remarks>
    public static bool Same(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        return Guid.TryParse(left, out var a)
            && Guid.TryParse(right, out var b)
            && a == b;
    }

    /// <summary>
    /// The hub's canonical form for a store id, or the value unchanged when it is not a GUID.
    /// </summary>
    /// <remarks>
    /// Used when writing a store id into a change-log row: the row must carry the form the hub
    /// itself issued, because that is what a terminal's pull is scoped by.
    /// </remarks>
    public static string Canonical(string? value) =>
        Guid.TryParse(value, out var parsed)
            ? parsed.ToString("N")
            : value ?? string.Empty;
}
