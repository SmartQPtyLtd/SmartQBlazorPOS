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

namespace Pos.Sync.Server.Endpoints;

/// <summary>
/// Extracts reportable columns from a synced payload without binding it to a concrete type.
/// </summary>
/// <remarks>
/// <para>
/// Keeping the change log payload-shaped means the sync protocol stays stable as record
/// types are added: a terminal running an older build can push a type the hub has never
/// heard of and it is still stored, still synced, and still recoverable.
/// </para>
/// <para>
/// Parsing is deliberately <b>tolerant</b>. A payload the hub cannot read is stored anyway
/// and simply contributes no reportable columns. Rejecting it would strand the record on
/// the terminal forever, which is a far worse outcome than a sale that cannot be indexed
/// until it is looked at by hand.
/// </para>
/// </remarks>
public static class SyncPayloadIndexer
{
    /// <summary>Record type that carries a business date and a total.</summary>
    public const string SaleEntityType = "sale";

    /// <summary>Record type a refund is pushed as.</summary>
    /// <remarks>
    /// Refunds carry their money in different fields from a sale — <c>totalRefund</c> and
    /// <c>taxReversed</c> rather than <c>total</c> and <c>taxTotal</c>. Skipping them here would
    /// leave every refund stored with no business date, and a head-office report that silently
    /// omitted every refund is worse than one that shows none, because it looks correct.
    /// </remarks>
    public const string ReturnEntityType = "salesReturn";

    /// <summary>
    /// Reads the indexable fields from a payload.
    /// </summary>
    /// <returns>
    /// The business date and amount where present; both null for any other record type or
    /// when the payload cannot be parsed.
    /// </returns>
    public static (string? BusinessDate, decimal? Total) Extract(string entityType, string payload)
    {
        var isSale = string.Equals(entityType, SaleEntityType, StringComparison.Ordinal);
        var isReturn = string.Equals(entityType, ReturnEntityType, StringComparison.Ordinal);

        if (!isSale && !isReturn)
        {
            return (null, null);
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var businessDate = ReadString(root, "businessDate");

            // A refund's amount is what was handed back, under its own name. Reading `total` for a
            // refund would index zero and quietly drop it from every total.
            var total = isReturn
                ? ReadDecimal(root, "totalRefund")
                : ReadDecimal(root, "total");

            return (businessDate, total);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static decimal? ReadDecimal(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        // Amounts may arrive as a JSON number or, from some serialisers, as a string.
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String &&
            decimal.TryParse(property.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var text))
        {
            return text;
        }

        return null;
    }
}
