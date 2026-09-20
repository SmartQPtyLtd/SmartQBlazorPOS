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
/// The bridge between C# and the browser's device APIs.
/// </summary>
/// <remarks>
/// <para>
/// Live device handles cannot be marshalled into .NET, so the <b>JavaScript side owns
/// the handle</b> while <b>C# owns the bytes</b>. Every call here is keyed by an opaque
/// connection id that JavaScript maps to a real <c>USBDevice</c>.
/// </para>
/// <para>
/// Abstracted behind an interface so the WebUSB transport can be unit tested without a
/// browser at all, and so that non-browser hosts can substitute a no-op.
/// </para>
/// </remarks>
public interface IDeviceBridge
{
    /// <summary>True when the WebUSB API exists in this browser.</summary>
    ValueTask<bool> IsWebUsbSupportedAsync(CancellationToken ct = default);

    /// <summary>True when the Web Serial API exists in this browser.</summary>
    ValueTask<bool> IsWebSerialSupportedAsync(CancellationToken ct = default);

    /// <summary>
    /// Begins a device chooser prompt.
    /// </summary>
    /// <remarks>
    /// <b>Must be invoked from a user gesture.</b> The implementation calls
    /// <c>requestDevice()</c> synchronously and stores the resulting promise against
    /// <paramref name="requestId"/>; the caller then polls <see cref="PollRequestAsync"/>.
    /// Awaiting the chooser directly would lose the gesture and the browser would reject
    /// the request.
    /// </remarks>
    /// <param name="requestId">Opaque id used to correlate the stored promise.</param>
    /// <param name="vendorId">Optional USB vendor filter, e.g. 0x04B8 for Epson.</param>
    ValueTask BeginWebUsbRequestAsync(string requestId, int? vendorId = null, CancellationToken ct = default);

    /// <summary>
    /// Checks the outcome of a request started with <see cref="BeginWebUsbRequestAsync"/>.
    /// </summary>
    /// <returns>A result describing whether it is still pending, succeeded, or failed.</returns>
    ValueTask<DeviceRequestResult> PollRequestAsync(string requestId, CancellationToken ct = default);

    /// <summary>
    /// Reattaches to previously authorised devices without showing a prompt.
    /// </summary>
    /// <returns>Connection ids for every device this origin already has access to.</returns>
    ValueTask<IReadOnlyList<string>> GetAuthorisedWebUsbConnectionsAsync(CancellationToken ct = default);

    /// <summary>Opens a WebUSB device ready for bulk transfer on the printer interface.</summary>
    ValueTask OpenWebUsbAsync(string connectionId, CancellationToken ct = default);

    /// <summary>Writes bytes to an open WebUSB connection.</summary>
    ValueTask WriteWebUsbAsync(string connectionId, byte[] payload, CancellationToken ct = default);

    /// <summary>Closes and forgets a WebUSB connection.</summary>
    ValueTask CloseWebUsbAsync(string connectionId, CancellationToken ct = default);

    /// <summary>Descriptor of a connected device, for display in settings.</summary>
    ValueTask<string?> DescribeAsync(string connectionId, CancellationToken ct = default);

    // ------------------------------------------------------------------ Web Serial

    /// <summary>
    /// Begins a serial port chooser prompt.
    /// </summary>
    /// <remarks>
    /// Same deferred-promise handshake as WebUSB, and for the same reason: the chooser must
    /// be opened while the user gesture is still live, so the promise is parked and polled.
    /// </remarks>
    ValueTask BeginWebSerialRequestAsync(string requestId, CancellationToken ct = default);

    /// <summary>Reattaches to serial ports this origin has already been granted.</summary>
    ValueTask<IReadOnlyList<string>> GetAuthorisedWebSerialConnectionsAsync(CancellationToken ct = default);

    /// <summary>Opens a serial port at the given line settings.</summary>
    ValueTask OpenWebSerialAsync(
        string connectionId,
        int baudRate,
        CancellationToken ct = default);

    /// <summary>Writes bytes to an open serial port.</summary>
    ValueTask WriteWebSerialAsync(string connectionId, byte[] payload, CancellationToken ct = default);

    /// <summary>Closes and forgets a serial connection.</summary>
    ValueTask CloseWebSerialAsync(string connectionId, CancellationToken ct = default);

    // ------------------------------------------------------------- Browser printing

    /// <summary>
    /// Whether the browser print fallback can be used at all.
    /// </summary>
    /// <remarks>
    /// Always true where a window exists, which is what makes it the last-resort transport
    /// for browsers with no device APIs at all.
    /// </remarks>
    ValueTask<bool> IsBrowserPrintAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Opens the system print dialog with the supplied document.
    /// </summary>
    /// <param name="title">Document title, used as the print job name.</param>
    /// <param name="htmlBody">Printable HTML for the receipt.</param>
    ValueTask PrintViaBrowserAsync(string title, string htmlBody, CancellationToken ct = default);
}

/// <summary>
/// The deferred-promise half of the Web Serial handshake.
/// </summary>
/// <remarks>
/// Separate from <see cref="IDeviceBridge"/> because WebUSB and Web Serial park their
/// chooser promises in independent registries. Keeping the poll methods on distinct
/// interfaces means a bridge can implement one without the other, and the transports stay
/// decoupled.
/// </remarks>
public interface ISerialRequestPoller
{
    /// <summary>
    /// Checks the outcome of a request started with
    /// <see cref="IDeviceBridge.BeginWebSerialRequestAsync"/>.
    /// </summary>
    ValueTask<DeviceRequestResult> PollSerialRequestAsync(string requestId, CancellationToken ct = default);
}

/// <summary>Outcome of polling a deferred device request.</summary>
/// <param name="State">Whether the request is pending, succeeded, was cancelled, or failed.</param>
/// <param name="ConnectionId">Connection id when <paramref name="State"/> is Succeeded.</param>
/// <param name="DeviceLabel">Product name of the chosen device, when known.</param>
/// <param name="Error">Failure detail when <paramref name="State"/> is Failed.</param>
public readonly record struct DeviceRequestResult(
    DeviceRequestState State,
    string? ConnectionId = null,
    string? DeviceLabel = null,
    string? Error = null);

/// <summary>Lifecycle of a deferred device chooser request.</summary>
public enum DeviceRequestState
{
    /// <summary>The operator has not yet finished choosing.</summary>
    Pending = 0,

    /// <summary>A device was chosen and is available for use.</summary>
    Succeeded = 1,

    /// <summary>The operator dismissed the chooser. Not an error.</summary>
    Cancelled = 2,

    /// <summary>The request failed. <c>Error</c> carries the reason.</summary>
    Failed = 3,

    /// <summary>No such request id. Usually means the page was reloaded mid-request.</summary>
    Unknown = 4,
}
