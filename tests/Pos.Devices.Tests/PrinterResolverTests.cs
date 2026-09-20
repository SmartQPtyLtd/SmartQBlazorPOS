// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Devices.EscPos;
using Pos.Devices.Transport;

namespace Pos.Devices.Tests;

/// <summary>
/// Resolver tests. These encode the browser-portability requirement directly: WebUSB is
/// preferred, and the system degrades to whatever the browser can actually do rather
/// than failing at the till.
/// </summary>
public sealed class PrinterResolverTests
{
    /// <summary>A transport whose availability and readiness are scripted.</summary>
    private sealed class StubTransport(string id, bool available, bool ready) : IDeviceTransport
    {
        public string TransportId => id;

        public string DisplayName => id;

        public DeviceState State { get; private set; } = ready
            ? new DeviceState(DeviceStatus.Ready, id)
            : new DeviceState(DeviceStatus.NotConfigured, id);

        public PrinterCapabilities Capabilities { get; } =
            new(true, true, true, true, 48);

        public event Action<DeviceState>? StateChanged;

        public bool ThrowsOnProbe { get; init; }

        public ValueTask<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            if (ThrowsOnProbe)
            {
                throw new InvalidOperationException("Probe failed.");
            }

            _ = StateChanged;
            return ValueTask.FromResult(available);
        }

        public ValueTask<bool> TryReconnectAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(ready);

        public ValueTask<bool> RequestDeviceAsync(CancellationToken ct = default) => ValueTask.FromResult(ready);

