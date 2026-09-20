// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

namespace Pos.Core.Domain;

/// <summary>How a discount is expressed.</summary>
public enum DiscountKind
{
    /// <summary>A percentage off, e.g. 10% staff discount.</summary>
    Percentage = 0,

    /// <summary>A fixed monetary amount off.</summary>
    FixedAmount = 1,
}

/// <summary>
/// A discount applied to a cart line or to the whole order.
/// </summary>
/// <param name="Kind">Whether <paramref name="Value"/> is a percentage or an amount.</param>
/// <param name="Value">
/// For <see cref="DiscountKind.Percentage"/> a fraction (0.10m = 10%).
/// For <see cref="DiscountKind.FixedAmount"/> a monetary amount.
/// </param>
/// <param name="Reason">Optional label printed on the receipt, e.g. "Staff discount".</param>
public readonly record struct Discount(DiscountKind Kind, decimal Value, string? Reason = null)
{
    /// <summary>True when the discount is well-formed and has an effect.</summary>
    public bool IsValid => Kind switch
    {
        DiscountKind.Percentage => Value is > 0m and <= 1m,
        DiscountKind.FixedAmount => Value > 0m,
        _ => false,
    };

    /// <summary>
    /// Computes the discount amount for a given base, clamped so it can never
    /// exceed the base. A discount that overshoots its base would produce a
    /// negative line — i.e. an accidental refund.
    /// </summary>
    public decimal AmountFor(decimal baseAmount)
    {
        if (baseAmount <= 0m || !IsValid)
        {
            return 0m;
        }

        var raw = Kind == DiscountKind.Percentage
            ? baseAmount * Value
            : Value;

        return Math.Clamp(raw, 0m, baseAmount);
    }

    public override string ToString() => Kind == DiscountKind.Percentage
        ? $"{Value * 100m:0.##}% off"
        : $"{Value:0.00} off";
}
