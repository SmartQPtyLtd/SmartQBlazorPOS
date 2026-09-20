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
using Microsoft.EntityFrameworkCore;
using Pos.Core.Reporting;
using Pos.Infrastructure.Sync;
using Pos.Sync.Server.Auth;
using Pos.Sync.Server.Data;

namespace Pos.Sync.Server.Endpoints;

/// <summary>A product as head office publishes it to a store.</summary>
/// <param name="Id">Product id. Stable across republications of the same product.</param>
/// <param name="Barcode">Scannable code.</param>
/// <param name="Name">Display name.</param>
/// <param name="UnitPrice">Shelf price.</param>
/// <param name="TaxName">Tax label, e.g. "VAT".</param>
/// <param name="TaxRate">Fractional rate, e.g. 0.15.</param>
/// <param name="Sku">Optional internal code.</param>
/// <param name="IsActive">Whether the product is on sale.</param>
/// <param name="IsDeleted">Whether head office has withdrawn it permanently.</param>
/// <param name="IsOpenPrice">Whether the price is entered at the till.</param>
/// <param name="IsSoldByWeight">Whether the quantity is a weight rather than a count.</param>
/// <param name="StationId">
/// Station that prepares the product, or null when it is handed over at the till. Published because
/// it is head-office data like the price: every till in a store must route the same product to the
/// same place, and a station set per terminal would send one order to two different printers.
/// </param>
/// <param name="Category">Grouping for reporting and filtering.</param>
/// <param name="TracksStock">Whether the shop counts this item's stock.</param>
/// <param name="ReorderLevel">
/// Quantity at which the store should reorder. Published as a starting point rather than a rule:
/// a level is per store, and the shop is free to set its own.
/// </param>
public sealed record PublishProductRequest(
    string Id,
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
    decimal? ReorderLevel = null);

/// <summary>Request to publish a catalogue to one store.</summary>
/// <param name="Products">The products to publish. Upserts by id.</param>
public sealed record PublishCatalogueRequest(IReadOnlyList<PublishProductRequest> Products);

/// <summary>
/// Head-office operations: the store register, the estate report, and catalogue publishing.
/// </summary>
/// <remarks>
/// <para>
/// Every endpoint here requires the head-office token. None of it is reachable with a device
/// credential, and that separation is the point rather than a detail: a device credential lives
/// on a till in a shop that may not be physically secure, which is exactly why its authority is
/// bounded to one store's books. The authority to create stores, mint enrolment codes, and revoke
/// other terminals cannot be reachable from a till at all.
/// </para>
/// <para>
/// The catalogue is the exception that proves the rule: it is <em>published</em> here and
/// <em>pulled</em> by terminals over the ordinary catalog stream. Head office never writes into a
/// terminal, and a terminal never writes the catalogue — single-writer ownership, which is what
/// removes the conflict instead of requiring a merge.
/// </para>
/// </remarks>
public static class HeadOfficeEndpoints
{
    /// <summary>
    /// Longest period one consolidated report may cover.
    /// </summary>
    /// <remarks>
    /// The figures are read by parsing stored payloads, because the hub keeps records
    /// payload-shaped so it can accept a record type it has never heard of. That is the right
    /// trade for sync and the wrong one for an unbounded report, so the range is capped and the
    /// caller is told why rather than being left to wonder why a long report timed out.
    /// </remarks>
    private const int MaxReportDays = 366;

    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void MapHeadOfficeEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/head-office");

