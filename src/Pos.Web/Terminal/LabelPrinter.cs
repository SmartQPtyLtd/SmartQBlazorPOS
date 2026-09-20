// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Globalization;
using Pos.Devices.Zpl;

namespace Pos.Web.Terminal;

/// <summary>
/// Sends shelf and stock labels to the label printer.
/// </summary>
/// <remarks>
/// <para>
/// A separate service from <see cref="TerminalPrinterProvider"/> even though it uses the same
/// transports, because what it sends is a different language. A receipt is line-oriented ESC/POS;
/// a label is an absolutely-positioned ZPL canvas. Sending ZPL to a receipt printer produces a
/// page of literal command text, and ESC/POS to a label printer produces nothing at all — so the
/// two are kept apart by type rather than by remembering.
/// </para>
/// <para>
/// Labels are printed on demand, never as part of a sale. A customer buying a tin does not need
/// the shelf edge reprinted, and a till that printed labels during checkout would slow the queue
/// down for work nobody asked for.
/// </para>
/// </remarks>
public sealed class LabelPrinter(
    TerminalPrinterProvider provider,
    LabelSettings settings,
    ILogger<LabelPrinter> logger)
{
    private readonly TerminalPrinterProvider _provider =
        provider ?? throw new ArgumentNullException(nameof(provider));

    private readonly LabelSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    private readonly ILogger<LabelPrinter> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>True when a label printer is paired and reachable.</summary>
    public bool IsAvailable => _provider.IsReady;

    /// <summary>
    /// Which physical printer this sends to.
    /// </summary>
    /// <remarks>
    /// Exposed so the wiring can be asserted rather than assumed. ZPL sent to the receipt printer
    /// comes out as a page of literal command text, and the mistake is a copy-paste in the
    /// container that no other test would notice.
    /// </remarks>
    public PrinterRole Role => _provider.Role;

    /// <summary>Resolves the label printer, or null when none is paired.</summary>
    public Task<Pos.Devices.EscPos.IReceiptPrinter?> ResolveAsync(CancellationToken ct = default) =>
        _provider.GetAsync(ct);

    /// <summary>Forgets the resolved printer, so the next attempt re-reads the binding.</summary>
    public void Reset() => _provider.Reset();

    /// <summary>The label media currently configured.</summary>
    public Task<LabelStock> StockAsync(CancellationToken ct = default) => _settings.GetAsync(ct);

    /// <summary>
    /// Prints a shelf-edge label for a product.
    /// </summary>
    /// <param name="name">Product name as the customer knows it.</param>
    /// <param name="barcode">Scannable product code.</param>
    /// <param name="price">Shelf price, formatted for the label.</param>
    /// <param name="sku">Optional stock-keeping unit.</param>
    /// <param name="copies">How many labels to print, for a row of the same product.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the labels were sent.</returns>
    public Task<bool> PrintShelfLabelAsync(
        string name,
        string barcode,
        decimal price,
        string? sku = null,
        int copies = 1,
        CancellationToken ct = default) =>
        PrintAsync(
            stock => LabelRenderer.RenderShelfLabel(
                new ShelfLabel(barcode, name, FormatPrice(price), sku),
                stock.WidthMm,
                stock.HeightMm,
                stock.Dpi),
            $"shelf label for {name}",
            copies,
            ct);

    /// <summary>
    /// Prints a barcode-only label, for tagging stock or a shelf position.
    /// </summary>
    /// <param name="barcode">Code to encode.</param>
    /// <param name="caption">Optional human-readable caption.</param>
    /// <param name="copies">How many labels to print.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the labels were sent.</returns>
    public Task<bool> PrintBarcodeLabelAsync(
        string barcode,
        string? caption = null,
        int copies = 1,
        CancellationToken ct = default) =>
        PrintAsync(
            stock => LabelRenderer.RenderBarcodeLabel(
                barcode, caption, stock.WidthMm, stock.HeightMm, stock.Dpi),
            $"barcode label for {barcode}",
            copies,
            ct);

    /// <summary>
    /// Renders and sends labels, sharing the failure handling between both label kinds.
    /// </summary>
    /// <remarks>
    /// Returns false rather than throwing. A label that does not print is an inconvenience — the
    /// shelf keeps its old label until someone tries again — which is a completely different
    /// situation from a sale that cannot complete. Throwing would present it as the latter.
    /// </remarks>
    private async Task<bool> PrintAsync(
        Func<LabelStock, byte[]> render,
        string description,
        int copies,
        CancellationToken ct)
    {
        if (copies < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(copies), copies, "At least one label is needed.");
        }

        try
        {
            var printer = await _provider.GetAsync(ct).ConfigureAwait(false);

            if (printer is null)
            {
                TerminalLog.LabelPrinterMissing(_logger, description);
                return false;
            }

            var stock = await _settings.GetAsync(ct).ConfigureAwait(false);

            if (!stock.IsValid(out var reason))
            {
                // Checked again here rather than trusting the settings screen, because a stored
                // value can outlive the validation that let it in.
                TerminalLog.LabelStockInvalid(_logger, reason);
                return false;
            }

            // Rendered per copy rather than repeating the bytes. A label printer's buffer is
            // small, and a shop printing forty shelf labels in one job should not have the 39th
            // silently dropped.
            for (var copy = 0; copy < copies; copy++)
            {
                ct.ThrowIfCancellationRequested();

                await printer.Transport.WriteAsync(render(stock), ct).ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TerminalLog.LabelPrintFailed(_logger, description, ex);
            return false;
        }
    }

    /// <summary>
    /// Formats a price for a shelf label.
    /// </summary>
    /// <remarks>
    /// Invariant culture and two decimals, matching how every other figure in the system is
    /// written. A label is read by a customer comparing it against the till, so the two must agree
    /// character for character.
    /// </remarks>
    private static string FormatPrice(decimal price) =>
        price.ToString("0.00", CultureInfo.InvariantCulture);
}
