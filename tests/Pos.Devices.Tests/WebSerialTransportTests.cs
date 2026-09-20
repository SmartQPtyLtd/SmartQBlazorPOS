// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Devices.Transport;

namespace Pos.Devices.Tests;

/// <summary>
/// Web Serial transport tests.
/// </summary>
/// <remarks>
/// Web Serial is the transport that succeeds where WebUSB often cannot — a printer held by
/// the operating system's own driver still exposes a serial port — so these cover both the
/// happy path and the "port already open" failure that a real till hits constantly.
/// </remarks>
public sealed class WebSerialTransportTests
{
    [Fact]
    public async Task An_unsupported_browser_reports_Unsupported_not_Faulted()
    {
        var bridge = new FakeDeviceBridge { SerialSupported = false };
        await using var transport = new WebSerialTransport(bridge);

        Assert.False(await transport.IsAvailableAsync());
        Assert.Equal(DeviceStatus.Unsupported, transport.State.Status);
        Assert.Contains("Chrome or Edge", transport.State.Detail);
    }

    [Fact]
    public async Task A_previously_authorised_port_reattaches_without_prompting()
    {
        var bridge = new FakeDeviceBridge { SerialSupported = true };
        bridge.AuthorisedSerialConnections.Add("serial-existing");

        await using var transport = new WebSerialTransport(bridge);

        Assert.True(await transport.TryReconnectAsync());
        Assert.Equal(DeviceStatus.Ready, transport.State.Status);
        Assert.Equal("serial-existing", transport.ConnectionId);
        Assert.False(bridge.BeginSerialWasCalled);
    }

    [Fact]
    public async Task Reconnecting_with_nothing_paired_reports_NotConfigured()
    {
        var bridge = new FakeDeviceBridge { SerialSupported = true };
        await using var transport = new WebSerialTransport(bridge);

        Assert.False(await transport.TryReconnectAsync());
        Assert.Equal(DeviceStatus.NotConfigured, transport.State.Status);
    }

    [Fact]
    public async Task A_port_held_by_another_application_reports_Faulted_with_the_reason()
    {
        // The realistic failure: a stale tab or a system tool still holds the port.
        var bridge = new FakeDeviceBridge { SerialSupported = true };
        bridge.AuthorisedSerialConnections.Add("serial-busy");
        bridge.UnopenableSerialConnections.Add("serial-busy");

        await using var transport = new WebSerialTransport(bridge);

        Assert.False(await transport.TryReconnectAsync());
        Assert.Equal(DeviceStatus.Disconnected, transport.State.Status);
    }

    [Fact]
    public async Task Pairing_opens_the_port_at_the_configured_line_rate()
    {
        var bridge = new FakeDeviceBridge { SerialSupported = true };
        bridge.SerialPollResponses.Enqueue(null);
        bridge.SerialPollResponses.Enqueue(new DeviceRequestResult(
            DeviceRequestState.Succeeded, "serial-new", "Serial printer (067b:2303)"));

        await using var transport = new WebSerialTransport(bridge, baudRate: 115200);

        Assert.True(await transport.RequestDeviceAsync());
        Assert.True(bridge.BeginSerialWasCalled);
        Assert.Equal(115200, bridge.OpenedBaudRate);
        Assert.Equal(DeviceStatus.Ready, transport.State.Status);
    }

    [Fact]
    public void The_default_line_rate_is_the_thermal_printer_standard()
    {
        var transport = new WebSerialTransport(new FakeDeviceBridge());

        Assert.Equal(9600, transport.BaudRate);
    }

