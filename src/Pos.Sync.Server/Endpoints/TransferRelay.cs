// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Text.Json;

namespace Pos.Sync.Server.Endpoints;

/// <summary>
/// Reads what the hub needs out of a stock transfer payload to route it.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately minimal: the hub is not the owner of a transfer and does not reimplement its rules.
/// It needs one fact — where the document is addressed — and it reads only that, tolerantly.
/// </para>
/// <para>
/// A transfer whose payload cannot be read is still stored and still synced to the sending store.
/// It simply is not relayed. Rejecting it would strand the record on the terminal while the stock
/// had already left that shop's books, which is a far worse outcome than a transfer that needs
/// looking at by hand.
/// </para>
/// </remarks>
public static class TransferRelay
{
    /// <summary>
    /// Reads the destination store from a transfer payload.
    /// </summary>
    /// <returns>The destination store id, or null when the payload does not name one.</returns>
    public static string? ReadDestinationStoreId(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("toStoreId", out var destination) ||
                destination.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = destination.GetString();

            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the sending store from a transfer payload, for diagnostics.
    /// </summary>
    /// <remarks>
    /// Never used for authorisation. The store a record is filed under comes from the credential,
    /// and a payload is not trusted for that — this is only to explain a refusal in the log.
    /// </remarks>
    public static string? ReadSourceStoreId(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("fromStoreId", out var source) ||
                source.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return source.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
