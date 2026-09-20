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
using Pos.Core.Reporting;

namespace Pos.Sync.Server.Endpoints;

/// <summary>
/// Reads a synced payload into the flat shape head-office reporting needs.
/// </summary>
/// <remarks>
/// <para>
/// This is where the hub's promise to be payload-shaped is cashed in. The hub stores records it
/// does not own the schema of, so a report cannot bind them to a domain type — a terminal running
/// an older build is expected to push sales the hub has never seen. Reading them tolerantly here
/// keeps that promise while still producing real figures.
/// </para>
/// <para>
/// Parsing is deliberately forgiving and returns <c>null</c> rather than throwing. A record whose
/// payload the hub cannot read still counts as stored and still syncs; it simply contributes
/// nothing to a report. Refusing to store it would strand a real sale on the terminal forever,
/// which is a far worse outcome than a report that is briefly short by one line.
/// </para>
/// <para>
/// The names read here are the ones <c>StoredSale</c> and <c>StoredReturn</c> serialise to with a
/// camel-case policy, plus the same tolerant string-or-number handling the indexer uses for
/// amounts.
/// </para>
/// </remarks>
public static class TradingFactReader
{
    /// <summary>Record type a completed sale is pushed as.</summary>
    public const string SaleEntityType = "sale";

    /// <summary>Record type a refund is pushed as.</summary>
    public const string ReturnEntityType = "salesReturn";

    /// <summary>Sale status meaning the sale was reversed before money changed hands.</summary>
    private const string VoidedStatus = "Voided";

    /// <summary>
    /// True when this entity type contributes to trading figures.
    /// </summary>
    /// <remarks>
    /// Stock movements, shifts, and drawer events are all pushed through the same stream and none
    /// of them is money taken. Filtering here rather than at the query keeps the rule in one place.
    /// </remarks>
    public static bool IsTradingFact(string? entityType) =>
        string.Equals(entityType, SaleEntityType, StringComparison.Ordinal) ||
        string.Equals(entityType, ReturnEntityType, StringComparison.Ordinal);

    /// <summary>
    /// Reads one payload.
    /// </summary>
    /// <param name="entityType">Record discriminator from the change log.</param>
    /// <param name="storeId">Store the hub recorded the payload under, never one from the payload.</param>
    /// <param name="payload">The serialised record.</param>
    /// <returns>The fact, or null when the record is not a trading fact or cannot be read.</returns>
    public static TradingFact? Read(string? entityType, string storeId, string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || !IsTradingFact(entityType))
        {
            return null;
        }

        var isRefund = string.Equals(entityType, ReturnEntityType, StringComparison.Ordinal);

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // The business date is what the report groups by, so a record without one cannot be
            // placed in the period and is skipped rather than guessed at from its arrival time.
            // A sale taken at 23:58 and pushed at 00:03 belongs to the earlier day, and attributing
            // it to the later one would move takings between two stores' Z-reports.
            if (ReadDate(root, "businessDate") is not { } businessDate)
            {
                return null;
            }

            var total = isRefund
                ? ReadDecimal(root, "totalRefund")
                : ReadDecimal(root, "total");

            if (total is not { } amount)
            {
                return null;
            }

            var tax = isRefund
                ? ReadDecimal(root, "taxReversed")
                : ReadDecimal(root, "taxTotal");

            var wasVoided = !isRefund &&
                string.Equals(ReadString(root, "status"), VoidedStatus, StringComparison.OrdinalIgnoreCase);

            return new TradingFact(
                StoreId: storeId,
                BusinessDate: businessDate,
                Kind: isRefund ? TradingFactKind.Refund : TradingFactKind.Sale,
                WasVoided: wasVoided,
                Total: amount,
                Tax: tax ?? 0m);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateOnly? ReadDate(JsonElement root, string propertyName)
    {
        var text = ReadString(root, propertyName);

        return DateOnly.TryParseExact(
            text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : null;
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

        // Amounts may arrive as a JSON number or, from some serialisers, as a string. The same
        // tolerance the indexer applies, so the two never disagree about what a record is worth.
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String &&
            decimal.TryParse(
                property.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var text))
        {
            return text;
        }

        return null;
    }
}
