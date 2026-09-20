// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Infrastructure.Storage;

namespace Pos.Infrastructure.Sync;

/// <summary>
/// A catalogue product, as head office publishes it and as a terminal applies it.
/// </summary>
/// <remarks>
/// <para>
/// One type shared by both ends of the wire, rather than the hub building an anonymous object and
/// the terminal keeping its own payload record. Two lists of the same fields is two lists that
/// drift, and the drift is silent: a field the applier does not know about is a field the
/// catalogue sync erases from every product in the shop.
/// </para>
/// <para>
/// That is not hypothetical. The preparation station was added to the stored product for the
/// kitchen printer, and the applier — listing fields by hand, written before the station existed —
/// dropped it. No error, no warning: the next catalogue sync would have stripped the station from
/// every product, and the kitchen would simply have stopped receiving tickets.
/// </para>
/// <para>
/// Being a payload rather than <see cref="StoredProduct"/> itself is deliberate. The hub does not
/// own the terminal's storage shape: it publishes a contract, and the terminal decides how to keep
/// its own replica. What travels is this, and only this.
/// </para>
/// </remarks>
/// <param name="Id">Product identity, stable across republications.</param>
/// <param name="StoreId">Store the product belongs to.</param>
/// <param name="Barcode">Scannable code.</param>
/// <param name="Name">Display name.</param>
/// <param name="UnitPrice">Shelf price.</param>
/// <param name="TaxName">Tax label, e.g. "VAT".</param>
/// <param name="TaxRate">Fractional rate, e.g. 0.15.</param>
/// <param name="Sku">Optional internal code.</param>
/// <param name="IsActive">Whether head office has it on sale.</param>
/// <param name="IsDeleted">Whether head office has withdrawn it permanently.</param>
/// <param name="IsOpenPrice">Whether the price is entered at the till.</param>
/// <param name="IsSoldByWeight">Whether the quantity is a weight rather than a count.</param>
/// <param name="StationId">Station that prepares it, or null when it is handed over at the till.</param>
/// <param name="Category">Grouping for reporting and filtering, e.g. "Beverages".</param>
/// <param name="TracksStock">Whether this shop counts the item's stock.</param>
/// <param name="ReorderLevel">Quantity at which to reorder, or null when none is set.</param>
public sealed record CatalogProductPayload(
    string Id,
    string StoreId,
    string Barcode,
    string Name,
    decimal UnitPrice,
    string TaxName,
    decimal TaxRate,
    string? Sku = null,
    bool IsActive = true,
    bool IsDeleted = false,
    bool IsOpenPrice = false,
    bool IsSoldByWeight = false,
    string? StationId = null,
    string? Category = null,
    bool TracksStock = true,
    decimal? ReorderLevel = null)
{
    /// <summary>
    /// Record type a catalogue product travels under.
    /// </summary>
    /// <remarks>
    /// Beside the payload rather than on either end of the wire, because both ends have to agree
    /// on it and a constant declared twice is a constant that can be changed once. A change-log
    /// row written under a different name is a row no terminal asks for.
    /// </remarks>
    public const string EntityType = "product";

    /// <summary>Builds the wire shape from a terminal's stored product.</summary>
    public static CatalogProductPayload FromStored(StoredProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);

        return new CatalogProductPayload(
            Id: product.Id,
            StoreId: product.StoreId,
            Barcode: product.Barcode,
            Name: product.Name,
            UnitPrice: product.UnitPrice,
            TaxName: product.TaxName,
            TaxRate: product.TaxRate,
            Sku: product.Sku,
            IsActive: product.IsActive,
            IsDeleted: product.IsDeleted,
            IsOpenPrice: product.IsOpenPrice,
            IsSoldByWeight: product.IsSoldByWeight,
            StationId: product.StationId,
            Category: product.Category,
            TracksStock: product.TracksStock,
            ReorderLevel: product.ReorderLevel);
    }

    /// <summary>
    /// Projects the wire shape onto the terminal's stored product.
    /// </summary>
    /// <remarks>
    /// Every field, because the applier replaces the local row wholesale. A property added to
    /// <see cref="StoredProduct"/> and not carried here is erased on the next catalogue sync —
    /// which is exactly how the preparation station was lost.
    /// </remarks>
    public StoredProduct ToStored() => new()
    {
        Id = Id,
        StoreId = StoreId,
        Barcode = Barcode,
        Name = Name,
        UnitPrice = UnitPrice,
        TaxName = TaxName,
        TaxRate = TaxRate,
        Sku = Sku,
        IsActive = IsActive,
        IsDeleted = IsDeleted,
        IsOpenPrice = IsOpenPrice,
        IsSoldByWeight = IsSoldByWeight,
        StationId = StationId,
        Category = Category,
        TracksStock = TracksStock,
        ReorderLevel = ReorderLevel,
    };
}