        public ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisconnectAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task WebUsb_is_preferred_when_it_is_connected()
    {
        // The stated requirement: prefer WebUSB, since Chromium/Edge dominates.
        var resolver = new PrinterResolver()
            .Add(1, "WebUSB", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("webusb", true, true)))
            .Add(5, "Simulated", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("simulated", true, true)));

        var selected = await resolver.ResolveAsync();

        Assert.NotNull(selected);
        Assert.Equal("webusb", selected.TransportId);
    }

    [Fact]
    public async Task An_unavailable_preferred_transport_is_skipped()
    {
        // This is the Firefox and Safari case: WebUSB simply does not exist there.
        var resolver = new PrinterResolver()
            .Add(1, "WebUSB", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("webusb", false, false)))
            .Add(5, "Simulated", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("simulated", true, true)));

        var selected = await resolver.ResolveAsync();

        Assert.NotNull(selected);
        Assert.Equal("simulated", selected.TransportId);
    }

    [Fact]
    public async Task A_connected_fallback_beats_an_unpaired_preferred_transport()
    {
        // WebUSB is the better transport, but a printer that is not paired cannot print.
        // Preferring it here would leave the till unable to produce a receipt.
        var resolver = new PrinterResolver()
            .Add(1, "WebUSB", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("webusb", true, false)))
            .Add(2, "Web Serial", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("webserial", true, true)));

        var selected = await resolver.ResolveAsync();

        Assert.NotNull(selected);
        Assert.Equal("webserial", selected.TransportId);
    }

    [Fact]
    public async Task An_available_but_unpaired_transport_is_still_reported_when_nothing_else_works()
    {
        // The operator should be offered the best transport and asked to pair it, rather
        // than silently dropped to a lesser one forever.
        var resolver = new PrinterResolver()
            .Add(1, "WebUSB", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("webusb", true, false)));

        var selected = await resolver.ResolveAsync();

        Assert.NotNull(selected);
        Assert.Equal("webusb", selected.TransportId);
        Assert.Contains("not yet paired", resolver.SelectionReason);
    }

    [Fact]
    public async Task A_transport_that_throws_while_probing_does_not_block_the_next_one()
    {
        // A broken browser API must not be able to stop a sale.
        var resolver = new PrinterResolver()
            .Add(1, "Broken", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("broken", true, true) { ThrowsOnProbe = true }))
            .Add(2, "Simulated", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("simulated", true, true)));

        var selected = await resolver.ResolveAsync();

        Assert.NotNull(selected);
        Assert.Equal("simulated", selected.TransportId);
    }

    [Fact]
    public async Task A_null_transport_from_a_factory_is_ignored()
    {
        var resolver = new PrinterResolver()
            .Add(1, "Absent", _ => ValueTask.FromResult<IDeviceTransport?>(null))
            .Add(2, "Simulated", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("simulated", true, true)));

        var selected = await resolver.ResolveAsync();

        Assert.NotNull(selected);
        Assert.Equal("simulated", selected.TransportId);
    }

    [Fact]
    public async Task With_nothing_usable_the_resolver_returns_null()
    {
        var resolver = new PrinterResolver()
            .Add(1, "WebUSB", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("webusb", false, false)));

        Assert.Null(await resolver.ResolveAsync());
        Assert.Contains("No printer transport", resolver.DescribeSelection());
    }

    [Fact]
    public async Task PreferReconnect_false_selects_a_transport_without_requiring_a_paired_device()
    {
        // Used when opening device settings, where the operator intends to pair.
        var resolver = new PrinterResolver()
            .Add(1, "WebUSB", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("webusb", true, false)));

        var selected = await resolver.ResolveAsync(preferReconnect: false);

        Assert.NotNull(selected);
        Assert.Equal("webusb", selected.TransportId);
    }

    [Fact]
    public async Task A_browser_with_no_device_apis_falls_back_to_browser_printing()
    {
        // The Safari and Firefox case. Neither will ever ship WebUSB, so the till must still
        // be able to hand the customer something.
        var resolver = new PrinterResolver()
            .Add(1, "WebUSB", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("webusb", false, false)))
            .Add(2, "Web Serial", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("webserial", false, false)))
            .Add(3, "Browser print", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("browserprint", true, true)));

        var selected = await resolver.ResolveAsync();

        Assert.NotNull(selected);
        Assert.Equal("browserprint", selected.TransportId);
    }

    [Fact]
    public async Task Web_serial_is_preferred_over_browser_printing_where_both_work()
    {
        // Browser printing is a genuine degradation — no cut, no drawer — so it must only be
        // chosen when nothing better exists.
        var resolver = new PrinterResolver()
            .Add(2, "Web Serial", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("webserial", true, true)))
            .Add(3, "Browser print", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("browserprint", true, true)));

        var selected = await resolver.ResolveAsync();

        Assert.NotNull(selected);
        Assert.Equal("webserial", selected.TransportId);
    }

    [Fact]
    public async Task Webusb_is_still_preferred_when_every_transport_is_available()
    {
        // The full preference chain in one assertion: WebUSB wins wherever it exists.
        var resolver = new PrinterResolver()
            .Add(1, "WebUSB", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("webusb", true, true)))
            .Add(2, "Web Serial", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("webserial", true, true)))
            .Add(3, "Browser print", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("browserprint", true, true)))
            .Add(100, "Simulated", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("simulated", true, true)));

        var selected = await resolver.ResolveAsync();

        Assert.NotNull(selected);
        Assert.Equal("webusb", selected.TransportId);
    }

    [Fact]
    public async Task A_browser_with_no_device_apis_and_no_print_falls_back_to_simulated()
    {
        // The last resort, so a terminal is never left with no printer at all.
        var resolver = new PrinterResolver()
            .Add(1, "WebUSB", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("webusb", false, false)))
            .Add(3, "Browser print", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("browserprint", false, false)))
            .Add(100, "Simulated", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("simulated", true, true)));

        var selected = await resolver.ResolveAsync();

        Assert.NotNull(selected);
        Assert.Equal("simulated", selected.TransportId);
    }

    [Fact]
    public async Task The_selection_can_be_described_for_the_device_settings_screen()
    {
        var resolver = new PrinterResolver()
            .Add(1, "WebUSB", _ => ValueTask.FromResult<IDeviceTransport?>(new StubTransport("webusb", true, true)));

        await resolver.ResolveAsync();

        var description = resolver.DescribeSelection();
        Assert.Contains("webusb", description);
        Assert.Contains("Ready", description);
    }
}

