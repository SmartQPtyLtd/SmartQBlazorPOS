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
using Pos.Devices.EscPos;
using Pos.Devices.Transport;

namespace Pos.Web.Terminal;

/// <summary>
/// A physical printer a terminal can have.
/// </summary>
/// <remarks>
/// Roles exist because a shop has more than one printer and they are not interchangeable. The
/// receipt printer faces the customer and prints money; the kitchen printer faces the pass and
/// prints work. Sending either document to the other machine is worse than not printing it.
/// </remarks>
public enum PrinterRole
{
    /// <summary>The customer-facing receipt printer. Every till has one.</summary>
    Receipt = 0,

    /// <summary>The kitchen or bar printer. Only shops that prepare food have one.</summary>
    Kitchen = 1,

    /// <summary>
    /// The label printer, for shelf-edge and stock labels.
    /// </summary>
    /// <remarks>
    /// A third role rather than a mode on the receipt printer, because the two speak entirely
    /// different command languages: a receipt is line-oriented ESC/POS, a label is an
    /// absolutely-positioned ZPL canvas. Sending ZPL to a receipt printer produces a page of
    /// literal command text, and ESC/POS to a label printer produces nothing at all.
    /// </remarks>
    Label = 2,
}

/// <summary>
/// Remembers which physical device is paired for each printer role.
/// </summary>
/// <remarks>
/// <para>
/// The binding is what stops a second printer from stealing the first one's device. A transport
/// given no binding takes whichever authorised device it can open, so two roles resolving in the
/// same page load would fight over one printer — and the symptom is not a failure, it is the
/// kitchen ticket coming out of the receipt printer.
/// </para>
/// <para>
/// Stored in local storage rather than the sync database, because it is a fact about <em>this
/// terminal's wiring</em>: the printer plugged into till 2 is not the printer plugged into till 1,
/// and syncing the binding would have every till in a shop try to open the same device.
/// </para>
/// </remarks>
public sealed class PrinterBindings(IJSRuntime js)
{
    private readonly IJSRuntime _js = js ?? throw new ArgumentNullException(nameof(js));

    private static string KeyFor(PrinterRole role) => role switch
    {
        PrinterRole.Kitchen => "pos.printer.kitchen.binding",
        PrinterRole.Label => "pos.printer.label.binding",
        _ => "pos.printer.receipt.binding",
    };

    /// <summary>The device bound to a role, or null when it has never been paired.</summary>
    public async Task<PrinterBindingRecord?> GetAsync(PrinterRole role, CancellationToken ct = default)
    {
        try
        {
            var json = await _js
                .InvokeAsync<string?>("localStorage.getItem", ct, KeyFor(role))
                .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<PrinterBindingRecord>(json);
        }
        catch (JSException)
        {
            // Storage can be unavailable in a locked-down profile. The terminal still sells; it
            // simply has to be paired again.
            return null;
        }
    }

    /// <summary>Records the device paired for a role.</summary>
    public async Task SaveAsync(PrinterRole role, PrinterBindingRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            await _js.InvokeVoidAsync(
                "localStorage.setItem", ct, KeyFor(role), JsonSerializer.Serialize(record))
                .ConfigureAwait(false);
        }
        catch (JSException)
        {
            // Ignored for the same reason as above: a binding that cannot be persisted costs a
            // re-pair after a refresh, not a lost sale.
        }
    }

    /// <summary>Forgets the device paired for a role.</summary>
    public async Task ClearAsync(PrinterRole role, CancellationToken ct = default)
    {
        try
        {
            await _js.InvokeVoidAsync("localStorage.removeItem", ct, KeyFor(role)).ConfigureAwait(false);
        }
        catch (JSException)
        {
            // Ignored.
        }
    }

    /// <summary>
    /// The binding a transport for this role should be constructed with.
    /// </summary>
    /// <remarks>
    /// The receipt printer is unbound on purpose. It is the machine that must always work — a
    /// sale that cannot print a receipt is a sale the customer cannot prove — so it takes whatever
    /// device it can open rather than refusing because a specific one is missing.
    /// </remarks>
    public async Task<PrinterBinding> ForTransportAsync(PrinterRole role, CancellationToken ct = default)
    {
        if (role == PrinterRole.Receipt)
        {
            return PrinterBinding.Any;
        }

        var record = await GetAsync(role, ct).ConfigureAwait(false);

        return record?.ConnectionId is { Length: > 0 } connectionId
            ? PrinterBinding.Only(connectionId)
            : PrinterBinding.Unpaired;
    }

    /// <summary>What the device settings screen shows for a role.</summary>
    public async Task<string> DescribeAsync(PrinterRole role, CancellationToken ct = default)
    {
        var record = await GetAsync(role, ct).ConfigureAwait(false);

        return record is null
            ? "Not paired"
            : $"{record.Label ?? "Printer"} ({record.Transport ?? "unknown transport"})";
    }
}

/// <summary>A paired device, as persisted.</summary>
/// <param name="ConnectionId">Opaque connection id the bridge understands.</param>
/// <param name="Transport">Transport it was paired over, e.g. "webusb".</param>
/// <param name="Label">Product name, for the operator.</param>
public sealed record PrinterBindingRecord(string ConnectionId, string? Transport, string? Label);
