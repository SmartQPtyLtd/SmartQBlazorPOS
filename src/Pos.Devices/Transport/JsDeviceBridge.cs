// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Microsoft.JSInterop;

namespace Pos.Devices.Transport;

/// <summary>
/// <see cref="IDeviceBridge"/> backed by the browser, via <c>device-bridge.js</c>.
/// </summary>
/// <remarks>
/// Thin by design: it only marshals arguments and results. All device handling lives in
/// JavaScript because a live <c>USBDevice</c> cannot cross into .NET.
/// </remarks>
public sealed class JsDeviceBridge(IJSRuntime js) : IDeviceBridge, ISerialRequestPoller
{
    private const string ModulePath = "./js/device-bridge.js";

    private readonly IJSRuntime _js = js ?? throw new ArgumentNullException(nameof(js));
    private IJSObjectReference? _module;

    private async ValueTask<IJSObjectReference> ModuleAsync(CancellationToken ct)
    {
        // Resolved once per application lifetime; module import is not free and the
        // bridge is called on every print.
        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", ct, ModulePath)
            .ConfigureAwait(false);

        return _module;
    }

    public async ValueTask<bool> IsWebUsbSupportedAsync(CancellationToken ct = default)
    {
        try
        {
            var module = await ModuleAsync(ct).ConfigureAwait(false);
            return await module.InvokeAsync<bool>("isWebUsbSupported", ct).ConfigureAwait(false);
        }
        catch (JSException)
        {
            // A missing or unloadable module means the capability is simply absent.
            // Reported as unsupported rather than thrown, so the resolver moves on.
            return false;
        }
    }

    public async ValueTask<bool> IsWebSerialSupportedAsync(CancellationToken ct = default)
    {
        try
        {
            var module = await ModuleAsync(ct).ConfigureAwait(false);
            return await module.InvokeAsync<bool>("isWebSerialSupported", ct).ConfigureAwait(false);
        }
        catch (JSException)
        {
            return false;
        }
    }

    public async ValueTask BeginWebUsbRequestAsync(
        string requestId,
        int? vendorId = null,
        CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        await module.InvokeVoidAsync("beginWebUsbRequest", ct, requestId, vendorId).ConfigureAwait(false);
    }

    public async ValueTask<DeviceRequestResult> PollRequestAsync(string requestId, CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);

        var dto = await module
            .InvokeAsync<DeviceRequestDto>("pollRequest", ct, requestId)
            .ConfigureAwait(false);

        return new DeviceRequestResult(ParseState(dto.State), dto.ConnectionId, dto.Label, dto.Error);
    }

    public async ValueTask<IReadOnlyList<string>> GetAuthorisedWebUsbConnectionsAsync(CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);

        var ids = await module
            .InvokeAsync<string[]>("getAuthorisedConnections", ct)
            .ConfigureAwait(false);

        return ids;
    }

    public async ValueTask OpenWebUsbAsync(string connectionId, CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        await module.InvokeVoidAsync("openWebUsb", ct, connectionId).ConfigureAwait(false);
    }

    public async ValueTask WriteWebUsbAsync(string connectionId, byte[] payload, CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);

        // 'send' is not universal; the array crosses as a plain JS array and is
        // converted to Uint8Array on the JavaScript side.
        await module.InvokeVoidAsync("writeWebUsb", ct, connectionId, payload).ConfigureAwait(false);
    }

    public async ValueTask CloseWebUsbAsync(string connectionId, CancellationToken ct = default)
    {
        if (_module is null)
        {
            return;
        }

        try
        {
            await _module.InvokeVoidAsync("closeWebUsb", ct, connectionId).ConfigureAwait(false);
        }
        catch (JSException)
        {
            // The page may be navigating away; there is nothing useful to do.
        }
    }

    public async ValueTask<string?> DescribeAsync(string connectionId, CancellationToken ct = default)
    {
        try
        {
            var module = await ModuleAsync(ct).ConfigureAwait(false);
            return await module.InvokeAsync<string?>("describeConnection", ct, connectionId).ConfigureAwait(false);
        }
        catch (JSException)
        {
            return null;
        }
    }

    /// <summary>Shape of the object returned by <c>pollRequest</c>.</summary>
    private sealed record DeviceRequestDto(string State, string? ConnectionId, string? Label, string? Error);

    // ------------------------------------------------------------------ Web Serial

    public async ValueTask BeginWebSerialRequestAsync(string requestId, CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        await module.InvokeVoidAsync("beginWebSerialRequest", ct, requestId).ConfigureAwait(false);
    }

    public async ValueTask<DeviceRequestResult> PollSerialRequestAsync(string requestId, CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);

        var dto = await module
            .InvokeAsync<DeviceRequestDto>("pollSerialRequest", ct, requestId)
            .ConfigureAwait(false);

        return new DeviceRequestResult(ParseState(dto.State), dto.ConnectionId, dto.Label, dto.Error);
    }

    public async ValueTask<IReadOnlyList<string>> GetAuthorisedWebSerialConnectionsAsync(CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);

        return await module
            .InvokeAsync<string[]>("getAuthorisedWebSerialConnections", ct)
            .ConfigureAwait(false);
    }

    public async ValueTask OpenWebSerialAsync(string connectionId, int baudRate, CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        await module.InvokeVoidAsync("openWebSerial", ct, connectionId, baudRate).ConfigureAwait(false);
    }

    public async ValueTask WriteWebSerialAsync(string connectionId, byte[] payload, CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        await module.InvokeVoidAsync("writeWebSerial", ct, connectionId, payload).ConfigureAwait(false);
    }

    public async ValueTask CloseWebSerialAsync(string connectionId, CancellationToken ct = default)
    {
        if (_module is null)
        {
            return;
        }

        try
        {
            await _module.InvokeVoidAsync("closeWebSerial", ct, connectionId).ConfigureAwait(false);
        }
        catch (JSException)
        {
            // The page may be navigating away.
        }
    }

    // ------------------------------------------------------------- Browser printing

    public async ValueTask<bool> IsBrowserPrintAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var module = await ModuleAsync(ct).ConfigureAwait(false);
            return await module.InvokeAsync<bool>("isBrowserPrintAvailable", ct).ConfigureAwait(false);
        }
        catch (JSException)
        {
            return false;
        }
    }

    public async ValueTask PrintViaBrowserAsync(string title, string htmlBody, CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct).ConfigureAwait(false);
        await module.InvokeVoidAsync("printViaBrowser", ct, title, htmlBody).ConfigureAwait(false);
    }

    private static DeviceRequestState ParseState(string state) => state switch
    {
        "pending" => DeviceRequestState.Pending,
        "succeeded" => DeviceRequestState.Succeeded,
        "cancelled" => DeviceRequestState.Cancelled,
        "failed" => DeviceRequestState.Failed,
        _ => DeviceRequestState.Unknown,
    };
}
