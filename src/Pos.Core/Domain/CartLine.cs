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
/// One line in an in-progress sale.
/// </summary>
/// <remarks>
/// Mutable by design: a cashier scans an item, then changes its quantity, applies a
/// discount, or voids it. The <see cref="Sale"/> aggregate captures an immutable
/// snapshot once the sale is finalised, so historical receipts never shift when the
/// catalogue is edited later.
/// </remarks>
public sealed class CartLine
{
    public required ProductId ProductId { get; init; }

    /// <summary>Barcode as scanned, retained for receipt reprints and lookups.</summary>
    public required string Barcode { get; init; }

    /// <summary>Name snapshot at the time of sale.</summary>
    public required string Name { get; init; }

    /// <summary>Tax rate snapshot at the time of sale.</summary>
    public required TaxRate TaxRate { get; init; }

    /// <summary>Price per unit at the time of sale, in the store's tax mode.</summary>
    public required Money UnitPrice { get; init; }

    private decimal _quantity = 1m;

    /// <summary>
    /// Number of units. Decimal rather than integer so weighed goods (0.734 kg)
    /// are representable without a separate line type.
    /// </summary>
    public decimal Quantity
    {
        get => _quantity;
        set
        {
            if (value <= 0m)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), "Quantity must be greater than zero. Void the line instead.");
            }

            _quantity = value;
        }
    }

    /// <summary>Discount applied to this line specifically.</summary>
    public Discount? LineDiscount { get; set; }

    /// <summary>Human-readable note printed on the line, e.g. a modifier.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// Station that prepares this line, copied from the product when it was added.
    /// </summary>
    /// <remarks>
    /// Copied rather than looked up at checkout, because the ticket must route the way the product
    /// was configured when it was sold. A product moved from the kitchen to the bar next week must
    /// not silently change where a ticket for today's order goes.
    /// </remarks>
    public string? StationId { get; set; }

    /// <summary>Quantity times unit price, rounded to minor units.</summary>
    public decimal ExtendedAmount => Round(Quantity * UnitPrice.Amount);

    /// <summary>Monetary value of the line-level discount.</summary>
    public decimal LineDiscountAmount =>
        LineDiscount is { } discount ? Round(discount.AmountFor(ExtendedAmount)) : 0m;

    /// <summary>Line total after any line-level discount, before order-level allocation.</summary>
    public decimal NetAmount => Round(ExtendedAmount - LineDiscountAmount);

    /// <summary>Whether this line can carry any further discount.</summary>
    public bool IsDiscountable => NetAmount > 0m;

    /// <summary>
    /// Rounds a monetary amount to the currency's minor units.
    /// </summary>
    /// <remarks>
    /// Public because every part of the system that computes a monetary figure must round it
    /// identically; two different rounding rules would eventually disagree on a receipt.
    /// </remarks>
    public static decimal Round(decimal value) =>
        Math.Round(value, TaxEngine.DecimalPlaces, MidpointRounding.ToEven);
}
