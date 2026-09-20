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
/// Sends receipt data to a thermal printer over WebUSB.
/// </summary>
/// <remarks>
/// <para>
/// The preferred transport, because it is the only one that reaches the printer
/// directly with no intermediate driver or local agent. It is also the most restricted:
/// WebUSB exists only in Chromium-based browsers, requires HTTPS, and needs a user
/// gesture to grant access. The fallbacks exist precisely because of those limits.
/// </para>
/// <para>
/// All browser interaction goes through <see cref="IDeviceBridge"/>, so this class is
/// plain async C# and can be tested against a fake bridge with no browser present.
/// </para>
/// </remarks>
public sealed class WebUsbTransport(
    IDeviceBridge bridge,
    int columns = 48,
    PrinterBinding? binding = null) : IDeviceTransport
{
    /// <summary>
    /// USB class code 7 is "Printer". Filtering on it presents the operator with
    /// printers rather than every USB device on the machine.
    /// </summary>
    public const int PrinterInterfaceClass = 7;

    /// <summary>Epson's USB vendor id, used as a hint when filtering.</summary>
    public const int EpsonVendorId = 0x04B8;

    /// <summary>Star Micronics' USB vendor id.</summary>
    public const int StarVendorId = 0x0519;

    /// <summary>Bixolon's USB vendor id.</summary>
    public const int BixolonVendorId = 0x1504;

    private readonly IDeviceBridge _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));

    /// <summary>
    /// Which authorised device this transport may attach to.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="PrinterBinding.Any"/>, which is what a shop with one printer wants
    /// and what this transport did before roles existed. A second printer is constructed with an
    /// explicit binding so the two cannot fight over the same device.
    /// </remarks>
    private readonly PrinterBinding _binding = binding ?? PrinterBinding.Any;

    private string? _connectionId;

    public string TransportId => "webusb";

    public string DisplayName => "WebUSB";

    public DeviceState State { get; private set; } = new(DeviceStatus.NotConfigured, "WebUSB");

    /// <summary>
    /// A USB connection can carry ESC/POS directly, so cutting, drawers, images and QR
    /// codes are all available — unlike the browser print fallback.
    /// </summary>
    public PrinterCapabilities Capabilities { get; } =
        new(CanCut: true, CanOpenDrawer: true, CanPrintImages: true, CanPrintQrCode: true, Columns: columns);

    public event Action<DeviceState>? StateChanged;

    /// <summary>The connection id of the attached device, when connected.</summary>
    public string? ConnectionId => _connectionId;

    public async ValueTask<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var supported = await _bridge.IsWebUsbSupportedAsync(ct).ConfigureAwait(false);

        if (!supported)
        {
            // Reported as Unsupported rather than Faulted: nothing is broken, the
            // browser simply does not implement the API.
            SetState(new DeviceState(
                DeviceStatus.Unsupported,
                "WebUSB",
                Detail: "This browser does not support WebUSB. Use Chrome or Edge, or connect over another transport."));
        }

        return supported;
    }

    /// <summary>
    /// Reattaches to an already-authorised printer without prompting.
    /// </summary>
    /// <remarks>
    /// The grant persists per origin, so a terminal that has been paired once comes back
    /// by itself after a refresh or power cycle. Without this the operator would face a
    /// device chooser on every reload, which is unusable at a till.
    /// </remarks>
    public async ValueTask<bool> TryReconnectAsync(CancellationToken ct = default)
    {
        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
        {
            return false;
        }

        var connections = await _bridge.GetAuthorisedWebUsbConnectionsAsync(ct).ConfigureAwait(false);

        var selection = _binding.Select(connections);

        if (!selection.HasCandidates)
        {
            SetState(new DeviceState(
                DeviceStatus.NotConfigured,
                "WebUSB",
                Detail: selection.Refusal));
            return false;
        }

        foreach (var connection in selection.Usable)
        {
            if (await TryOpenAsync(connection, ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        SetState(new DeviceState(
            DeviceStatus.Disconnected,
            "WebUSB",
            Detail: $"{selection.Usable.Count} paired device(s) could not be opened."));
        return false;
    }

    /// <summary>
    /// Prompts the operator to choose a printer.
    /// </summary>
    /// <remarks>
    /// Uses the deferred-promise handshake: the chooser is opened synchronously inside
    /// the click, and the outcome is polled. Calling the chooser from an awaited context
    /// would lose the user gesture and the browser would reject it outright.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask<bool> RequestDeviceAsync(CancellationToken ct = default)
    {
        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
        {
            return false;
        }

        // The chooser stays open until the operator dismisses it. This bound only stops
        // the polling loop from spinning forever if the page is left unattended.
        var limit = TimeSpan.FromMinutes(2);
        var requestId = Guid.NewGuid().ToString("N");

        await _bridge.BeginWebUsbRequestAsync(requestId, vendorId: null, ct).ConfigureAwait(false);

        var deadline = DateTimeOffset.UtcNow + limit;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var result = await _bridge.PollRequestAsync(requestId, ct).ConfigureAwait(false);

            switch (result.State)
            {
                case DeviceRequestState.Succeeded when result.ConnectionId is { } id:
                    if (await TryOpenAsync(id, ct).ConfigureAwait(false))
                    {
                        return true;
                    }

                    SetState(new DeviceState(
                        DeviceStatus.Faulted,
                        "WebUSB",
                        result.DeviceLabel,
                        "The printer was selected but could not be opened."));
                    return false;

                case DeviceRequestState.Cancelled:
                    SetState(new DeviceState(
                        DeviceStatus.NotConfigured,
                        "WebUSB",
                        Detail: "Device selection was cancelled."));
                    return false;

                case DeviceRequestState.Failed:
                case DeviceRequestState.Unknown:
                    SetState(new DeviceState(
                        DeviceStatus.Faulted,
                        "WebUSB",
                        Detail: result.Error ?? "Device selection failed."));
                    return false;
            }

            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        SetState(new DeviceState(
            DeviceStatus.NotConfigured,
            "WebUSB",
            Detail: "Timed out waiting for a device to be selected."));
        return false;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (_connectionId is not { } connection)
        {
            throw new InvalidOperationException(
                "No WebUSB printer is connected. Pair a printer in device settings first.");
        }

        try
        {
            // Copied to an array because the bridge marshals it into JavaScript, which
            // cannot see a span.
            await _bridge.WriteWebUsbAsync(connection, payload.ToArray(), ct).ConfigureAwait(false);
            SetState(State with { Status = DeviceStatus.Ready, Detail = null });
        }
        catch (Exception ex)
        {
            // A printer that is unplugged mid-shift must surface as Faulted, not as an
            // unhandled exception that aborts the sale in progress.
            SetState(State with { Status = DeviceStatus.Faulted, Detail = ex.Message });
            throw;
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        if (_connectionId is { } connection)
        {
            await _bridge.CloseWebUsbAsync(connection, ct).ConfigureAwait(false);
            _connectionId = null;
        }

        SetState(new DeviceState(DeviceStatus.NotConfigured, "WebUSB", Detail: "Disconnected."));
    }

    public ValueTask DisposeAsync() => DisconnectAsync();

    private async Task<bool> TryOpenAsync(string connectionId, CancellationToken ct)
    {
        try
        {
            await _bridge.OpenWebUsbAsync(connectionId, ct).ConfigureAwait(false);
            _connectionId = connectionId;

            var label = await _bridge.DescribeAsync(connectionId, ct).ConfigureAwait(false);
            SetState(new DeviceState(DeviceStatus.Ready, "WebUSB", label));

            return true;
        }
        catch (Exception ex)
        {
            SetState(new DeviceState(DeviceStatus.Faulted, "WebUSB", Detail: ex.Message));
            return false;
        }
    }

    private void SetState(DeviceState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }
}
