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
/// A sellable item at a store.
/// </summary>
/// <remarks>
/// Kept intentionally small. Variants (size/colour matrices), units of measure, and supplier
/// links arrive with the inventory work — but <see cref="StoreId"/> and <see cref="TaxRate"/>
/// are present now because both are expensive to backfill.
/// </remarks>
public sealed class Product
{
    public required ProductId Id { get; init; }

    /// <summary>Owning store. Multi-store catalogues share products by convention, not by row.</summary>
    public required StoreId StoreId { get; init; }

    /// <summary>Primary barcode (EAN-13, UPC-A, Code128). Scanned at the till.</summary>
    public required string Barcode { get; set; }

    /// <summary>Display name, also printed on the receipt line.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Internal stock-keeping code, or null.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="Barcode"/>, and not a substitute for it. A shop's own
    /// code is stable across suppliers and repackaging — a case of 24 and a single unit can share one
    /// SKU with different barcodes — whereas the barcode changes when the manufacturer does. It is
    /// also the column a bookkeeper recognises on a stock sheet, which is why it is carried through
    /// to the shelf label and the CSV export rather than being till-internal.
    /// </remarks>
    public string? Sku { get; set; }

    /// <summary>
    /// Grouping for reporting and navigation, e.g. "Beverages" or "Chilled / Dairy".
    /// </summary>
    /// <remarks>
    /// Free text rather than a category table. A franchise's own product hierarchy is a back-office
    /// concern with its own lifecycle; the till needs a label to group and filter by, and inventing a
    /// category entity here would mean maintaining it in two places.
    /// </remarks>
    public string? Category { get; set; }

    /// <summary>
    /// Unit price. Per <see cref="Store.TaxMode"/>, this either includes or
    /// excludes tax — the price is stored exactly as it appears on the shelf.
    /// </summary>
    public Money UnitPrice { get; set; }

    /// <summary>Tax rate for this item. Overrides the store default.</summary>
    public TaxRate TaxRate { get; set; } = TaxRate.Zero("Tax");

    /// <summary>Whether the price is entered by the operator at the till (e.g. loose produce, services).</summary>
    public bool IsOpenPrice { get; set; }

    /// <summary>Whether the item is sold by weight rather than as discrete units.</summary>
    public bool IsSoldByWeight { get; set; }

    /// <summary>
    /// Whether this shop counts this item's stock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A decision, not an observation. The catalogue already distinguishes "no movements yet" from
    /// "zero on the shelf", and that is not the same question as whether the item should be counted
    /// at all: a service, a carrier bag, or a newspaper returns to no shelf to be counted, and a stock
    /// report that chased them would be noise an owner learns to ignore.
    /// </para>
    /// <para>
    /// Defaults to true, because the ordinary article in a shop is one you count. Movement is still
    /// recorded either way — the flag decides what is <em>reported</em>, never what is written, so
    /// turning it on later reveals the history rather than starting from nothing.
    /// </para>
    /// </remarks>
    public bool TracksStock { get; set; } = true;

    /// <summary>
    /// Quantity at which the item should be reordered, or null when no reorder point is set.
    /// </summary>
    /// <remarks>
    /// Per store, because the same article sells at different rates in different branches: a level
    /// that is a fortnight's cover in one shop is a day's in another. Null means nobody has decided,
    /// which is honestly different from a decision to reorder at zero.
    /// </remarks>
    public decimal? ReorderLevel { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Station that prepares this product, or null when it is handed over at the till.
    /// </summary>
    /// <remarks>
    /// Null is the ordinary case for anything off a shelf. A product only needs a station when
    /// somebody has to make it, and assigning one to a bottled drink would send work to a kitchen
    /// printer that has nothing to do — which is how a kitchen learns to ignore tickets.
    /// </remarks>
    public string? StationId { get; set; }
}

/// <summary>Strongly-typed product identifier.</summary>
public readonly record struct ProductId(Guid Value)
{
    public static ProductId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
