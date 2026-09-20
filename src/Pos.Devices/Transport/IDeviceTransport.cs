// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

namespace Pos.Devices.Transport;

/// <summary>Health of a configured peripheral.</summary>
public enum DeviceStatus
{
    /// <summary>No device has been selected for this role.</summary>
    NotConfigured = 0,

    /// <summary>A device is selected but not currently reachable.</summary>
    Disconnected = 1,

    /// <summary>Connected and ready to accept a job.</summary>
    Ready = 2,

    /// <summary>The last operation failed. Carries a message for the operator.</summary>
    Faulted = 3,

    /// <summary>
    /// Not supported in this browser. Distinct from <see cref="Faulted"/>: nothing is
    /// broken, the capability simply does not exist here.
    /// </summary>
    Unsupported = 4,
}

/// <summary>
/// What a connected printer can actually do.
/// </summary>
/// <remarks>
/// Reported by the adapter rather than assumed, so the UI can degrade honestly.
/// A browser print fallback cannot pulse a cash drawer, and telling the operator that
/// is far better than silently failing to open it mid-transaction.
/// </remarks>
public readonly record struct PrinterCapabilities(
    bool CanCut,
    bool CanOpenDrawer,
    bool CanPrintImages,
    bool CanPrintQrCode,
    int Columns)
{
    /// <summary>Nothing is possible. Used before a device is selected.</summary>
    public static PrinterCapabilities None => new(false, false, false, false, 32);
}

/// <summary>Current state of a peripheral, surfaced to the UI.</summary>
/// <param name="Status">Health of the device.</param>
/// <param name="TransportName">Human-readable transport, e.g. "WebUSB".</param>
/// <param name="DeviceLabel">Product name of the peripheral where known.</param>
/// <param name="Detail">Extra diagnostic text, shown in device settings.</param>
public readonly record struct DeviceState(
    DeviceStatus Status,
    string TransportName,
    string? DeviceLabel = null,
    string? Detail = null)
{
    public bool IsReady => Status == DeviceStatus.Ready;
}

/// <summary>
/// Moves raw bytes to a physical device.
/// </summary>
/// <remarks>
/// <para>
/// The single seam that keeps browser-specific device APIs out of the rest of the
/// system. Callers hand over a finished ESC/POS byte array and never learn whether it
/// travelled over WebUSB, Web Serial, Bluetooth, an HTTP bridge, or the operating
/// system print dialog.
/// </para>
/// <para>
/// Implementations must be safe to call when not connected: they should report a
/// <see cref="DeviceStatus"/> rather than throwing, so a failed print never becomes an
/// unhandled exception part-way through a sale.
/// </para>
/// </remarks>
public interface IDeviceTransport : IAsyncDisposable
{
    /// <summary>Stable identifier for this transport kind, e.g. "webusb".</summary>
    string TransportId { get; }

    /// <summary>Display name shown in device settings.</summary>
    string DisplayName { get; }

    /// <summary>Current health of the underlying device.</summary>
    DeviceState State { get; }

    /// <summary>Capabilities of the connected device.</summary>
    PrinterCapabilities Capabilities { get; }

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    event Action<DeviceState>? StateChanged;

    /// <summary>
    /// Whether this transport can even be attempted in the current environment.
    /// </summary>
    /// <remarks>
    /// Lets the resolver skip WebUSB entirely on Firefox rather than offering a
    /// button that can only fail.
    /// </remarks>
    ValueTask<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Attempts to reconnect to a previously authorised device without prompting.
    /// </summary>
    /// <remarks>
    /// This is what makes a terminal usable day to day: after a one-time pairing, a
    /// page refresh or a power cycle should silently reattach to the printer. A browser
    /// prompt on every reload would be unacceptable at a till.
    /// </remarks>
    ValueTask<bool> TryReconnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Prompts the operator to choose a device. Must be called from a user gesture.
    /// </summary>
    ValueTask<bool> RequestDeviceAsync(CancellationToken ct = default);

    /// <summary>
    /// Sends a raw byte payload. Throws only on genuine failure; a disconnected device
    /// is reported through <see cref="State"/>.
    /// </summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default);

    /// <summary>Forgets the paired device.</summary>
    ValueTask DisconnectAsync(CancellationToken ct = default);
}