/// <summary>
/// Tests for the simulated transport, which is the development and CI path: with no
/// thermal hardware available on a build agent, this is how a receipt is verified.
/// </summary>
public sealed class SimulatedTransportTests
{
    [Fact]
    public async Task The_simulated_printer_is_always_available()
    {
        await using var transport = new SimulatedTransport();

        Assert.True(await transport.IsAvailableAsync());
        Assert.Equal(DeviceStatus.Ready, transport.State.Status);
    }

    [Fact]
    public async Task Jobs_are_recorded_with_their_exact_bytes()
    {
        await using var transport = new SimulatedTransport();
        var payload = new byte[] { 0x1B, 0x40, 0x41, 0x0A };

        await transport.WriteAsync(payload);

        var job = Assert.Single(transport.Jobs);
        Assert.Equal(payload, job.Bytes);
    }

    [Fact]
    public async Task Recorded_jobs_expose_readable_text()
    {
        // Lets a test or an operator log read the receipt instead of a wall of hex.
        await using var transport = new SimulatedTransport();

        var bytes = new EscPosBuilder()
            .Initialise()
            .Line("HELLO")
            .Line("WORLD")
            .Cut()
            .ToArray();

        await transport.WriteAsync(bytes);

        var text = transport.LastJob!.Text;
        Assert.Contains("HELLO", text);
        Assert.Contains("WORLD", text);

        // Command bytes must be stripped, leaving only printable content. The escape
        // character is written as '\u001b' in a normal string, not a verbatim one, so
        // this asserts on the control byte itself rather than the six-character text.
        Assert.DoesNotContain('\u001b', text);
        Assert.Equal("HELLO\nWORLD\n", text);
    }

    [Fact]
    public async Task Multiple_jobs_accumulate_in_order()
    {
        await using var transport = new SimulatedTransport();

        await transport.WriteAsync(new byte[] { 0x41 });
        await transport.WriteAsync(new byte[] { 0x42 });

        Assert.Equal(2, transport.Jobs.Count);
        Assert.Equal(2, transport.TotalBytes);
        Assert.Equal(new byte[] { 0x42 }, transport.LastJob!.Bytes);
    }

    [Fact]
    public async Task An_offline_printer_fails_the_write_and_reports_Disconnected()
    {
        await using var transport = new SimulatedTransport { SimulateOffline = true };

        await Assert.ThrowsAsync<IOException>(async () => await transport.WriteAsync(new byte[] { 0x41 }));

        Assert.Equal(DeviceStatus.Disconnected, transport.State.Status);
    }

    [Fact]
    public async Task A_scripted_failure_is_consumed_after_one_use()
    {
        // Allows a test to simulate a transient fault followed by recovery.
        await using var transport = new SimulatedTransport
        {
            FailNextWrite = new IOException("Paper out."),
        };

        await Assert.ThrowsAsync<IOException>(async () => await transport.WriteAsync(new byte[] { 0x41 }));
        Assert.Equal(DeviceStatus.Faulted, transport.State.Status);

        // The next write succeeds and clears the fault.
        await transport.WriteAsync(new byte[] { 0x42 });
        Assert.Equal(DeviceStatus.Ready, transport.State.Status);
        Assert.Single(transport.Jobs);
    }

    [Fact]
    public async Task Clearing_discards_recorded_jobs()
    {
        await using var transport = new SimulatedTransport();
        await transport.WriteAsync(new byte[] { 0x41 });

        transport.Clear();

        Assert.Empty(transport.Jobs);
        Assert.Null(transport.LastJob);
    }

    [Fact]
    public void Capabilities_report_a_full_featured_printer()
    {
        var transport = new SimulatedTransport();
        Assert.True(transport.Capabilities.CanCut);
        Assert.True(transport.Capabilities.CanOpenDrawer);
        Assert.Equal(48, transport.Capabilities.Columns);
    }
}
