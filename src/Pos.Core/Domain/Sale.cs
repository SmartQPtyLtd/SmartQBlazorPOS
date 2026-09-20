// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

namespace Pos.Core.Domain;

/// <summary>Lifecycle state of a sale.</summary>
public enum SaleStatus
{
    /// <summary>Completed and paid. The normal terminal state.</summary>
    Completed = 0,

    /// <summary>Fully reversed. The sale and its tenders remain on record for audit.</summary>
    Voided = 1,

    /// <summary>Partially or fully returned via a later refund.</summary>
    Refunded = 2,
}

/// <summary>
/// An immutable snapshot of a completed sale.
/// </summary>
/// <remarks>
/// <para>
/// The sale copies the product name, tax rate, and unit price onto each line rather
/// than referencing the catalogue. This is essential: if a price or VAT rate changes
/// next month, a reprint of today's receipt must still show what the customer
/// actually paid. Historic financial records must never be recomputed from current data.
/// </para>
/// <para>
/// Voiding sets <see cref="Status"/> rather than deleting. A deleted sale is a
/// vanished audit trail, and a POS without an audit trail cannot be reconciled.
/// </para>
/// </remarks>
public sealed class Sale
{
    public required SaleId Id { get; init; }

    public required StoreId StoreId { get; init; }

    public required SaleNumber Number { get; init; }

    /// <summary>When the sale was finalised, in UTC.</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>
    /// Business date the sale is attributed to. Held separately from
    /// <see cref="CompletedAt"/> because a till closing after midnight must still
    /// report against the trading day it belongs to.
    /// </summary>
    public required DateOnly BusinessDate { get; init; }

    /// <summary>Currency of every amount on this sale.</summary>
    public required string Currency { get; init; }

    /// <summary>Tax mode in force at the time of sale.</summary>
    public required TaxMode TaxMode { get; init; }

    public required IReadOnlyList<SaleLine> Lines { get; init; }

    /// <summary>Payments applied. May be several (split tender).</summary>
    public required IReadOnlyList<Tender> Tenders { get; init; }

    /// <summary>The tax breakdown actually charged, frozen at the time of sale.</summary>
    public required TaxCalculation Tax { get; init; }

    /// <summary>Sum of line nets before any order-level discount.</summary>
    public required decimal Subtotal { get; init; }

    /// <summary>Total discount given across the sale.</summary>
    public required decimal TotalDiscount { get; init; }

    /// <summary>The amount the customer paid.</summary>
    public required decimal Total { get; init; }

    /// <summary>Optional employee who rang the sale, for audit and commission.</summary>
    public string? EmployeeId { get; init; }

    /// <summary>Optional customer reference.</summary>
    public string? CustomerId { get; init; }

    public SaleStatus Status { get; set; } = SaleStatus.Completed;

    /// <summary>Reason recorded when a sale is voided. Required for audit.</summary>
    public string? VoidReason { get; set; }

    /// <summary>Total received across all tenders.</summary>
    public decimal AmountTendered => Tenders.Sum(t => t.Amount.Amount);

    /// <summary>
    /// Change returned to the customer, summed across cash tenders.
    /// </summary>
    public decimal ChangeGiven => Tenders.Sum(t => t.ChangeDue(Currency).Amount);

    /// <summary>Total unit count, for the receipt item count line.</summary>
    public decimal TotalQuantity => Lines.Sum(l => l.Quantity);

    public bool IsVoided => Status == SaleStatus.Voided;

    /// <summary>
    /// Applies tenders to the sale, enforcing that the balance is settled exactly.
    /// </summary>
    /// <remarks>
    /// Over-tendering is rejected rather than silently accepted, because the excess
    /// would have to become change — and change is only meaningful for cash. If a
    /// caller wants to hand back money on a card tender, that is a refund, not a sale.
    /// </remarks>
    public static void ValidateTenders(IReadOnlyList<Tender> tenders, decimal total, string currency)
    {
        ArgumentNullException.ThrowIfNull(tenders);

        if (tenders.Count == 0)
        {
            throw new InvalidOperationException("A sale must have at least one tender.");
        }

        var applied = tenders.Sum(t => t.Amount.Amount);

        // Compare at minor-unit precision: 0.1 + 0.2 style drift must not block a sale.
        var difference = CartLine.Round(applied - total);
        if (difference != 0m)
        {
            throw new InvalidOperationException(
                $"Tenders total {applied:0.00} {currency} but the sale total is {total:0.00} {currency}.");
        }

        foreach (var tender in tenders)
        {
            if (tender.Amount.IsNegative)
            {
                throw new InvalidOperationException("A tender cannot be negative.");
            }

            if (tender.Type == TenderType.Cash && !tender.IsSufficient)
            {
                throw new InvalidOperationException(
                    $"Cash tendered {tender.Tendered} is less than the amount applied {tender.Amount}.");
            }
        }
    }
}

/// <summary>
/// An immutable snapshot of one sold line.
/// </summary>
/// <param name="ProductId">Product sold.</param>
/// <param name="Barcode">Barcode as scanned.</param>
/// <param name="Name">Product name at the time of sale.</param>
/// <param name="Quantity">Units sold.</param>
/// <param name="UnitPrice">Unit price charged.</param>
/// <param name="TaxRate">Tax rate applied.</param>
/// <param name="DiscountAmount">Total discount applied to this line, including its share of any order discount.</param>
/// <param name="TaxableAmount">Post-discount amount that tax was assessed on.</param>
/// <param name="TaxAmount">Tax attributable to this line.</param>
/// <param name="Note">
/// Free-text modifier such as "No onions" or "Extra shot".
/// </param>
/// <param name="StationId">
/// Station that prepares this line, or null when it is handed over at the till. Carried onto the
/// sale so a kitchen reprint routes to the same place it did the first time.
/// </param>
/// <remarks>
/// The note is carried onto the sale rather than staying on the cart because it is an
/// instruction to whoever prepares the order, not a remark about the transaction. Dropping it
/// at checkout would mean the kitchen ticket silently loses the modifier.
/// </remarks>
public readonly record struct SaleLine(
    ProductId ProductId,
    string Barcode,
    string Name,
    decimal Quantity,
    Money UnitPrice,
    TaxRate TaxRate,
    decimal DiscountAmount,
    decimal TaxableAmount,
    decimal TaxAmount,
    string? Note = null,
    string? StationId = null)
{
    /// <summary>Line total before discounts.</summary>
    public decimal ExtendedAmount => CartLine.Round(Quantity * UnitPrice.Amount);

    /// <summary>What this line contributed to the sale total.</summary>
    public decimal GrossAmount => TaxableAmount;
}