        group.MapGet("/stores", ListStoresAsync);
        group.MapGet("/reports/consolidated", ConsolidatedReportAsync);
        group.MapPost("/stores/{storeId}/catalogue", PublishCatalogueAsync);
    }

    /// <summary>The store register: every store in the estate and how many tills each has.</summary>
    private static async Task<IResult> ListStoresAsync(
        HttpContext context,
        SyncDbContext db,
        HeadOfficeCredential credential,
        CancellationToken ct)
    {
        if (HeadOfficeAuthentication.Authorise(context, credential) is { } refusal)
        {
            return refusal;
        }

        var stores = await db.Stores
            .AsNoTracking()
            .OrderBy(s => s.Code)
            .Select(s => new
            {
                storeId = s.Id,
                code = s.Code,
                name = s.Name,
                currency = s.Currency,
                taxMode = s.TaxMode,
                defaultTaxRate = s.DefaultTaxRate,
                isActive = s.IsActive,
                createdAt = s.CreatedAt,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var deviceCounts = await db.Devices
            .AsNoTracking()
            .GroupBy(d => d.StoreId)
            .Select(g => new { StoreId = g.Key, Total = g.Count(), Active = g.Count(d => d.RevokedAt == null) })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byStore = deviceCounts.ToDictionary(d => d.StoreId, d => d, StringComparer.Ordinal);

        return Results.Ok(stores.Select(s =>
        {
            byStore.TryGetValue(s.storeId, out var counts);

            return new
            {
                s.storeId,
                s.code,
                s.name,
                s.currency,
                s.taxMode,
                s.defaultTaxRate,
                s.isActive,
                s.createdAt,
                deviceCount = counts?.Total ?? 0,
                activeDeviceCount = counts?.Active ?? 0,
            };
        }));
    }

    /// <summary>
    /// Takings across every store for a period.
    /// </summary>
    /// <remarks>
    /// Built from the records the hub holds, so it is unaffected by a till that never printed its
    /// Z-report or has not been cashed up. Voided sales are counted and valued separately and
    /// never netted into the takings, matching what a store's own report shows — a franchise total
    /// that disagreed with the shop's own Z-report would be worse than no total at all.
    /// </remarks>
    private static async Task<IResult> ConsolidatedReportAsync(
        HttpContext context,
        SyncDbContext db,
        HeadOfficeCredential credential,
        string? from = null,
        string? to = null,
        CancellationToken ct = default)
    {
        if (HeadOfficeAuthentication.Authorise(context, credential) is { } refusal)
        {
            return refusal;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (!TryParseDate(from, out var first) || !TryParseDate(to, out var last))
        {
            return Results.Problem(
                title: "Invalid period",
                detail: "from and to must both be ISO dates, e.g. 2026-03-25.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        first ??= today;
        last ??= first.Value;

        if (last < first)
        {
            return Results.Problem(
                title: "Invalid period",
                detail: $"The period ends ({last:yyyy-MM-dd}) before it starts ({first:yyyy-MM-dd}).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var days = last.Value.DayNumber - first.Value.DayNumber + 1;
        if (days > MaxReportDays)
        {
            return Results.Problem(
                title: "Period too long",
                detail:
                    $"A consolidated report may cover at most {MaxReportDays} days; " +
                    $"{days} were requested. Request a shorter period, or read per-store reports.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var stores = await db.Stores
            .AsNoTracking()
            .OrderBy(s => s.Code)
            .Select(s => new { s.Id, s.Code, s.Name })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var facts = await LoadFactsAsync(db, first.Value, last.Value, ct).ConfigureAwait(false);

        var report = ConsolidatedReportBuilder.Build(
            first.Value,
            last.Value,
            stores.Select(s => (s.Id, s.Code, s.Name)),
            facts,
            DateTimeOffset.UtcNow);

        return Results.Ok(report);
    }

    /// <summary>
    /// Publishes a catalogue to one store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written to the change log on the catalog stream, which is how head-office data reaches a
    /// terminal: the terminal pulls and upserts. Nothing is pushed at a terminal, so a shop that is
    /// offline simply receives the catalogue when it next connects, and a shop that is online
    /// receives it the same way. There is no separate "online" code path to get wrong.
    /// </para>
    /// <para>
    /// Publishing the same product id twice appends a second change. That is deliberate: the
    /// change log is an ordered log of what head office published, so a terminal that missed the
    /// first publication still converges, and the last publication wins by arriving last.
    /// </para>
    /// <para>
    /// Each product is scoped to the requested store. A price list is a per-store artefact in a
    /// franchise, and a global publication would silently overwrite a store's negotiated pricing.
    /// </para>
    /// </remarks>
    private static async Task<IResult> PublishCatalogueAsync(
        string storeId,
        PublishCatalogueRequest request,
        HttpContext context,
        SyncDbContext db,
        HeadOfficeCredential credential,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (HeadOfficeAuthentication.Authorise(context, credential) is { } refusal)
        {
            return refusal;
        }

        var logger = loggerFactory.CreateLogger("HeadOffice.Catalogue");

        if (request.Products.Count == 0)
        {
            return Results.Problem(
                title: "Nothing to publish",
                detail: "The catalogue is empty. Publishing an empty list would change nothing.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var store = await db.Stores
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == storeId, ct)
            .ConfigureAwait(false);

        if (store is null)
        {
            return Results.Problem(
                title: "Unknown store",
                detail: $"No store with id '{storeId}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var rejected = new List<string>();

        foreach (var product in request.Products)
        {
            // Rejected individually rather than failing the batch, so one bad line on a price list
            // does not stop the other four hundred products reaching the shop.
            if (Validate(product) is { } reason)
            {
                rejected.Add($"{product.Id}: {reason}");
            }
        }

        var accepted = request.Products.Where(p => Validate(p) is null).ToArray();

        if (accepted.Length == 0)
        {
            return Results.Problem(
                title: "No publishable products",
                detail: string.Join(" ", rejected),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var now = DateTimeOffset.UtcNow;

        foreach (var product in accepted)
        {
            // Built through the shared payload type rather than an anonymous object, so the shape
            // the hub publishes and the shape a terminal applies are one definition. Two hand-
            // written lists of the same fields is two lists that drift, and a field the applier
            // does not know about is a field the catalogue sync erases from every product.
            var payload = new CatalogProductPayload(
                Id: product.Id,
                StoreId: store.Id,
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

            db.ChangeLog.Add(new ChangeLogEntity
            {
                Stream = SyncStreams.Catalog,
                EntityType = CatalogProductPayload.EntityType,
                EntityId = product.Id,
                StoreId = store.Id,
                Payload = JsonSerializer.Serialize(payload, PayloadJson),
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        ServerLog.CataloguePublished(logger, store.Code, accepted.Length, rejected.Count);

        return Results.Ok(new
        {
            storeId = store.Id,
            storeCode = store.Code,
            published = accepted.Length,
            rejected,
        });
    }

    /// <summary>
    /// Checks a product before it is published.
    /// </summary>
    /// <remarks>
    /// A catalogue line that reaches a till with no barcode or a negative price becomes a product
    /// a cashier cannot sell and a report that does not reconcile. Catching it at publication is
    /// the only point where a person is still looking at the data.
    /// </remarks>
    private static string? Validate(PublishProductRequest product)
    {
        if (string.IsNullOrWhiteSpace(product.Id))
        {
            return "An id is required.";
        }

        if (string.IsNullOrWhiteSpace(product.Barcode))
        {
            return "A barcode is required, or the product cannot be scanned.";
        }

        if (string.IsNullOrWhiteSpace(product.Name))
        {
            return "A name is required.";
        }

        if (product.UnitPrice < 0m)
        {
            return "A price cannot be negative.";
        }

        if (product.TaxRate is < 0m or > 1m)
        {
            return "The tax rate is a fraction between 0 and 1, e.g. 0.15 for 15%.";
        }

        return null;
    }

    /// <summary>
    /// Loads trading facts for a period.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Filtered by the indexed <c>BusinessDate</c> so the scan is bounded by the period rather than
    /// by everything the hub has ever received, then parsed per record because the tax figure and
    /// the void status are not indexed columns. Indexing them would need a schema change, and the
    /// hub creates its schema on first run with no migration tooling — for a shop owner deploying
    /// this, a silent missing column is a worse failure than a slower report.
    /// </para>
    /// <para>
    /// The period is matched against an explicit list of dates rather than a range comparison. A
    /// range would be a culture-sensitive string comparison in the query, and while an ISO date
    /// happens to sort chronologically under any culture, relying on that is the kind of thing
    /// that works until the day it does not. The list is at most <see cref="MaxReportDays"/>
    /// entries, so it is bounded by construction.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<TradingFact>> LoadFactsAsync(
        SyncDbContext db,
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        var dates = new List<string>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            dates.Add(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        var rows = await db.Records
            .AsNoTracking()
            .Where(r => r.BusinessDate != null && dates.Contains(r.BusinessDate))
            .Select(r => new { r.EntityType, r.StoreId, r.Payload })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var facts = new List<TradingFact>(rows.Count);

        foreach (var row in rows)
        {
            // The store comes from the row the hub wrote, never from the payload: a terminal must
            // not be able to attribute its takings to another shop by editing a sale body.
            if (TradingFactReader.Read(row.EntityType, row.StoreId, row.Payload) is { } fact)
            {
                facts.Add(fact);
            }
        }

        return facts;
    }

    private static bool TryParseDate(string? text, out DateOnly? date)
    {
        date = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            date = parsed;
            return true;
        }

        return false;
    }
}
