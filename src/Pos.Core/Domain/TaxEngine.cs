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
/// One amount subject to a tax rate, as fed into the tax engine.
/// </summary>
/// <param name="Rate">The tax rate that applies.</param>
/// <param name="Amount">
/// The line's extended amount, already rounded to minor units. For
/// <see cref="TaxMode.Inclusive"/> this is tax-inclusive; for
/// <see cref="TaxMode.Exclusive"/> it is tax-exclusive.
/// </param>
public readonly record struct TaxableAmount(TaxRate Rate, decimal Amount);

/// <summary>
/// Tax computed for one rate group.
/// </summary>
/// <param name="Rate">The rate this group was taxed at.</param>
/// <param name="Net">Amount excluding tax.</param>
/// <param name="Tax">Tax attributable to this group.</param>
public readonly record struct TaxComponent(TaxRate Rate, decimal Net, decimal Tax)
{
    /// <summary>Net plus tax.</summary>
    public decimal Gross => Net + Tax;

    /// <summary>True when this component carries any value worth printing.</summary>
    public bool IsReportable => Net != 0m || Tax != 0m;
}

/// <summary>
/// The full tax outcome for a basket: net, tax, gross, and a per-rate breakdown.
/// </summary>
public readonly record struct TaxCalculation(
    TaxMode Mode,
    decimal Net,
    decimal Tax,
    decimal Gross,
    IReadOnlyList<TaxComponent> Components)
{
    public Money NetMoney(string currency) => new(Net, currency);

    public Money TaxMoney(string currency) => new(Tax, currency);

    public Money GrossMoney(string currency) => new(Gross, currency);
}

/// <summary>
/// Computes tax for a basket under either inclusive or exclusive pricing.
/// </summary>
/// <remarks>
/// <para>
/// Pure and stateless, so it is directly unit-testable without a browser, a
/// database, or a running till. This is deliberate: tax maths is the one part of
/// a POS where a subtle bug is both invisible and financially serious.
/// </para>
/// <para>
/// <b>Grouping matters.</b> Tax is computed on the total of each rate group rather
/// than per line, because per-line rounding then summing drifts from the correctly
/// computed basket total. Real tax authorities assess on the invoice total.
/// </para>
/// </remarks>
public static class TaxEngine
{
    /// <summary>Minor-unit precision for money. Two decimals for all supported currencies so far.</summary>
    public const int DecimalPlaces = 2;

    /// <summary>
    /// Calculates tax for the supplied taxable amounts.
    /// </summary>
    /// <param name="items">Line amounts, each already rounded to minor units.</param>
    /// <param name="mode">Whether the supplied amounts include tax.</param>
    /// <param name="currency">Currency for validation and display.</param>
    public static TaxCalculation Calculate(
        IEnumerable<TaxableAmount> items,
        TaxMode mode,
        string currency)
    {
        ArgumentNullException.ThrowIfNull(items);
        _ = new Money(0m, currency); // validates the currency code early

        var groups = new Dictionary<TaxRate, decimal>();

        foreach (var item in items)
        {
            if (!item.Rate.IsValid)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(items),
                    $"Tax rate '{item.Rate}' is outside the valid 0%-100% range.");
            }

            // Accumulate the rounded line amount so group totals stay exact.
            var rounded = Round(item.Amount);
            groups[item.Rate] = groups.TryGetValue(item.Rate, out var running)
                ? running + rounded
                : rounded;
        }

        var components = new List<TaxComponent>(groups.Count);
        decimal totalNet = 0m;
        decimal totalTax = 0m;

        // Order by rate, then name, so receipts and tests are deterministic
        // regardless of the order items were scanned in.
        foreach (var (rate, groupAmount) in groups
                     .OrderBy(g => g.Key.Rate)
                     .ThenBy(g => g.Key.Name, StringComparer.Ordinal))
        {
            decimal net;
            decimal tax;

            if (mode == TaxMode.Inclusive)
            {
                // Gross is known; back out the net. Withholding tax is a liability
                // from the moment of sale, so the extracted figure must be exact.
                net = rate.IsZero ? groupAmount : Round(groupAmount / (1m + rate.Rate));
                tax = Round(groupAmount - net);
            }
            else
            {
                // Net is known; add tax on top.
                net = groupAmount;
                tax = Round(groupAmount * rate.Rate);
            }

            // In inclusive mode `tax` was derived as the residual
            // (groupAmount - net), so net + tax reconstructs the group amount
            // exactly with no half-cent left over. In exclusive mode the group
            // amount is already the net, so gross is net + tax by definition.
            var component = new TaxComponent(rate, net, tax);

            components.Add(component);
            totalNet += component.Net;
            totalTax += component.Tax;
        }

        totalNet = Round(totalNet);
        totalTax = Round(totalTax);

        return new TaxCalculation(
            Mode: mode,
            Net: totalNet,
            Tax: totalTax,
            Gross: Round(totalNet + totalTax),
            Components: components);
    }

    /// <summary>
    /// Rounds to minor units using banker's rounding.
    /// </summary>
    private static decimal Round(decimal value) =>
        Math.Round(value, DecimalPlaces, MidpointRounding.ToEven);
}
