// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

namespace Pos.Core.Domain;

/// <summary>
/// How tax is presented to the customer at this store.
/// </summary>
/// <remarks>
/// This is configured per store because the two models are mutually exclusive on a
/// receipt and getting it wrong misstates every total:
///
/// <list type="bullet">
/// <item><see cref="Inclusive"/> — displayed prices already contain tax (UK/EU/ZA,
/// and most consumer retail worldwide). The tax is <em>extracted</em> from the
/// shelf price for reporting, and the customer pays exactly what was on the label.</item>
/// <item><see cref="Exclusive"/> — displayed prices exclude tax, which is
/// <em>added</em> at checkout (US/Canada). The customer pays more than the label.</item>
/// </list>
/// </remarks>
public enum TaxMode
{
    /// <summary>Shelf price includes tax. Tax is extracted for reporting.</summary>
    Inclusive = 0,

    /// <summary>Shelf price excludes tax. Tax is added at checkout.</summary>
    Exclusive = 1,
}

/// <summary>
/// A single tax rate that can apply to a product or a store.
/// </summary>
/// <param name="Name">Display name, e.g. "VAT" or "Sales Tax".</param>
/// <param name="Rate">Fractional rate, e.g. 0.15m for 15%.</param>
public readonly record struct TaxRate(string Name, decimal Rate)
{
    /// <summary>Zero-rated / exempt.</summary>
    public static TaxRate Zero(string name = "Tax") => new(name, 0m);

    public bool IsZero => Rate == 0m;

    /// <summary>True when the rate is a sane fraction (0% to 100%).</summary>
    public bool IsValid => Rate is >= 0m and <= 1m;

    public override string ToString() => $"{Name} {Rate * 100m:0.##}%";
}
