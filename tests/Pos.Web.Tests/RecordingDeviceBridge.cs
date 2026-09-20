// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Devices.Transport;

namespace Pos.Web.Tests;

/// <summary>
/// A device bridge that records what would have been sent to each printer.
/// </summary>
/// <remarks>
/// <para>
/// Stands in for the browser's device APIs so a whole sale can be rung up in a test and the bytes
/// that reached each printer inspected afterwards. Without this, the printers are verified only
/// through their renderers — which proves what a receipt <em>looks like</em> and nothing about
/// whether the till ever sends one, to which machine, or whether the drawer is kicked.
/// </para>
/// <para>
/// Connections are named, and each keeps its own list of writes, because the interesting questions
/// are all about routing: did the kitchen ticket go to the kitchen printer, did ZPL go anywhere near
/// the receipt printer, did the receipt printer get a cut and the label printer not.
/// </para>
/// </remarks>
internal sealed class RecordingDeviceBridge : IDeviceBridge, ISerialRequestPoller
{
    /// <summary>Every payload written, per connection, in order.</summary>
    public Dictionary<string, List<byte[]>> Writes { get; } = new(StringComparer.Ordinal);

    /// <summary>Connections the browser already has permission for, so reattachment needs no prompt.</summary>
    public List<string> AuthorisedConnections { get; } = [];

    /// <summary>Friendly names, as the browser would report them.</summary>
    public Dictionary<string, string> Labels { get; } = new(StringComparer.Ordinal);

    /// <summary>False to simulate Safari or Firefox, where neither device API exists.</summary>
    public bool UsbSupported { get; set; } = true;

    public bool SerialSupported { get; set; }

    /// <summary>Connections that refuse to open, as when a driver has claimed the interface.</summary>
    public HashSet<string> UnopenableConnections { get; } = new(StringComparer.Ordinal);

    /// <summary>Set to make every write fail, as when a printer is switched off mid-shift.</summary>
    public Exception? WriteFailure { get; set; }

    /// <summary>Pages sent through the browser's own print dialog, as (title, body).</summary>
    public List<(string Title, string Body)> BrowserPrints { get; } = [];

    public bool BrowserPrintAvailable { get; set; } = true;

    /// <summary>Everything written to one connection, as one blob.</summary>
    public byte[] BytesFor(string connectionId) =>
        Writes.TryGetValue(connectionId, out var payloads)
            ? [.. payloads.SelectMany(p => p)]
            : [];

    /// <summary>
    /// What was written to a connection, rendered for reading.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Printable ASCII is kept as text; every other byte is escaped as its hex value. The control
    /// bytes are the point — a cut is <c>GS V</c> and a drawer kick is <c>ESC p 0 25 250</c> — so a
    /// rendering that dropped them could not tell a receipt that was cut from one that was not.
    /// </para>
    /// <para>
    /// Deliberately not a full CP437 decode. The app writes product names and prices as ASCII, and a
    /// reverse lookup table here would be a second implementation of the code page to keep in step
    /// with the first, for the benefit of a test that only ever asserts on ASCII.
    /// </para>
    /// </remarks>
    public string TextFor(string connectionId)
    {
        var bytes = BytesFor(connectionId);
        var text = new System.Text.StringBuilder(bytes.Length);

        foreach (var b in bytes)
        {
            if (b is >= 0x20 and <= 0x7e)
            {
                text.Append((char)b);
            }
            else if (b == (byte)'\n')
            {
                text.Append('\n');
            }
            else
            {
                text.Append(
                    System.Globalization.CultureInfo.InvariantCulture, $"<{b:X2}>");
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Named ESC/POS sequences, so an assertion reads as what it means.
    /// </summary>
    /// <remarks>
    /// Written the way <see cref="TextFor"/> renders them, which keeps printable bytes as characters.
    /// <c>GS V</c> is therefore <c>&lt;1D&gt;V</c> and not <c>&lt;1D&gt;&lt;56&gt;</c> — a difference
    /// that cost an hour the first time this file was written, and is worth the note.
    /// </remarks>
    public static class Esc
    {
        /// <summary><c>GS V</c> — cut the paper. The cut mode byte follows.</summary>
        public const string Cut = "<1D>V";

        /// <summary><c>ESC p 0 25 250</c> — the standard cash-drawer kick on pin 2.</summary>
        public const string DrawerKick = "<1B>p<00><19><FA>";

        /// <summary><c>ESC @</c> — initialise the printer.</summary>
        public const string Initialise = "<1B>@";

        /// <summary><c>^XA</c> — the start of a ZPL label.</summary>
        public const string ZplStart = "^XA";
    }

    // ------------------------------------------------------------------ capability probes

    public ValueTask<bool> IsWebUsbSupportedAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(UsbSupported);

    public ValueTask<bool> IsWebSerialSupportedAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(SerialSupported);

    public ValueTask<bool> IsBrowserPrintAvailableAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(BrowserPrintAvailable);

    public ValueTask PrintViaBrowserAsync(string title, string htmlBody, CancellationToken ct = default)
    {
        BrowserPrints.Add((title, htmlBody));

        return ValueTask.CompletedTask;
    }

    // ---------------------------------------------------------------------------- WebUSB

    public ValueTask BeginWebUsbRequestAsync(
        string requestId,
        int? vendorId = null,
        CancellationToken ct = default) =>
        // No operator is present to click "connect", so a request that needs a prompt cannot
        // succeed. A test pairs its printers up front, the way a shop does in device settings.
        ValueTask.CompletedTask;

    public ValueTask<DeviceRequestResult> PollRequestAsync(
        string requestId,
        CancellationToken ct = default) =>
        ValueTask.FromResult(new DeviceRequestResult(DeviceRequestState.Pending));

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

    public ValueTask WriteWebUsbAsync(string connectionId, byte[] payload, CancellationToken ct = default) =>
        Record(connectionId, payload);

    public ValueTask CloseWebUsbAsync(string connectionId, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask<string?> DescribeAsync(string connectionId, CancellationToken ct = default) =>
        ValueTask.FromResult(Labels.TryGetValue(connectionId, out var label) ? label : null);

    // ----------------------------------------------------------------------- Web Serial

    public ValueTask BeginWebSerialRequestAsync(string requestId, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask<DeviceRequestResult> PollSerialRequestAsync(
        string requestId,
        CancellationToken ct = default) =>
        ValueTask.FromResult(new DeviceRequestResult(DeviceRequestState.Pending));

    public ValueTask<IReadOnlyList<string>> GetAuthorisedWebSerialConnectionsAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<string>>([]);

    public ValueTask OpenWebSerialAsync(
        string connectionId,
        int baudRate,
        CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask WriteWebSerialAsync(string connectionId, byte[] payload, CancellationToken ct = default) =>
        Record(connectionId, payload);

    public ValueTask CloseWebSerialAsync(string connectionId, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    private ValueTask Record(string connectionId, byte[] payload)
    {
        if (WriteFailure is { } failure)
        {
            throw failure;
        }

        if (!Writes.TryGetValue(connectionId, out var payloads))
        {
            payloads = [];
            Writes[connectionId] = payloads;
        }

        payloads.Add(payload);

        return ValueTask.CompletedTask;
    }
}
