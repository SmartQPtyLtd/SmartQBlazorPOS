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
/// Sends receipt data over a serial connection using Web Serial.
/// </summary>
/// <remarks>
/// <para>
/// The pragmatic second choice after WebUSB, and often the one that actually works. Many
/// ESC/POS printers expose a USB-serial interface, and USB-to-serial adapters are
/// universal. Crucially, it also succeeds where WebUSB fails: on Windows a printer is
/// frequently held by the system <c>usbprint</c> driver, which prevents the interface from
/// being claimed, whereas the serial port remains available.
/// </para>
/// <para>
/// Chromium-only, like WebUSB, so it is not the cross-browser answer — the browser print
/// fallback covers that.
/// </para>
/// </remarks>
public sealed class WebSerialTransport(
    IDeviceBridge bridge,
    int columns = 48,
    int baudRate = 9600,
    PrinterBinding? binding = null)
    : IDeviceTransport
{
    /// <summary>Line rate most thermal printers default to.</summary>
    public const int DefaultBaudRate = 9600;

    /// <summary>Line rate used by faster 80mm printers and by many adapter cables.</summary>
    public const int HighSpeedBaudRate = 115200;

    private readonly IDeviceBridge _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    private readonly int _baudRate = baudRate <= 0 ? DefaultBaudRate : baudRate;

    /// <summary>
    /// Which authorised port this transport may attach to.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="PrinterBinding.Any"/> for a single-printer shop. A second printer is
    /// constructed with an explicit binding, so two transports cannot open the same port.
    /// </remarks>
    private readonly PrinterBinding _binding = binding ?? PrinterBinding.Any;

    private string? _connectionId;

    public string TransportId => "webserial";

    public string DisplayName => "Web Serial";

    public DeviceState State { get; private set; } = new(DeviceStatus.NotConfigured, "Web Serial");

    /// <summary>
    /// A serial connection carries raw ESC/POS, so cutting, drawer pulses, images and QR
    /// codes are all available — unlike the browser print fallback.
    /// </summary>
    public PrinterCapabilities Capabilities { get; } =
        new(CanCut: true, CanOpenDrawer: true, CanPrintImages: true, CanPrintQrCode: true, Columns: columns);

    public event Action<DeviceState>? StateChanged;

    /// <summary>The connection id of the attached port, when connected.</summary>
    public string? ConnectionId => _connectionId;

    /// <summary>The line rate this transport opens ports at.</summary>
    public int BaudRate => _baudRate;

    public async ValueTask<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var supported = await _bridge.IsWebSerialSupportedAsync(ct).ConfigureAwait(false);

        if (!supported)
        {
            SetState(new DeviceState(
                DeviceStatus.Unsupported,
                "Web Serial",
                Detail: "This browser does not support Web Serial. Use Chrome or Edge, or print via the browser instead."));
        }

        return supported;
    }

    public async ValueTask<bool> TryReconnectAsync(CancellationToken ct = default)
    {
        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
        {
            return false;
        }

        var connections = await _bridge.GetAuthorisedWebSerialConnectionsAsync(ct).ConfigureAwait(false);

        var selection = _binding.Select(connections, deviceNoun: "serial printer");

        if (!selection.HasCandidates)
        {
            SetState(new DeviceState(
                DeviceStatus.NotConfigured,
                "Web Serial",
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
            "Web Serial",
            Detail: $"{selection.Usable.Count} paired port(s) could not be opened."));
        return false;
    }

    /// <summary>
    /// Prompts the operator to choose a serial port.
    /// </summary>
    /// <remarks>
    /// Uses the same deferred-promise handshake as WebUSB: the chooser must be opened while
    /// the click is still live, so the promise is parked under a request id and polled.
    /// </remarks>
    public async ValueTask<bool> RequestDeviceAsync(CancellationToken ct = default)
    {
        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
        {
            return false;
        }

        // Web Serial does not share the WebUSB request registry, so a distinct poller is
        // required; without it the request would hang until the timeout.
        if (_bridge is not ISerialRequestPoller poller)
        {
            SetState(new DeviceState(
                DeviceStatus.Faulted,
                "Web Serial",
                Detail: "This build cannot complete a serial pairing request."));
            return false;
        }

        var limit = TimeSpan.FromMinutes(2);
        var requestId = Guid.NewGuid().ToString("N");

        await _bridge.BeginWebSerialRequestAsync(requestId, ct).ConfigureAwait(false);

        var deadline = DateTimeOffset.UtcNow + limit;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var result = await poller.PollSerialRequestAsync(requestId, ct).ConfigureAwait(false);

            switch (result.State)
            {
                case DeviceRequestState.Succeeded when result.ConnectionId is { } id:
                    if (await TryOpenAsync(id, ct).ConfigureAwait(false))
                    {
                        return true;
                    }

                    SetState(new DeviceState(
                        DeviceStatus.Faulted,
                        "Web Serial",
                        result.DeviceLabel,
                        "The port was selected but could not be opened."));
                    return false;

                case DeviceRequestState.Cancelled:
                    SetState(new DeviceState(
                        DeviceStatus.NotConfigured,
                        "Web Serial",
                        Detail: "Port selection was cancelled."));
                    return false;

                case DeviceRequestState.Failed:
                case DeviceRequestState.Unknown:
                    SetState(new DeviceState(
                        DeviceStatus.Faulted,
                        "Web Serial",
                        Detail: result.Error ?? "Port selection failed."));
                    return false;
            }

            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        SetState(new DeviceState(
            DeviceStatus.NotConfigured,
            "Web Serial",
            Detail: "Timed out waiting for a port to be selected."));
        return false;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (_connectionId is not { } connection)
        {
            throw new InvalidOperationException(
                "No serial printer is connected. Pair one in device settings first.");
        }

        try
        {
            await _bridge.WriteWebSerialAsync(connection, payload.ToArray(), ct).ConfigureAwait(false);
            SetState(State with { Status = DeviceStatus.Ready, Detail = null });
        }
        catch (Exception ex)
        {
            // An unplugged cable must surface as Faulted rather than as an unhandled
            // exception that aborts the sale in progress.
            SetState(State with { Status = DeviceStatus.Faulted, Detail = ex.Message });
            throw;
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        if (_connectionId is { } connection)
        {
            await _bridge.CloseWebSerialAsync(connection, ct).ConfigureAwait(false);
            _connectionId = null;
        }

        SetState(new DeviceState(DeviceStatus.NotConfigured, "Web Serial", Detail: "Disconnected."));
    }

    public ValueTask DisposeAsync() => DisconnectAsync();

    private async Task<bool> TryOpenAsync(string connectionId, CancellationToken ct)
    {
        try
        {
            await _bridge.OpenWebSerialAsync(connectionId, _baudRate, ct).ConfigureAwait(false);
            _connectionId = connectionId;

            var label = await _bridge.DescribeAsync(connectionId, ct).ConfigureAwait(false);
            SetState(new DeviceState(DeviceStatus.Ready, "Web Serial", label ?? $"Serial at {_baudRate} baud"));

            return true;
        }
        catch (Exception ex)
        {
            // A port held by another application, or opened by a stale tab, fails here.
            SetState(new DeviceState(DeviceStatus.Faulted, "Web Serial", Detail: ex.Message));
            return false;
        }
    }

    private void SetState(DeviceState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }
}
