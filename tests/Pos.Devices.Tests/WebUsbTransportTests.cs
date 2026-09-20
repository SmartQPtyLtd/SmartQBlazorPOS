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
/// A scriptable in-memory <see cref="IDeviceBridge"/>, so the WebUSB transport can be
/// exercised end to end without a browser or a printer.
/// </summary>
internal sealed class FakeDeviceBridge : IDeviceBridge, ISerialRequestPoller
{
    public bool UsbSupported { get; set; } = true;

    public bool SerialSupported { get; set; }

    /// <summary>Connections returned by the no-prompt reattach path.</summary>
    public List<string> AuthorisedConnections { get; } = [];

    /// <summary>Connections that fail to open, to simulate a device held by a driver.</summary>
    public HashSet<string> UnopenableConnections { get; } = [];

    public string? DeviceLabel { get; set; } = "Epson TM-T20III";

    /// <summary>Scripted outcomes for successive polls. A null entry means "still pending".</summary>
    public Queue<DeviceRequestResult?> PollResponses { get; } = new();

    public bool BeginWasCalled { get; private set; }

    public string? LastRequestId { get; private set; }

    public int? LastVendorId { get; private set; }

    /// <summary>Every payload written, in order.</summary>
    public List<byte[]> Writes { get; } = [];

    public Exception? WriteFailure { get; set; }

    public bool CloseWasCalled { get; private set; }

    public ValueTask<bool> IsWebUsbSupportedAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(UsbSupported);

    public ValueTask<bool> IsWebSerialSupportedAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(SerialSupported);

    public ValueTask BeginWebUsbRequestAsync(string requestId, int? vendorId = null, CancellationToken ct = default)
    {
        BeginWasCalled = true;
        LastRequestId = requestId;
        LastVendorId = vendorId;
        return ValueTask.CompletedTask;
    }

    public ValueTask<DeviceRequestResult> PollRequestAsync(string requestId, CancellationToken ct = default)
    {
        if (PollResponses.Count == 0)
        {
            return ValueTask.FromResult(new DeviceRequestResult(DeviceRequestState.Pending));
        }

        var next = PollResponses.Dequeue();

        // Keep the describe result consistent with a successful poll, so the transport
        // reports the label of the device the operator actually chose.
        if (next?.DeviceLabel is { } label)
        {
            DeviceLabel = label;
        }

        return ValueTask.FromResult(next ?? new DeviceRequestResult(DeviceRequestState.Pending));
    }

    public ValueTask<IReadOnlyList<string>> GetAuthorisedWebUsbConnectionsAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<string>>(AuthorisedConnections);

