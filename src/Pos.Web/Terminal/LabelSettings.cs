// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Text.Json;
using Microsoft.JSInterop;
using Pos.Devices.Zpl;

namespace Pos.Web.Terminal;

/// <summary>
/// Remembers the label stock loaded in the label printer.
/// </summary>
/// <remarks>
/// <para>
/// Not part of the device binding, deliberately. A binding is <em>which printer</em>; this is
/// <em>what is loaded in it</em>. Swapping a roll of 50×25mm labels for 100×50mm ones does not
/// change which printer the till talks to, and a shop that had to re-pair after every roll change
/// would stop bothering.
/// </para>
/// <para>
/// Local storage, like the bindings, because it is a fact about this terminal's hardware. Syncing
/// it would have every till in a shop claim the same roll of labels.
/// </para>
/// </remarks>
public sealed class LabelSettings(IJSRuntime js)
{
    private const string Key = "pos.printer.label.stock";

    /// <summary>
    /// Serialisation settings for the stored row.
    /// </summary>
    /// <remarks>
    /// Explicit rather than the defaults, because this value is written by one build and read by
    /// the next. Default serialisation is case-sensitive on the way back in, so a naming change or
    /// a hand-edited value would silently stop matching and the till would quietly revert to the
    /// default roll size — printing labels that look right and are the wrong dimensions.
    /// </remarks>
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IJSRuntime _js = js ?? throw new ArgumentNullException(nameof(js));

    /// <summary>The configured stock, or the default when nothing has been set.</summary>
    public async Task<LabelStock> GetAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", ct, Key).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(json))
            {
                return LabelStock.Default;
            }

            var stored = JsonSerializer.Deserialize<StoredStock>(json, Json);

            if (stored is null)
            {
                return LabelStock.Default;
            }

            var stock = new LabelStock(stored.WidthMm, stored.HeightMm, stored.Dpi);

            // Returned as-is even when it is out of range, rather than being quietly replaced with
            // the default.
            //
            // The two cases are different. Nothing stored means nothing was ever configured, and
            // the default roll size is the only sensible answer. A value that parsed but is
            // unusable means somebody configured something wrong — and printing a differently
            // sized label than they asked for is worse than printing nothing, because the wrong
            // label comes out looking like a successful print. Callers check IsValid and refuse,
            // and the settings screen shows the bad value so it can be corrected.
            return stock;
        }
        catch (JSException)
        {
            return LabelStock.Default;
        }
        catch (JsonException)
        {
            return LabelStock.Default;
        }
    }

    /// <summary>Stores the stock, refusing geometry the printer could not use.</summary>
    /// <exception cref="InvalidOperationException">The geometry is not usable.</exception>
    public async Task SaveAsync(LabelStock stock, CancellationToken ct = default)
    {
        if (!stock.IsValid(out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        try
        {
            await _js.InvokeVoidAsync(
                "localStorage.setItem",
                ct,
                Key,
                JsonSerializer.Serialize(new StoredStock(stock.WidthMm, stock.HeightMm, stock.Dpi), Json))
                .ConfigureAwait(false);
        }
        catch (JSException)
        {
            // Storage can be unavailable in a locked-down profile. The printer still prints; it
            // simply reverts to the default roll size after a refresh.
        }
    }

    /// <summary>
    /// The stored row.
    /// </summary>
    /// <remarks>
    /// Not private: the serialiser needs to construct it, and a private nested type is not
    /// something it will do reliably.
    /// </remarks>
    public sealed record StoredStock(double WidthMm, double HeightMm, int Dpi);
}
