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
/// A <see cref="IDeviceBridge"/> for environments with no browser device APIs.
/// </summary>
/// <remarks>
/// Reports every capability as unavailable rather than throwing, so transports can be
/// constructed and probed safely in unit tests, on a server, and in browsers whose
/// APIs are missing. The resolver then simply skips these transports, which is exactly
/// the graceful degradation the design calls for.
/// </remarks>
public sealed class NullDeviceBridge : IDeviceBridge
{
    /// <summary>Shared instance, since the type carries no state.</summary>
    public static NullDeviceBridge Instance { get; } = new();

    public ValueTask<bool> IsWebUsbSupportedAsync(CancellationToken ct = default) => ValueTask.FromResult(false);

    public ValueTask<bool> IsWebSerialSupportedAsync(CancellationToken ct = default) => ValueTask.FromResult(false);

    public ValueTask BeginWebUsbRequestAsync(string requestId, int? vendorId = null, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask<DeviceRequestResult> PollRequestAsync(string requestId, CancellationToken ct = default) =>
        ValueTask.FromResult(new DeviceRequestResult(
            DeviceRequestState.Failed,
            Error: "WebUSB is not available in this environment."));

    public ValueTask<IReadOnlyList<string>> GetAuthorisedWebUsbConnectionsAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<string>>([]);

    public ValueTask OpenWebUsbAsync(string connectionId, CancellationToken ct = default) =>
        throw new NotSupportedException("WebUSB is not available in this environment.");

    public ValueTask WriteWebUsbAsync(string connectionId, byte[] payload, CancellationToken ct = default) =>
        throw new NotSupportedException("WebUSB is not available in this environment.");

    public ValueTask CloseWebUsbAsync(string connectionId, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask<string?> DescribeAsync(string connectionId, CancellationToken ct = default) =>
        ValueTask.FromResult<string?>(null);

    public ValueTask BeginWebSerialRequestAsync(string requestId, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask<IReadOnlyList<string>> GetAuthorisedWebSerialConnectionsAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<string>>([]);

    public ValueTask OpenWebSerialAsync(string connectionId, int baudRate, CancellationToken ct = default) =>
        throw new NotSupportedException("Web Serial is not available in this environment.");

    public ValueTask WriteWebSerialAsync(string connectionId, byte[] payload, CancellationToken ct = default) =>
        throw new NotSupportedException("Web Serial is not available in this environment.");

    public ValueTask CloseWebSerialAsync(string connectionId, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask<bool> IsBrowserPrintAvailableAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(false);

    public ValueTask PrintViaBrowserAsync(string title, string htmlBody, CancellationToken ct = default) =>
        throw new NotSupportedException("Browser printing is not available in this environment.");
}
