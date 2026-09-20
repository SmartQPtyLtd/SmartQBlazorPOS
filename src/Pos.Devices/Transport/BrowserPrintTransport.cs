// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

namespace Pos.Devices.Transport;

/// <summary>
/// Prints receipts through the browser's own print dialog.
/// </summary>
/// <remarks>
/// <para>
/// The last-resort transport, and the only one that works in <b>every</b> browser —
/// including Safari and Firefox, which will never implement WebUSB. It exists so that a
/// terminal with no device APIs can still complete a sale rather than leaving the operator
/// with nothing to hand the customer.
/// </para>
/// <para>
/// It is genuinely degraded, and the capability report says so: a browser print job cannot
/// cut paper or pulse a cash drawer, and it routes through the operating system's print
/// stack rather than to the printer directly, so it is unsuitable for high-volume receipt
/// printing. The UI uses <see cref="PrinterCapabilities"/> to warn the operator instead of
/// silently failing to open a drawer.
/// </para>
/// </remarks>
public sealed class BrowserPrintTransport(IDeviceBridge bridge, int columns = 48)
    : IDeviceTransport, IHtmlDocumentTransport
{
    private readonly IDeviceBridge _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));

    public string TransportId => "browserprint";

    public string DisplayName => "Browser print";

    public DeviceState State { get; private set; } = new(DeviceStatus.NotConfigured, "Browser print");

    /// <summary>
    /// No cut and no drawer, because the browser print path cannot reach printer control
    /// codes at all. QR codes and images survive, since they are rendered as page content.
    /// </summary>
    public PrinterCapabilities Capabilities { get; } =
        new(CanCut: false, CanOpenDrawer: false, CanPrintImages: true, CanPrintQrCode: true, Columns: columns);

    public event Action<DeviceState>? StateChanged;

    public async ValueTask<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var available = await _bridge.IsBrowserPrintAvailableAsync(ct).ConfigureAwait(false);

        SetState(available
            ? new DeviceState(DeviceStatus.Ready, "Browser print", "System print dialog")
            : new DeviceState(DeviceStatus.Unsupported, "Browser print", Detail: "No print dialog is available."));

        return available;
    }

    public async ValueTask<bool> TryReconnectAsync(CancellationToken ct = default)
    {
        // There is nothing to reattach: the print dialog is part of the browser, so if the
        // browser is running then this transport is ready.
        return await IsAvailableAsync(ct).ConfigureAwait(false);
    }

    public ValueTask<bool> RequestDeviceAsync(CancellationToken ct = default) =>
        TryReconnectAsync(ct);

    /// <summary>
    /// Always throws.
    /// </summary>
    /// <remarks>
    /// ESC/POS control bytes are meaningless to a print dialog — sending them would emit a
    /// page of garbage characters. Rather than corrupt a receipt, this refuses loudly and
    /// directs the caller to <see cref="PrintHtmlAsync"/>.
    /// </remarks>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "The browser print transport cannot accept raw ESC/POS bytes. " +
            "Use PrintHtmlAsync, or connect a printer over WebUSB or Web Serial.");

    /// <summary>
    /// Prints a receipt rendered as HTML.
    /// </summary>
    /// <param name="title">Job name shown in the print dialog.</param>
    /// <param name="htmlBody">Printable HTML for the receipt body.</param>
    public async ValueTask PrintHtmlAsync(string title, string htmlBody, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(htmlBody);

        try
        {
            await _bridge.PrintViaBrowserAsync(title, htmlBody, ct).ConfigureAwait(false);
            SetState(new DeviceState(DeviceStatus.Ready, "Browser print", "Sent to the print dialog"));
        }
        catch (Exception ex)
        {
            SetState(State with { Status = DeviceStatus.Faulted, Detail = ex.Message });
            throw;
        }
    }

    public ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        // Nothing is held open, so there is nothing to release.
        SetState(new DeviceState(DeviceStatus.NotConfigured, "Browser print"));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void SetState(DeviceState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }
}
