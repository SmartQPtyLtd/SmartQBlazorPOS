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
/// Allocates sequential, per-store, per-business-date sale numbers.
/// </summary>
/// <remarks>
/// Abstracted rather than implemented here because the concrete strategy differs by
/// deployment: a single terminal can keep a counter in local storage, whereas several
/// terminals sharing a store must coordinate to avoid two sales claiming the same
/// number. The domain should not care which.
/// </remarks>
public interface ISaleNumberSource
{
    /// <summary>
    /// Reserves the next sale number for the given store and business date.
    /// </summary>
    Task<SaleNumber> NextAsync(StoreId storeId, string storeCode, DateOnly businessDate, CancellationToken ct = default);
}

/// <summary>
/// Finalises a cart into an immutable <see cref="Sale"/>.
/// </summary>
/// <remarks>
/// This is the boundary between "a basket the cashier is still editing" and "a
/// financial record". Everything after this point must be immutable, so all
/// validation happens here and the sale is only produced if the tenders balance exactly.
/// </remarks>
public sealed class CheckoutService(ISaleNumberSource saleNumbers, TimeProvider? timeProvider = null)
{
    private readonly ISaleNumberSource _saleNumbers =
        saleNumbers ?? throw new ArgumentNullException(nameof(saleNumbers));

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Completes a cart into a sale, verifying the tenders settle the balance.
    /// </summary>
    /// <param name="cart">The cart to finalise. Must not be empty.</param>
    /// <param name="tenders">Payments applied. Must sum exactly to the cart total.</param>
    /// <param name="storeCode">Store code used to build the sale number.</param>
    /// <param name="employeeId">Optional operator, recorded for audit.</param>
    /// <param name="businessDate">
    /// Business date to attribute the sale to. Defaults to the local date of the
    /// point of sale; pass an explicit value for a till closing after midnight.
    /// </param>
    public async Task<Sale> CompleteSaleAsync(
        Cart cart,
        IReadOnlyList<Tender> tenders,
        string storeCode,
        string? employeeId = null,
        DateOnly? businessDate = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cart);
        ArgumentNullException.ThrowIfNull(tenders);

        if (cart.IsEmpty)
        {
            throw new InvalidOperationException("Cannot complete a sale with an empty cart.");
        }

        var totals = cart.CalculateTotals();
        Sale.ValidateTenders(tenders, totals.Total, cart.Currency);

        var now = _time.GetUtcNow();
        var date = businessDate ?? DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        var number = await _saleNumbers.NextAsync(cart.StoreId, storeCode, date, ct).ConfigureAwait(false);

        var lines = totals.LineTotals
            .Select(lt => new SaleLine(
                ProductId: lt.Line.ProductId,
                Barcode: lt.Line.Barcode,
                Name: lt.Line.Name,
                Quantity: lt.Line.Quantity,
                UnitPrice: lt.Line.UnitPrice,
                TaxRate: lt.Line.TaxRate,
                DiscountAmount: CartLine.Round(lt.Line.LineDiscountAmount + lt.OrderDiscountAllocated),
                TaxableAmount: lt.TaxableAmount,
                TaxAmount: lt.LineTax,

                // Carried through so a kitchen ticket still shows the modifier.
                Note: lt.Line.Note,

                // Carried through for the same reason: the station decides which printer the line
                // is sent to, and a reprint must route it the way the product was configured when
                // it was sold rather than where it sits in the catalogue today.
                StationId: lt.Line.StationId))
            .ToArray();

        return new Sale
        {
            Id = SaleId.New(),
            StoreId = cart.StoreId,
            Number = number,
            CompletedAt = now,
            BusinessDate = date,
            Currency = cart.Currency,
            TaxMode = cart.TaxMode,
            Lines = lines,
            Tenders = [.. tenders],
            Tax = totals.Tax,
            // Sale.Subtotal is the sum of line nets after any line-level discount,
            // matching CartTotals.NetBeforeOrderDiscount. The order-level discount is
            // recorded separately as TotalDiscount and is already reflected in Total.
            Subtotal = totals.NetBeforeOrderDiscount,
            TotalDiscount = totals.TotalDiscounts,
            Total = totals.Total,
            EmployeeId = employeeId,
            CustomerId = cart.CustomerId,
        };
    }
}