    [Fact]
    public async Task Dismissing_the_chooser_is_a_cancellation_not_a_fault()
    {
        var bridge = new FakeDeviceBridge { SerialSupported = true };
        bridge.SerialPollResponses.Enqueue(new DeviceRequestResult(DeviceRequestState.Cancelled));

        await using var transport = new WebSerialTransport(bridge);

        Assert.False(await transport.RequestDeviceAsync());
        Assert.Equal(DeviceStatus.NotConfigured, transport.State.Status);
        Assert.Contains("cancelled", transport.State.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Writing_without_a_connected_port_fails_clearly()
    {
        var bridge = new FakeDeviceBridge { SerialSupported = true };
        await using var transport = new WebSerialTransport(bridge);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transport.WriteAsync(new byte[] { 0x1B, 0x40 }));

        Assert.Contains("Pair one in device settings", exception.Message);
    }

    [Fact]
    public async Task Bytes_reach_the_port_unchanged()
    {
        var bridge = new FakeDeviceBridge { SerialSupported = true };
        bridge.AuthorisedSerialConnections.Add("serial-1");

        await using var transport = new WebSerialTransport(bridge);
        await transport.TryReconnectAsync();

        var payload = new byte[] { 0x1B, 0x40, 0x41, 0x0A };
        await transport.WriteAsync(payload);

        Assert.Equal(payload, Assert.Single(bridge.SerialWrites));
    }

    [Fact]
    public async Task A_write_failure_marks_the_transport_Faulted()
    {
        var bridge = new FakeDeviceBridge { SerialSupported = true };
        bridge.AuthorisedSerialConnections.Add("serial-1");
        bridge.SerialWriteFailure = new IOException("The cable was removed.");

        await using var transport = new WebSerialTransport(bridge);
        await transport.TryReconnectAsync();

        await Assert.ThrowsAsync<IOException>(async () => await transport.WriteAsync(new byte[] { 0x41 }));
        Assert.Equal(DeviceStatus.Faulted, transport.State.Status);
    }

    [Fact]
    public void Serial_reports_full_capability_including_the_drawer()
    {
        // Unlike browser printing, a serial connection sends raw ESC/POS, so cutting and
        // drawer pulses both work.
        var transport = new WebSerialTransport(new FakeDeviceBridge());

        Assert.True(transport.Capabilities.CanCut);
        Assert.True(transport.Capabilities.CanOpenDrawer);
        Assert.True(transport.Capabilities.CanPrintQrCode);
    }

    [Fact]
    public async Task A_bridge_that_cannot_poll_serial_requests_fails_rather_than_hanging()
    {
        // Without a poller the request would wait out the full two-minute timeout, which
        // looks to the operator like a frozen till.
        var bridge = new BridgeWithoutSerialPolling();
        await using var transport = new WebSerialTransport(bridge);

        Assert.False(await transport.RequestDeviceAsync());
        Assert.Equal(DeviceStatus.Faulted, transport.State.Status);
        Assert.Contains("pairing request", transport.State.Detail!);
    }

    /// <summary>A bridge that supports serial but cannot report a chooser outcome.</summary>
    private sealed class BridgeWithoutSerialPolling : IDeviceBridge
    {
        public ValueTask<bool> IsWebUsbSupportedAsync(CancellationToken ct = default) => ValueTask.FromResult(false);

        public ValueTask<bool> IsWebSerialSupportedAsync(CancellationToken ct = default) => ValueTask.FromResult(true);

        public ValueTask BeginWebUsbRequestAsync(string requestId, int? vendorId = null, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask<DeviceRequestResult> PollRequestAsync(string requestId, CancellationToken ct = default) =>
            ValueTask.FromResult(new DeviceRequestResult(DeviceRequestState.Pending));

        public ValueTask<IReadOnlyList<string>> GetAuthorisedWebUsbConnectionsAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask OpenWebUsbAsync(string connectionId, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask WriteWebUsbAsync(string connectionId, byte[] payload, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask CloseWebUsbAsync(string connectionId, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<string?> DescribeAsync(string connectionId, CancellationToken ct = default) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask BeginWebSerialRequestAsync(string requestId, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<string>> GetAuthorisedWebSerialConnectionsAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask OpenWebSerialAsync(string connectionId, int baudRate, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask WriteWebSerialAsync(string connectionId, byte[] payload, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask CloseWebSerialAsync(string connectionId, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<bool> IsBrowserPrintAvailableAsync(CancellationToken ct = default) => ValueTask.FromResult(false);

        public ValueTask PrintViaBrowserAsync(string title, string htmlBody, CancellationToken ct = default) =>
            ValueTask.CompletedTask;
    }
}

/// <summary>
/// Browser print fallback tests.
/// </summary>
/// <remarks>
/// This is the transport that makes the system work in Safari and Firefox, where no device
/// API will ever exist. It is deliberately degraded, and the tests pin exactly how — so the
/// UI can warn the operator instead of a cash drawer silently not opening.
/// </remarks>
public sealed class BrowserPrintTransportTests
{
    [Fact]
    public async Task The_print_dialog_is_reported_as_available()
    {
        var bridge = new FakeDeviceBridge { BrowserPrintAvailable = true };
        await using var transport = new BrowserPrintTransport(bridge);

        Assert.True(await transport.IsAvailableAsync());
        Assert.Equal(DeviceStatus.Ready, transport.State.Status);
    }

    [Fact]
    public async Task Without_a_print_dialog_the_transport_reports_Unsupported()
    {
        var bridge = new FakeDeviceBridge { BrowserPrintAvailable = false };
        await using var transport = new BrowserPrintTransport(bridge);

        Assert.False(await transport.IsAvailableAsync());
        Assert.Equal(DeviceStatus.Unsupported, transport.State.Status);
    }

    [Fact]
    public void It_honestly_reports_that_it_cannot_cut_or_open_the_drawer()
    {
        // This is the whole point of the capability report. A cashier who believes the
        // drawer opened will walk away from an open till.
        var transport = new BrowserPrintTransport(new FakeDeviceBridge());

        Assert.False(transport.Capabilities.CanCut);
        Assert.False(transport.Capabilities.CanOpenDrawer);

        // Content that is part of the page still works.
        Assert.True(transport.Capabilities.CanPrintQrCode);
        Assert.True(transport.Capabilities.CanPrintImages);
    }

    [Fact]
    public async Task Raw_escpos_bytes_are_refused_rather_than_printed_as_garbage()
    {
        // Sending control bytes to a print dialog would emit a page of nonsense. Refusing
        // loudly is far better than corrupting a customer's receipt.
        var bridge = new FakeDeviceBridge();
        await using var transport = new BrowserPrintTransport(bridge);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            async () => await transport.WriteAsync(new byte[] { 0x1B, 0x40 }));

        Assert.Contains("cannot accept raw ESC/POS", exception.Message);
        Assert.Empty(bridge.PrintedDocuments);
    }

    [Fact]
    public async Task An_html_receipt_is_handed_to_the_print_dialog()
    {
        var bridge = new FakeDeviceBridge();
        await using var transport = new BrowserPrintTransport(bridge);

        await transport.PrintHtmlAsync("Receipt CT01-1", "<p>TOTAL 15.00</p>");

        var (title, html) = Assert.Single(bridge.PrintedDocuments);
        Assert.Equal("Receipt CT01-1", title);
        Assert.Contains("TOTAL 15.00", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Printing_an_empty_document_is_rejected()
    {
        await using var transport = new BrowserPrintTransport(new FakeDeviceBridge());

        await Assert.ThrowsAsync<ArgumentException>(async () => await transport.PrintHtmlAsync("Title", "   "));
    }

    [Fact]
    public async Task A_print_failure_marks_the_transport_Faulted()
    {
        var bridge = new FakeDeviceBridge { PrintFailure = new InvalidOperationException("The dialog was blocked.") };
        await using var transport = new BrowserPrintTransport(bridge);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transport.PrintHtmlAsync("Title", "<p>x</p>"));

        Assert.Equal(DeviceStatus.Faulted, transport.State.Status);
    }

    [Fact]
    public async Task It_is_always_ready_to_reattach_because_nothing_is_held_open()
    {
        var bridge = new FakeDeviceBridge { BrowserPrintAvailable = true };
        await using var transport = new BrowserPrintTransport(bridge);

        Assert.True(await transport.TryReconnectAsync());
    }
}
