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
/// A monetary amount in a specific currency.
/// </summary>
/// <remarks>
/// Money is a value object: two amounts are equal only when both the value and the
/// currency match. Arithmetic between different currencies throws rather than
/// silently producing a meaningless number.
///
/// We use <see cref="decimal"/> throughout — never <c>double</c>. Binary floating
/// point cannot represent 0.10 exactly, and those errors accumulate across a
/// basket until the receipt total disagrees with the sum of its lines.
/// </remarks>
public readonly record struct Money : IComparable<Money>
{
    public Money(decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            throw new ArgumentException("Currency is required.", nameof(currency));
        }

        if (currency.Length != 3)
        {
            throw new ArgumentException(
                $"Currency must be a 3-letter ISO 4217 code, got '{currency}'.", nameof(currency));
        }

        Amount = amount;
        Currency = currency.ToUpperInvariant();
    }

    /// <summary>The numeric amount, unrounded. Rounding happens at the till boundary.</summary>
    public decimal Amount { get; }

    /// <summary>ISO 4217 currency code, upper-cased.</summary>
    public string Currency { get; }

    /// <summary>
    /// Zero in the given currency.
    /// </summary>
    public static Money Zero(string currency) => new(0m, currency);

    public bool IsZero => Amount == 0m;

    public bool IsNegative => Amount < 0m;

    public bool IsPositive => Amount > 0m;

    /// <summary>
    /// Rounds to the currency's smallest minor unit using banker's rounding.
    /// </summary>
    /// <remarks>
    /// MidpointRounding.ToEven (banker's rounding) is the correct choice for money:
    /// it avoids the upward bias that AwayFromZero introduces over many transactions
    /// and is what most accounting standards expect. Every total that reaches a
    /// customer's receipt or a drawer count passes through here exactly once.
    /// </remarks>
    public Money Rounded(int decimals = 2) =>
        new(Math.Round(Amount, decimals, MidpointRounding.ToEven), Currency);

    private void EnsureSameCurrency(Money other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot combine {Currency} with {other.Currency}.");
        }
    }

    public static Money operator +(Money left, Money right)
    {
        left.EnsureSameCurrency(right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        left.EnsureSameCurrency(right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    public static Money operator -(Money value) => new(-value.Amount, value.Currency);

    public static Money operator *(Money value, decimal factor) =>
        new(value.Amount * factor, value.Currency);

    public static Money operator *(decimal factor, Money value) => value * factor;

    public static Money operator /(Money value, decimal divisor)
    {
        if (divisor == 0m)
        {
            throw new DivideByZeroException("Cannot divide Money by zero.");
        }

        return new Money(value.Amount / divisor, value.Currency);
    }

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(other);
        return Amount.CompareTo(other.Amount);
    }

    /// <summary>
    /// Formats for display, e.g. <c>R 123.45</c>. Receipt layout uses the ESC/POS
    /// renderer instead; this is for UI and logs.
    /// </summary>
    public override string ToString() => $"{Currency} {Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}";
}
