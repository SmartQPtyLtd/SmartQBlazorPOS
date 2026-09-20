// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Globalization;
using System.Text.Json;
using Pos.Core.Domain;
using Pos.Infrastructure.Catalog;
using Pos.Infrastructure.Storage;

namespace Pos.Infrastructure.Checkout;

/// <summary>
/// Manages the catalogue and the stock ledger.
/// </summary>
/// <remarks>
/// <para>
/// Stock changes are always appended to the movement ledger, never written as a level. A level is
/// derived, so the history behind it survives — which is the difference between a number an owner
/// can act on and one they have to take on faith.
/// </para>
/// <para>
/// Catalogue rows are a read-only replica of head-office data. A shop may deactivate a product
/// locally or override its price, but neither mutates the head-office row, so a catalogue pull can
/// never silently discard a local decision.
/// </para>
/// </remarks>
public sealed class StockService(
    ILocalStore store,
    ITerminalIdentity terminal,
    TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILocalStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ITerminalIdentity _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Lists a store's catalogue with derived stock levels.</summary>
    /// <param name="storeId">Store to list.</param>
    /// <param name="search">Optional name or barcode filter.</param>
    /// <param name="includeInactive">Include products withdrawn from sale.</param>
    public async Task<IReadOnlyList<CatalogueItem>> GetCatalogueAsync(
        string storeId,
        string? search = null,
        bool includeInactive = false,
        CancellationToken ct = default)
    {
        var products = await _store
            .GetProductsAsync(storeId, includeInactive, 500, ct)
            .ConfigureAwait(false);

        var movements = await _store
            .GetStockMovementsAsync(storeId, null, 5000, ct)
            .ConfigureAwait(false);

        var levels = DeriveLevels(movements);

        var items = products
            .Where(p => Matches(p, search))
            .Select(p => new CatalogueItem(
                Product: p,
                Stock: levels.TryGetValue(p.Id, out var level)
                    ? level
                    : new StockLevel(p.Id, 0m, 0, null)))
            .ToArray();

        return items;
    }

    /// <summary>
    /// Lists the items this shop should reorder, worst shortfall first.
    /// </summary>
    /// <param name="storeId">Store to check.</param>
    /// <param name="ct">Cancellation.</param>
    /// <remarks>
    /// <para>
    /// The end of the chain the whole stock design exists for: a sale appends a movement, the level is
    /// derived from the movements, and the reorder point is compared against that level. Nothing is
    /// stored in between, so there is no counter that can drift away from the ledger it is supposed to
    /// summarise, and no rebuild step that has to be remembered.
    /// </para>
    /// <para>
    /// Withdrawn products are excluded: an item the shop has stopped selling will never be reordered
    /// however low it runs, and leaving it on the list is how a list stops being read.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<CatalogueItem>> GetReorderListAsync(
        string storeId,
        CancellationToken ct = default)
    {
        var items = await GetCatalogueAsync(storeId, search: null, includeInactive: false, ct)
            .ConfigureAwait(false);

        return items
            .Where(item => !item.IsWithdrawn && item.NeedsReorder)

            // Largest shortfall first, which is the order somebody would actually work down. Ties
            // broken by name so the list does not reshuffle between two identical requests.
            .OrderByDescending(item => item.ReorderShortfall)
            .ThenBy(item => item.Product.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Derives a stock level per product from the movement ledger.
    /// </summary>
    /// <remarks>
    /// Shared with the in-memory store's own derivation so the two cannot disagree about the same
    /// ledger — a level that differed depending on which path computed it would be worse than no
    /// level at all.
    /// </remarks>
    public static Dictionary<string, StockLevel> DeriveLevels(IReadOnlyList<StockMovement> movements)
    {
        ArgumentNullException.ThrowIfNull(movements);

        return movements
            .GroupBy(m => m.ProductId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => new StockLevel(
                    ProductId: g.Key,
                    Quantity: g.Sum(m => m.QtyDelta),
                    MovementCount: g.Count(),
                    LastMovementAt: g
                        .Select(ParseOccurredAt)
                        .Where(at => at is not null)
                        .Max()),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// Records a stock correction after a physical count.
    /// </summary>
    /// <param name="productId">Product counted.</param>
    /// <param name="countedQuantity">What was actually on the shelf.</param>
    /// <param name="employeeName">Who counted it, for the audit trail.</param>
    /// <param name="reason">Why the level is being corrected.</param>
    /// <remarks>
    /// Records the <b>difference</b> between the count and the derived level, not the count itself.
    /// Writing the count as a movement would make the resulting level correct only if the previous
    /// level were exactly right — which is the thing a count exists to question.
    /// </remarks>
    public async Task<StockMovement> RecordCountAsync(
        string productId,
        decimal countedQuantity,
        string employeeName,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);

        if (countedQuantity < 0m)
        {
            throw new InvalidOperationException("A counted quantity cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidOperationException("A reason is required for a stock correction.");
        }

        var product = await _store.GetProductAsync(productId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("That product is not on this terminal.");

        var movements = await _store
            .GetStockMovementsAsync(product.StoreId, productId, 5000, ct)
            .ConfigureAwait(false);

        var current = movements.Sum(m => m.QtyDelta);
        var delta = CartLine.Round(countedQuantity - current);

        if (delta == 0m)
        {
            // Nothing to record. An empty movement would clutter the ledger and make a level look
            // better-evidenced than it is.
            throw new InvalidOperationException(
                $"The count of {countedQuantity:0.###} matches the recorded level, so there is nothing to correct.");
        }

        return await AppendAsync(
            product,
            delta,
            StockMovementReason.Adjustment,
            $"{reason} (counted by {employeeName})",
            ct).ConfigureAwait(false);
    }

    /// <summary>Records goods received from a supplier.</summary>
    public async Task<StockMovement> RecordGoodsReceiptAsync(
        string productId,
        decimal quantity,
        string reference,
        CancellationToken ct = default)
    {
        if (quantity <= 0m)
        {
            throw new InvalidOperationException("A goods receipt must be greater than zero.");
        }

        var product = await _store.GetProductAsync(productId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("That product is not on this terminal.");

        return await AppendAsync(
            product,
            quantity,
            StockMovementReason.GoodsReceipt,
            string.IsNullOrWhiteSpace(reference) ? "Goods received" : $"Goods received: {reference}",
            ct).ConfigureAwait(false);
    }

    /// <summary>Writes stock off as damaged, spoiled, or stolen.</summary>
    public async Task<StockMovement> RecordShrinkageAsync(
        string productId,
        decimal quantity,
        string reason,
        CancellationToken ct = default)
    {
        if (quantity <= 0m)
        {
            throw new InvalidOperationException("A write-off must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidOperationException("A reason is required for a write-off.");
        }

        var product = await _store.GetProductAsync(productId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("That product is not on this terminal.");

        // Negative: the goods are leaving.
        return await AppendAsync(
            product,
            -quantity,
            StockMovementReason.Shrinkage,
            reason,
            ct).ConfigureAwait(false);
    }

    /// <summary>The movement history for a product, most recent first.</summary>
    public Task<IReadOnlyList<StockMovement>> GetHistoryAsync(
        string storeId,
        string productId,
        CancellationToken ct = default) =>
        _store.GetStockMovementsAsync(storeId, productId, 200, ct);

    /// <summary>Updates the locally held details of a product.</summary>
    /// <remarks>
    /// Only the fields a shop owns are writable. The head-office row is a replica, so changing a
    /// price here records a local decision rather than mutating the published catalogue.
    /// </remarks>
    /// <remarks>
    /// <para>
    /// Takes the whole set of editable values rather than a list of optional overrides. The previous
    /// shape could not tell "leave the station alone" from "clear the station", because both are null —
    /// so a caller that omitted it silently stopped a product's kitchen tickets. Every field here is
    /// what the caller wants the product to be afterwards, which is what a screen with an edit form
    /// already knows.
    /// </para>
    /// <para>
    /// Head-office-owned fields are deliberately not editable: the barcode, tax rate and identity come
    /// from the catalogue, and a local edit to any of them would be discarded by the next sync —
    /// silently, and after the shop had come to rely on it.
    /// </para>
    /// </remarks>
    public async Task UpdateLocalProductAsync(
        StoredProduct product,
        ProductEdit edit,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(product);

        if (string.IsNullOrWhiteSpace(edit.Name))
        {
            throw new InvalidOperationException("A product needs a name.");
        }

        if (edit.UnitPrice < 0m)
        {
            throw new InvalidOperationException("A price cannot be negative.");
        }

        if (edit.ReorderLevel is { } level && level < 0m)
        {
            throw new InvalidOperationException("A reorder level cannot be negative.");
        }

        var updated = product with
        {
            Name = edit.Name.Trim(),
            UnitPrice = edit.UnitPrice,
            IsActive = edit.IsActive,

            // Blank clears the station, which is how a shop stops sending a product to a printer it no
            // longer needs to.
            StationId = string.IsNullOrWhiteSpace(edit.StationId) ? null : edit.StationId.Trim(),
            Sku = string.IsNullOrWhiteSpace(edit.Sku) ? null : edit.Sku.Trim(),
            Category = string.IsNullOrWhiteSpace(edit.Category) ? null : edit.Category.Trim(),
            TracksStock = edit.TracksStock,
            ReorderLevel = edit.ReorderLevel,
        };

        await _store.UpsertProductsAsync([updated], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes this store's catalogue as CSV.
    /// </summary>
    /// <param name="storeId">Store to export.</param>
    /// <param name="ct">Cancellation.</param>
    /// <remarks>
    /// Withdrawn products are included. The export doubles as the template for an import and as the
    /// shop's own backup of what it sells, and a file that silently dropped the articles somebody had
    /// just withdrawn would come back as a catalogue that re-listed them.
    /// </remarks>
    public async Task<string> ExportCsvAsync(string storeId, CancellationToken ct = default)
    {
        var products = await _store
            .GetProductsAsync(storeId, includeInactive: true, limit: 5000, ct)
            .ConfigureAwait(false);

        return CatalogCsv.Write(products.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads a catalogue file into this store's replica.
    /// </summary>
    /// <param name="storeId">Store the rows belong to.</param>
    /// <param name="text">The file's contents.</param>
    /// <param name="ct">Cancellation.</param>
    /// <remarks>
    /// <para>
    /// Matched on barcode, so re-importing an exported file updates the articles it names rather than
    /// creating a second copy of each — which a till would then happily sell twice under two ids.
    /// </para>
    /// <para>
    /// These rows belong to this terminal. A catalogue published by head office is authoritative and
    /// arrives over the catalog stream; an import here changes what this shop sells without telling
    /// anybody, and the screen says so rather than implying the estate has been updated.
    /// </para>
    /// </remarks>
    public async Task<CatalogImportOutcome> ImportCsvAsync(
        string storeId,
        string text,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(storeId);

        var existing = await _store
            .GetProductsAsync(storeId, includeInactive: true, limit: 5000, ct)
            .ConfigureAwait(false);

        var byId = existing.ToDictionary(p => p.Id, StringComparer.Ordinal);

        var result = CatalogCsv.Read(text, storeId, byId);

        if (result.Rows.Count == 0)
        {
            return new CatalogImportOutcome(0, 0, 0, result.Errors);
        }

        var products = result.Rows.Select(r => r.Product).ToArray();

        await _store.UpsertProductsAsync(products, ct).ConfigureAwait(false);

        var updated = products.Count(p => byId.ContainsKey(p.Id));

        return new CatalogImportOutcome(
            Created: products.Length - updated,
            Updated: updated,
            Skipped: result.Errors.Count,
            Errors: result.Errors);
    }

    private async Task<StockMovement> AppendAsync(
        StoredProduct product,
        decimal delta,
        StockMovementReason reason,
        string reference,
        CancellationToken ct)
    {
        // Reserved from the terminal's single persisted counter, the same one sales and drawer
        // events draw from, so no two records can claim one position in the stream.
        var terminalSeq = await _store.ReserveTerminalSequenceAsync(1, ct).ConfigureAwait(false);

        var movement = new StockMovement
        {
            Id = Guid.CreateVersion7().ToString("N"),
            StoreId = product.StoreId,
            TerminalId = _terminal.TerminalId,
            TerminalSeq = terminalSeq,
            ProductId = product.Id,
            QtyDelta = delta,
            Reason = reason.ToString(),
            Reference = reference,
            OccurredAt = _time.GetUtcNow()
                .ToString("O", CultureInfo.InvariantCulture),
        };

        await _store
            .RecordStockMovementAsync(
                movement,
                JsonSerializer.Serialize(movement, JsonOptions),
                _terminal.TerminalId,
                ct)
            .ConfigureAwait(false);

        return movement;
    }

    private static bool Matches(StoredProduct product, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var needle = search.Trim();

        return product.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || product.Barcode.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || (product.Sku?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static DateTimeOffset? ParseOccurredAt(StockMovement movement) =>
        DateTimeOffset.TryParse(
            movement.OccurredAt, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var at)
                ? at
                : null;
}

/// <summary>
/// What a catalogue import did.
/// </summary>
/// <param name="Created">Articles the file added.</param>
/// <param name="Updated">Articles the file changed, matched by barcode.</param>
/// <param name="Skipped">Rows that could not be read.</param>
/// <param name="Errors">Why each skipped row was skipped, with its line number.</param>
public sealed record CatalogImportOutcome(
    int Created,
    int Updated,
    int Skipped,
    IReadOnlyList<CatalogImportError> Errors)
{
    /// <summary>True when the file held nothing that could be imported.</summary>
    public bool IsEmpty => Created == 0 && Updated == 0;
}

/// <summary>
/// The values a shop may change about a product locally.
/// </summary>
/// <remarks>
/// Every field is the value the caller wants the product to have afterwards, not an override that may
/// be absent. That distinction is the point of the type: the previous signature could not express
/// "clear the station" and "leave the station" as different requests, so one of them silently meant the
/// other.
/// </remarks>
/// <param name="Name">Display name. Required.</param>
/// <param name="UnitPrice">Shelf price, stored as it appears on the shelf.</param>
/// <param name="IsActive">Whether this shop sells it.</param>
/// <param name="StationId">Station that prepares it, or null to send it nowhere.</param>
/// <param name="Sku">Internal code, or null.</param>
/// <param name="Category">Grouping, or null.</param>
/// <param name="TracksStock">Whether this shop counts its stock.</param>
/// <param name="ReorderLevel">Quantity at which to reorder, or null for no reorder point.</param>
public readonly record struct ProductEdit(
    string Name,
    decimal UnitPrice,
    bool IsActive,
    string? StationId = null,
    string? Sku = null,
    string? Category = null,
    bool TracksStock = true,
    decimal? ReorderLevel = null);

/// <summary>A catalogue row with its derived stock level.</summary>
/// <param name="Product">The product as held locally.</param>
/// <param name="Stock">Level derived from the movement ledger.</param>
public readonly record struct CatalogueItem(StoredProduct Product, StockLevel Stock)
{
    /// <summary>True when the product is withdrawn from sale in this shop.</summary>
    public bool IsWithdrawn => !Product.IsActive || Product.IsDeleted;

    /// <summary>
    /// True when this item is at or below the point at which somebody said to reorder it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three separate conditions, and each one is a question somebody would otherwise get wrong:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>The item is counted at all.</b> A service or a carrier bag returning to no shelf would
    /// otherwise sit at the top of a reorder list forever.
    /// </description></item>
    /// <item><description>
    /// <b>Somebody set a reorder point.</b> Null means nobody has decided, and treating that as zero
    /// would put every new article on the list the moment it sold out.
    /// </description></item>
    /// <item><description>
    /// <b>The level is actually known.</b> A product with no movements has never been counted; a
    /// derived level of zero there is the absence of information, not an empty shelf, and reordering
    /// against it is how a shop ends up with a pallet of something it already had.
    /// </description></item>
    /// </list>
    /// <para>
    /// Derived from the ledger like every other figure here, never from a stored "on order" flag. That
    /// is what makes it impossible to drift: there is no counter to fall out of step with the
    /// movements that produced it.
    /// </para>
    /// </remarks>
    public bool NeedsReorder =>
        Product.TracksStock
        && Product.ReorderLevel is { } level
        && !Stock.HasNoHistory
        && Stock.Quantity <= level;

    /// <summary>
    /// How many units to order to reach the reorder point, or zero when nothing is due.
    /// </summary>
    /// <remarks>
    /// The shortfall against the reorder level rather than against an ideal stock holding, because the
    /// only number the shop has actually decided is the reorder point. Inventing a target would be
    /// this code making a commercial decision on the owner's behalf.
    /// </remarks>
    public decimal ReorderShortfall =>
        NeedsReorder && Product.ReorderLevel is { } level
            ? Math.Max(0m, level - Stock.Quantity)
            : 0m;
}
