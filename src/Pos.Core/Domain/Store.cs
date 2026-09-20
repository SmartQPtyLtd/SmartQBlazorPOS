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
/// A physical trading location.
/// </summary>
/// <remarks>
/// <see cref="StoreId"/> is threaded through every store-scoped entity from day
/// one, even when running a single location. Retrofitting tenancy later means
/// touching every query, every table, and every receipt — it is the single most
/// expensive kind of change to make to a live POS.
/// </remarks>
public sealed class Store
{
    public required StoreId Id { get; init; }

    /// <summary>Human-readable store name, printed on receipts.</summary>
    public required string Name { get; set; }

    /// <summary>Short code used on receipts and in reports, e.g. "CT01".</summary>
    public required string Code { get; set; }

    /// <summary>ISO 4217 currency this store prices in.</summary>
    public required string Currency { get; set; }

    /// <summary>
    /// Whether shelf prices at this store include tax. Configured per store
    /// because franchisees can operate under different tax jurisdictions.
    /// </summary>
    public TaxMode TaxMode { get; set; } = TaxMode.Inclusive;

    /// <summary>Default tax rate applied to products with no explicit rate.</summary>
    public TaxRate DefaultTaxRate { get; set; } = TaxRate.Zero("Tax");

    /// <summary>Address lines printed on the receipt header.</summary>
    public List<string> AddressLines { get; init; } = [];

    /// <summary>Tax registration / VAT number printed on receipts where required by law.</summary>
    public string? TaxRegistrationNumber { get; set; }

    /// <summary>Contact phone printed on the receipt footer.</summary>
    public string? Phone { get; set; }

    /// <summary>Free-text footer, e.g. returns policy or a thank-you line.</summary>
    public string ReceiptFooter { get; set; } = "Thank you for your business!";

    /// <summary>
    /// Width of the receipt paper in characters. 32 for 58mm paper, 48 for 80mm.
    /// Drives ESC/POS column layout.
    /// </summary>
    public int ReceiptColumns { get; set; } = 48;

    /// <summary>Whether this store is active. Inactive stores cannot open tills.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Places this store prepares orders, in the order their tickets should be produced.
    /// </summary>
    /// <remarks>
    /// Empty for a shop that prepares nothing, which is the common case for a convenience store
    /// and means no kitchen tickets are produced at all. The list is what makes a ticket say
    /// "BAR" rather than "bar-01", and what fixes the order tickets come out in — a shop that
    /// prints the kitchen before the bar expects that every time.
    /// </remarks>
    public IReadOnlyList<PreparationStation> Stations { get; set; } = [];

    /// <summary>Finds a declared station by id, or null when the store has not declared it.</summary>
    public PreparationStation? FindStation(string? stationId) =>
        string.IsNullOrWhiteSpace(stationId)
            ? null
            : Stations.FirstOrDefault(s =>
                string.Equals(s.Id, stationId, StringComparison.OrdinalIgnoreCase)) is { IsUsable: true } match
                    ? match
                    : null;

    /// <summary>
    /// The name to print for a station id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single rule for turning an id into something a person reads, used by both the router
    /// and the renderer. Having two implementations is how a ticket came to be headed "KITCHEN"
    /// while its lines were routed to "grill" — the paper and the routing disagreed, and the paper
    /// was the one a human trusted.
    /// </para>
    /// <para>
    /// An undeclared id prints as itself in capitals rather than being hidden behind a default.
    /// The product is pointing at a station this store has not declared, which is a configuration
    /// gap worth seeing: silently calling it "KITCHEN" would send a grill item to the kitchen and
    /// make the mistake invisible.
    /// </para>
    /// </remarks>
    public string StationNameFor(string? stationId) =>
        FindStation(stationId)?.Name
        ?? (string.IsNullOrWhiteSpace(stationId) ? string.Empty : stationId.ToUpperInvariant());
}

/// <summary>Strongly-typed store identifier.</summary>
public readonly record struct StoreId(Guid Value)
{
    public static StoreId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
