// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

namespace Pos.Core.Domain;

/// <summary>Strongly-typed sale identifier.</summary>
/// <remarks>
/// UUIDv7 rather than a random UUID. Version 7 is time-ordered, so identifiers sort
/// chronologically. That matters here for two reasons: receipts and reports come out
/// in the order they happened, and B-tree index inserts stay sequential instead of
/// scattering across pages. Generated on the terminal, so sales remain uniquely
/// identified while offline and can be reconciled later without collision.
/// </remarks>
public readonly record struct SaleId(Guid Value)
{
    public static SaleId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// A human-readable sale number, e.g. <c>CT01-20260325-0042</c>.
/// </summary>
/// <param name="StoreCode">Store that issued the sale.</param>
/// <param name="BusinessDate">Business date the sale belongs to.</param>
/// <param name="Sequence">Per-store, per-date counter starting at 1.</param>
public readonly record struct SaleNumber(string StoreCode, DateOnly BusinessDate, int Sequence)
{
    public override string ToString() =>
        $"{StoreCode}-{BusinessDate:yyyyMMdd}-{Sequence:D4}";
}
