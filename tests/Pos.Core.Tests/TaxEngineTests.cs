// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;

namespace Pos.Core.Tests;

/// <summary>
/// Tax is the highest-risk arithmetic in a POS: an error is invisible on screen but
/// wrong on every receipt and every tax return. These tests pin both tax modes and,
/// critically, the reconstruction invariant that the printed lines must always add
/// up to the printed total.
/// </summary>
public sealed class TaxEngineTests
{
    private const string Zar = "ZAR";

    [Fact]
    public void Inclusive_extracts_tax_from_a_gross_amount()
    {
        // 115.00 including 15% VAT => 100.00 net + 15.00 VAT
        var result = TaxEngine.Calculate(
            [new TaxableAmount(new TaxRate("VAT", 0.15m), 115.00m)],
            TaxMode.Inclusive,
            Zar);

        Assert.Equal(100.00m, result.Net);
        Assert.Equal(15.00m, result.Tax);
        Assert.Equal(115.00m, result.Gross);
    }

    [Fact]
    public void Exclusive_adds_tax_to_a_net_amount()
    {
        // 100.00 excluding 15% VAT => 115.00 due
        var result = TaxEngine.Calculate(
            [new TaxableAmount(new TaxRate("VAT", 0.15m), 100.00m)],
            TaxMode.Exclusive,
            Zar);

        Assert.Equal(100.00m, result.Net);
        Assert.Equal(15.00m, result.Tax);
        Assert.Equal(115.00m, result.Gross);
    }

    [Fact]
    public void Inclusive_gross_never_drifts_from_the_shelf_price()
    {
        // The customer must pay exactly the labelled price. This is the promise
        // inclusive pricing makes, and it must hold for awkward rates and amounts.
        var rates = new[] { 0.15m, 0.20m, 0.05m, 0.075m, 0.14m, 0.23m };
        var amounts = new[] { 0.01m, 0.99m, 1.00m, 9.99m, 19.99m, 123.45m, 999.99m };

        foreach (var rate in rates)
        {
            foreach (var amount in amounts)
            {
                var result = TaxEngine.Calculate(
                    [new TaxableAmount(new TaxRate("VAT", rate), amount)],
                    TaxMode.Inclusive,
                    Zar);

                Assert.Equal(amount, result.Gross);
                Assert.Equal(amount, result.Net + result.Tax);
            }
        }
    }

    [Fact]
    public void Tax_is_assessed_per_rate_group_not_per_line()
    {
        // Three lines of 0.01 at 15% inclusive. Assessed per line, each yields a
        // fraction of a cent and rounds to zero tax; assessed on the group total it
        // does not. Grouping is the behaviour tax authorities expect.
        var rate = new TaxRate("VAT", 0.15m);
        var result = TaxEngine.Calculate(
            [
                new TaxableAmount(rate, 0.01m),
                new TaxableAmount(rate, 0.01m),
                new TaxableAmount(rate, 0.01m),
            ],
            TaxMode.Inclusive,
            Zar);

        Assert.Equal(0.03m, result.Gross);
        Assert.Equal(0.03m, result.Net + result.Tax);
    }

    [Fact]
    public void Zero_rated_items_carry_no_tax()
    {
        // Basic foodstuffs are commonly zero-rated; they must appear on the receipt
        // as a tax group with zero tax, not be omitted.
        var result = TaxEngine.Calculate(
            [
                new TaxableAmount(new TaxRate("VAT", 0.15m), 115.00m),
                new TaxableAmount(TaxRate.Zero("VAT-Zero"), 50.00m),
            ],
            TaxMode.Inclusive,
            Zar);

        Assert.Equal(15.00m, result.Tax);
        Assert.Equal(165.00m, result.Gross);

        var zeroComponent = result.Components.Single(c => c.Rate.Name == "VAT-Zero");
        Assert.Equal(0m, zeroComponent.Tax);
        Assert.Equal(50.00m, zeroComponent.Net);
    }

    [Fact]
    public void Component_breakdown_reconciles_to_the_totals()
    {
        var result = TaxEngine.Calculate(
            [
                new TaxableAmount(new TaxRate("VAT", 0.15m), 115.00m),
                new TaxableAmount(new TaxRate("VAT", 0.15m), 230.00m),
                new TaxableAmount(new TaxRate("Levy", 0.05m), 21.00m),
            ],
            TaxMode.Inclusive,
            Zar);

        Assert.Equal(result.Net, result.Components.Sum(c => c.Net));
        Assert.Equal(result.Tax, result.Components.Sum(c => c.Tax));
        Assert.Equal(result.Gross, result.Components.Sum(c => c.Gross));
    }

    [Fact]
    public void A_rate_above_one_hundred_percent_is_rejected()
    {
        // Guards against a config typo such as entering "15" instead of "0.15".
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            TaxEngine.Calculate(
                [new TaxableAmount(new TaxRate("VAT", 15m), 100m)],
                TaxMode.Inclusive,
                Zar));

        Assert.Contains("0%-100%", exception.Message);
    }

    [Fact]
    public void An_empty_basket_yields_zero_tax()
    {
        var result = TaxEngine.Calculate([], TaxMode.Inclusive, Zar);

        Assert.Equal(0m, result.Net);
        Assert.Equal(0m, result.Tax);
        Assert.Equal(0m, result.Gross);
        Assert.Empty(result.Components);
    }

    [Fact]
    public void An_unknown_currency_code_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            TaxEngine.Calculate([], TaxMode.Inclusive, "RANDOM"));
    }

    [Fact]
    public void Rounding_uses_bankers_rounding_to_avoid_upward_bias()
    {
        // 2.5% of 1.00 = 0.025 exactly, a true midpoint. AwayFromZero would give
        // 0.03 on every such sale; ToEven gives 0.02, removing the systematic
        // over-charge that accumulates across thousands of transactions.
        var result = TaxEngine.Calculate(
            [new TaxableAmount(new TaxRate("VAT", 0.025m), 1.00m)],
            TaxMode.Exclusive,
            Zar);

        Assert.Equal(0.02m, result.Tax);
    }
}