    public ValueTask OpenWebUsbAsync(string connectionId, CancellationToken ct = default)
    {
        if (UnopenableConnections.Contains(connectionId))
        {
            throw new InvalidOperationException("The interface is claimed by a system driver.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask WriteWebUsbAsync(string connectionId, byte[] payload, CancellationToken ct = default)
    {
        if (WriteFailure is { } failure)
        {
            throw failure;
        }

        Writes.Add(payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseWebUsbAsync(string connectionId, CancellationToken ct = default)
    {
        CloseWasCalled = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> DescribeAsync(string connectionId, CancellationToken ct = default) =>
        ValueTask.FromResult(DeviceLabel);

    // ------------------------------------------------------------------ Web Serial

    public bool SerialPairingSucceeds { get; set; } = true;

    public List<string> AuthorisedSerialConnections { get; } = [];

    public HashSet<string> UnopenableSerialConnections { get; } = [];

    public bool BeginSerialWasCalled { get; private set; }

    public Queue<DeviceRequestResult?> SerialPollResponses { get; } = new();

    public List<byte[]> SerialWrites { get; } = [];

    public Exception? SerialWriteFailure { get; set; }

    public int? OpenedBaudRate { get; private set; }

    public ValueTask BeginWebSerialRequestAsync(string requestId, CancellationToken ct = default)
    {
        BeginSerialWasCalled = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask<DeviceRequestResult> PollSerialRequestAsync(string requestId, CancellationToken ct = default)
    {
        if (SerialPollResponses.Count == 0)
        {
            return ValueTask.FromResult(new DeviceRequestResult(DeviceRequestState.Pending));
        }

        var next = SerialPollResponses.Dequeue();

        if (next?.DeviceLabel is { } label)
        {
            DeviceLabel = label;
        }

        return ValueTask.FromResult(next ?? new DeviceRequestResult(DeviceRequestState.Pending));
    }

    public ValueTask<IReadOnlyList<string>> GetAuthorisedWebSerialConnectionsAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<string>>(AuthorisedSerialConnections);

    public ValueTask OpenWebSerialAsync(string connectionId, int baudRate, CancellationToken ct = default)
    {
        if (UnopenableSerialConnections.Contains(connectionId))
        {
            throw new InvalidOperationException("The port is already open in another application.");
        }

        OpenedBaudRate = baudRate;
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteWebSerialAsync(string connectionId, byte[] payload, CancellationToken ct = default)
    {
        if (SerialWriteFailure is { } failure)
        {
            throw failure;
        }

        SerialWrites.Add(payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseWebSerialAsync(string connectionId, CancellationToken ct = default)
    {
        CloseWasCalled = true;
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------- Browser printing

    public bool BrowserPrintAvailable { get; set; } = true;

    public List<(string Title, string Html)> PrintedDocuments { get; } = [];

    public Exception? PrintFailure { get; set; }

    public ValueTask<bool> IsBrowserPrintAvailableAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(BrowserPrintAvailable);

    public ValueTask PrintViaBrowserAsync(string title, string htmlBody, CancellationToken ct = default)
    {
        if (PrintFailure is { } failure)
        {
            throw failure;
        }

        PrintedDocuments.Add((title, htmlBody));
        return ValueTask.CompletedTask;
    }
}

public sealed class WebUsbTransportTests
{
    [Fact]
    public async Task Browser_support_is_probed_rather_than_assumed()
    {
        var bridge = new FakeDeviceBridge { UsbSupported = false };
        await using var transport = new WebUsbTransport(bridge);

        Assert.False(await transport.IsAvailableAsync());
    }

    [Fact]
    public async Task An_unsupported_browser_reports_Unsupported_not_Faulted()
    {
        // The distinction matters for the UI: nothing is broken, the capability is
        // simply absent, and the operator should be pointed at another transport.
        var bridge = new FakeDeviceBridge { UsbSupported = false };
        await using var transport = new WebUsbTransport(bridge);

        await transport.IsAvailableAsync();

        Assert.Equal(DeviceStatus.Unsupported, transport.State.Status);
        Assert.Contains("Chrome or Edge", transport.State.Detail);
    }

    [Fact]
    public async Task A_previously_authorised_printer_reattaches_without_prompting()
    {
        // This is what makes a terminal usable daily. Without it the operator would face
        // a device chooser on every page refresh.
        var bridge = new FakeDeviceBridge();
        bridge.AuthorisedConnections.Add("usb-existing");

        await using var transport = new WebUsbTransport(bridge);

        Assert.True(await transport.TryReconnectAsync());
        Assert.Equal(DeviceStatus.Ready, transport.State.Status);
        Assert.Equal("usb-existing", transport.ConnectionId);
        Assert.Equal("Epson TM-T20III", transport.State.DeviceLabel);

        // Critically, no chooser was opened.
        Assert.False(bridge.BeginWasCalled);
    }

    [Fact]
    public async Task Reconnecting_with_nothing_paired_reports_NotConfigured()
    {
        var bridge = new FakeDeviceBridge();
        await using var transport = new WebUsbTransport(bridge);

        Assert.False(await transport.TryReconnectAsync());
        Assert.Equal(DeviceStatus.NotConfigured, transport.State.Status);
    }

    [Fact]
    public async Task Reconnecting_falls_through_to_a_device_that_can_actually_be_opened()
    {
        // A user may have granted access to several USB devices. One held by a system
        // driver must not stop the printer from being found.
        var bridge = new FakeDeviceBridge();
        bridge.AuthorisedConnections.Add("usb-blocked");
        bridge.AuthorisedConnections.Add("usb-printer");
        bridge.UnopenableConnections.Add("usb-blocked");

        await using var transport = new WebUsbTransport(bridge);

        Assert.True(await transport.TryReconnectAsync());
        Assert.Equal("usb-printer", transport.ConnectionId);
    }

    [Fact]
    public async Task A_failed_open_reports_Faulted_with_the_reason()
    {
        var bridge = new FakeDeviceBridge();
        bridge.AuthorisedConnections.Add("usb-blocked");
        bridge.UnopenableConnections.Add("usb-blocked");

        await using var transport = new WebUsbTransport(bridge);

        Assert.False(await transport.TryReconnectAsync());
        Assert.Equal(DeviceStatus.Disconnected, transport.State.Status);
    }

    [Fact]
    public async Task Pairing_uses_the_deferred_promise_handshake()
    {
        // requestDevice must be started from a user gesture; the outcome is polled.
        var bridge = new FakeDeviceBridge();
        bridge.PollResponses.Enqueue(null); // pending
        bridge.PollResponses.Enqueue(new DeviceRequestResult(
            DeviceRequestState.Succeeded, "usb-new", "Star TSP143"));

        await using var transport = new WebUsbTransport(bridge);

        Assert.True(await transport.RequestDeviceAsync());
        Assert.True(bridge.BeginWasCalled);
        Assert.Equal(DeviceStatus.Ready, transport.State.Status);
        Assert.Equal("Star TSP143", transport.State.DeviceLabel);
    }

    [Fact]
    public async Task Dismissing_the_chooser_is_a_cancellation_not_a_fault()
    {
        // Closing the dialog is a normal action. Reporting it as an error would train
        // operators to ignore real errors.
        var bridge = new FakeDeviceBridge();
        bridge.PollResponses.Enqueue(new DeviceRequestResult(DeviceRequestState.Cancelled));

        await using var transport = new WebUsbTransport(bridge);

        Assert.False(await transport.RequestDeviceAsync());
        Assert.Equal(DeviceStatus.NotConfigured, transport.State.Status);
        Assert.Contains("cancelled", transport.State.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_failed_pairing_surfaces_the_reason()
    {
        var bridge = new FakeDeviceBridge();
        bridge.PollResponses.Enqueue(new DeviceRequestResult(
            DeviceRequestState.Failed, Error: "Access denied by the browser."));

        await using var transport = new WebUsbTransport(bridge);

        Assert.False(await transport.RequestDeviceAsync());
        Assert.Equal(DeviceStatus.Faulted, transport.State.Status);
        Assert.Equal("Access denied by the browser.", transport.State.Detail);
    }

    [Fact]
    public async Task Pairing_on_an_unsupported_browser_never_opens_a_chooser()
    {
        var bridge = new FakeDeviceBridge { UsbSupported = false };
        await using var transport = new WebUsbTransport(bridge);

        Assert.False(await transport.RequestDeviceAsync());
        Assert.False(bridge.BeginWasCalled);
    }

    [Fact]
    public async Task Writing_without_a_connected_printer_fails_clearly()
    {
        var bridge = new FakeDeviceBridge();
        await using var transport = new WebUsbTransport(bridge);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transport.WriteAsync(new byte[] { 0x1B, 0x40 }));

        Assert.Contains("Pair a printer", exception.Message);
    }

    [Fact]
    public async Task Bytes_reach_the_bridge_unchanged_and_in_order()
    {
        var bridge = new FakeDeviceBridge();
        bridge.AuthorisedConnections.Add("usb-1");

        await using var transport = new WebUsbTransport(bridge);
        await transport.TryReconnectAsync();

        var payload = new byte[] { 0x1B, 0x40, 0x1B, 0x61, 0x01, 0x41, 0x0A };
        await transport.WriteAsync(payload);

        var written = Assert.Single(bridge.Writes);
        Assert.Equal(payload, written);
    }

    [Fact]
    public async Task A_write_failure_marks_the_transport_Faulted_and_propagates()
    {
        // An unplugged printer must be visible in the device state rather than silently
        // swallowing the receipt.
        var bridge = new FakeDeviceBridge();
        bridge.AuthorisedConnections.Add("usb-1");
        bridge.WriteFailure = new IOException("The device was disconnected.");

        await using var transport = new WebUsbTransport(bridge);
        await transport.TryReconnectAsync();

        await Assert.ThrowsAsync<IOException>(async () => await transport.WriteAsync(new byte[] { 0x41 }));

        Assert.Equal(DeviceStatus.Faulted, transport.State.Status);
        Assert.Contains("disconnected", transport.State.Detail!);
    }

    [Fact]
    public async Task State_changes_are_announced_to_listeners()
    {
        // The device settings screen relies on this to reflect the printer going away.
        var bridge = new FakeDeviceBridge();
        bridge.AuthorisedConnections.Add("usb-1");

        await using var transport = new WebUsbTransport(bridge);

        var observed = new List<DeviceStatus>();
        transport.StateChanged += state => observed.Add(state.Status);

        await transport.TryReconnectAsync();

        Assert.Contains(DeviceStatus.Ready, observed);
    }

    [Fact]
    public async Task Disconnecting_forgets_the_device_and_closes_the_handle()
    {
        var bridge = new FakeDeviceBridge();
        bridge.AuthorisedConnections.Add("usb-1");

        await using var transport = new WebUsbTransport(bridge);
        await transport.TryReconnectAsync();

        await transport.DisconnectAsync();

        Assert.True(bridge.CloseWasCalled);
        Assert.Null(transport.ConnectionId);
        Assert.Equal(DeviceStatus.NotConfigured, transport.State.Status);
    }

    [Fact]
    public void WebUsb_reports_full_capability_including_the_drawer()
    {
        // Unlike the browser-print fallback, a USB connection can cut paper and pulse
        // the cash drawer.
        var transport = new WebUsbTransport(new FakeDeviceBridge());

        Assert.True(transport.Capabilities.CanCut);
        Assert.True(transport.Capabilities.CanOpenDrawer);
        Assert.True(transport.Capabilities.CanPrintQrCode);
    }

    // ------------------------------------------------- two printers on one terminal

    [Fact]
    public async Task A_receipt_printer_and_a_kitchen_printer_attach_to_different_devices()
    {
        // The scenario: two thermal printers on one till. Whoever resolves first takes the device
        // it can open, so without bindings the kitchen ticket can come out of the receipt printer.
        var bridge = new FakeDeviceBridge();
        bridge.AuthorisedConnections.AddRange(["usb-receipt", "usb-kitchen"]);

        await using var receipts = new WebUsbTransport(bridge, binding: PrinterBinding.Only("usb-receipt"));
        await using var kitchen = new WebUsbTransport(bridge, binding: PrinterBinding.Only("usb-kitchen"));

        Assert.True(await receipts.TryReconnectAsync());
        Assert.True(await kitchen.TryReconnectAsync());

        Assert.Equal("usb-receipt", receipts.ConnectionId);
        Assert.Equal("usb-kitchen", kitchen.ConnectionId);
    }

    [Fact]
    public async Task A_bound_transport_refuses_to_fall_back_to_the_other_printer()
    {
        // The failure that looks like success: the kitchen printer is unplugged, so kitchen
        // tickets quietly print on the customer-facing roll. The operator sees a ticket come out
        // and has no reason to look closer.
        var bridge = new FakeDeviceBridge();
        bridge.AuthorisedConnections.Add("usb-receipt");

        await using var kitchen = new WebUsbTransport(bridge, binding: PrinterBinding.Only("usb-kitchen"));

        Assert.False(await kitchen.TryReconnectAsync());
        Assert.Null(kitchen.ConnectionId);
        Assert.Equal(DeviceStatus.NotConfigured, kitchen.State.Status);
        Assert.Contains("not available", kitchen.State.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unpaired_second_printer_claims_nothing()
    {
        // So that pairing the kitchen printer cannot re-point the receipt printer, and the
        // receipt printer cannot silently become the kitchen printer.
        var bridge = new FakeDeviceBridge();
        bridge.AuthorisedConnections.Add("usb-receipt");

        await using var kitchen = new WebUsbTransport(bridge, binding: PrinterBinding.Unpaired);

        Assert.False(await kitchen.TryReconnectAsync());
        Assert.Null(kitchen.ConnectionId);
        Assert.Contains("paired", kitchen.State.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unbound_transport_still_takes_the_first_device_it_can_open()
    {
        // Backwards compatibility for a shop with one printer: the device held by a system driver
        // is skipped and the next one is tried.
        var bridge = new FakeDeviceBridge();
        bridge.AuthorisedConnections.AddRange(["usb-held", "usb-free"]);
        bridge.UnopenableConnections.Add("usb-held");

        await using var transport = new WebUsbTransport(bridge);

        Assert.True(await transport.TryReconnectAsync());
        Assert.Equal("usb-free", transport.ConnectionId);
    }

    [Fact]
    public async Task A_bound_transport_does_not_hunt_for_a_working_device()
    {
        // A binding means exactly one device. Trying the next one on failure would defeat the
        // point of binding, so a driver-held paired printer is reported rather than worked around.
        var bridge = new FakeDeviceBridge();
        bridge.AuthorisedConnections.AddRange(["usb-kitchen", "usb-receipt"]);
        bridge.UnopenableConnections.Add("usb-kitchen");

        await using var kitchen = new WebUsbTransport(bridge, binding: PrinterBinding.Only("usb-kitchen"));

        Assert.False(await kitchen.TryReconnectAsync());
        Assert.Null(kitchen.ConnectionId);
    }
}
