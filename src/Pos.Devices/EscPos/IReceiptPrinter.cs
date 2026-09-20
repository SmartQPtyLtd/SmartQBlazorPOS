// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;
using Pos.Devices.Transport;

namespace Pos.Devices.EscPos;

/// <summary>
/// High-level printer operations, expressed in documents rather than bytes.
/// </summary>
/// <remarks>
/// The interface the checkout flow talks to. It knows nothing about USB, serial
/// ports, or browser APIs — only how to print a receipt and open a drawer. Selecting
/// and connecting a physical device is the resolver's job.
/// </remarks>
public interface IReceiptPrinter
{
    /// <summary>The transport in use, for diagnostics and the device settings screen.</summary>
    IDeviceTransport Transport { get; }

    /// <summary>Whether a printer is connected and ready.</summary>
    bool IsReady => Transport.State.IsReady;

    /// <summary>
    /// Prints a completed receipt.
    /// </summary>
    /// <param name="document">The sale to render and print.</param>
    /// <param name="openDrawer">
    /// Whether to pulse the cash drawer. Ignored when the transport reports
    /// <see cref="PrinterCapabilities.CanOpenDrawer"/> as false.
    /// </param>
    Task PrintReceiptAsync(ReceiptDocument document, bool openDrawer = false, CancellationToken ct = default);

    /// <summary>
    /// Opens the cash drawer without printing.
    /// </summary>
    /// <remarks>
    /// Needed for a "no sale" drawer open, which is a normal retail operation and an
    /// auditable event in its own right.
    /// </remarks>
    Task OpenDrawerAsync(CancellationToken ct = default);
}

/// <summary>
/// Renders receipts and sends them over a transport.
/// </summary>
public sealed class ReceiptPrinter(IDeviceTransport transport, ReceiptRenderer? renderer = null) : IReceiptPrinter
{
    private readonly IDeviceTransport _transport = transport
        ?? throw new ArgumentNullException(nameof(transport));

    private readonly ReceiptRenderer _renderer = renderer ?? new ReceiptRenderer();

    public IDeviceTransport Transport => _transport;

    public async Task PrintReceiptAsync(
        ReceiptDocument document,
        bool openDrawer = false,
        CancellationToken ct = default)
    {
        // A transport that takes HTML is not a broken ESC/POS printer, it is a different kind of
        // output — the browser's print dialog, which is the only route to paper in Safari and
        // Firefox. Routing on the transport's input type is what makes that fallback real: it used
        // to be selected, then handed control bytes it refuses by design, and the customer got
        // nothing while the sale completed and reported only a printer fault.
        if (_transport is IHtmlDocumentTransport html)
        {
            // The drawer is deliberately not attempted. This transport cannot reach one, and
            // claiming otherwise would send a cashier away from an open till — the same rule as
            // the capability check below, applied to a transport that has no capabilities to report.
            //
            // The renderer is built per document because the currency belongs to the sale, and a
            // terminal that has been re-enrolled into another store's books must print that store's
            // currency rather than the one it started the day with.
            await html.PrintHtmlAsync(
                ReceiptHtmlRenderer.TitleFor(document),
                new ReceiptHtmlRenderer(document.Sale.Currency).Render(document),
                ct).ConfigureAwait(false);

            return;
        }

        // Never claim to open a drawer the transport cannot reach. A cashier who
        // believes the drawer opened will walk away from an open till.
        var drawerSupported = openDrawer && _transport.Capabilities.CanOpenDrawer;

        var bytes = _renderer.Render(document, drawerSupported);

        await _transport.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    public async Task OpenDrawerAsync(CancellationToken ct = default)
    {
        if (_transport.Capabilities.CanOpenDrawer)
        {
            var bytes = new EscPosBuilder(_transport.Capabilities.Columns).OpenDrawer().ToArray();

            await _transport.WriteAsync(bytes, ct).ConfigureAwait(false);

            return;
        }

        throw new NotSupportedException(
            $"The {_transport.DisplayName} transport cannot open a cash drawer. " +
            "Connect a printer over USB, Serial, or Bluetooth to use one.");
    }
}
