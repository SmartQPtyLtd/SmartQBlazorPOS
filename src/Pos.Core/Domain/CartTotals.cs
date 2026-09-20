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
/// A receipt-ready view of one line, after all discounts have been allocated.
/// </summary>
/// <param name="Line">The underlying cart line.</param>
/// <param name="OrderDiscountAllocated">Portion of the order-level discount charged to this line.</param>
/// <param name="TaxableAmount">Line net minus its share of the order discount.</param>
/// <param name="LineTax">
/// Tax attributable to this line, derived by apportioning the group tax across the
/// lines in that group. The sum of these across all lines always equals the
/// basket tax exactly — see <see cref="CartTotals"/>.
/// </param>
public readonly record struct CartLineTotal(
    CartLine Line,
    decimal OrderDiscountAllocated,
    decimal TaxableAmount,
    decimal LineTax)
{
    /// <summary>Line net after the order discount but still tax-inclusive/exclusive per store mode.</summary>
    public decimal NetAfterDiscounts => TaxableAmount;
}

/// <summary>
/// The complete computed state of a cart: totals, per-line detail, and tax breakdown.
/// </summary>
/// <param name="Mode">Tax mode the totals were computed under.</param>
/// <param name="Currency">Currency of every amount here.</param>
/// <param name="LineTotals">Per-line detail with discounts allocated.</param>
/// <param name="Tax">The basket tax calculation, including the per-rate breakdown.</param>
/// <param name="NetBeforeOrderDiscount">
/// Sum of line nets <em>after</em> line-level discounts but <em>before</em> any
/// order-level discount. Deliberately not called "subtotal": a line discount has
/// already been taken off here, so this is not the sum of the extended prices. Use
/// <see cref="GrossBeforeDiscounts"/> for that.
/// </param>
/// <param name="OrderDiscount">The order-level discount actually applied (clamped to the subtotal).</param>
/// <param name="Total">The amount the customer owes.</param>
public readonly record struct CartTotals(
    TaxMode Mode,
    string Currency,
    IReadOnlyList<CartLineTotal> LineTotals,
    TaxCalculation Tax,
    decimal NetBeforeOrderDiscount,
    decimal OrderDiscount,
    decimal Total)
{
    /// <summary>The amount to present for payment.</summary>
    public Money TotalMoney => new(Total, Currency);

    /// <summary>
    /// Sum of line extended prices with no discounts applied at all. This is the
    /// "was" figure in a "was X, now Y" receipt comparison.
    /// </summary>
    public decimal GrossBeforeDiscounts =>
        LineTotals.Sum(lt => lt.Line.ExtendedAmount);

    /// <summary>Total customer savings, for the receipt "You saved" line.</summary>
    public decimal TotalDiscounts =>
        LineTotals.Sum(lt => lt.Line.LineDiscountAmount) + OrderDiscount;

    public static CartTotals Empty(string currency, TaxMode mode) =>
        new(mode, currency, [], new TaxCalculation(mode, 0m, 0m, 0m, []), 0m, 0m, 0m);
}
