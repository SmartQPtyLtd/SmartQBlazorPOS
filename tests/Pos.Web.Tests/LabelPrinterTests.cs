// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Text;
using Microsoft.JSInterop;
using Microsoft.Extensions.Logging.Abstractions;
using Pos.Devices.EscPos;
using Pos.Devices.Transport;
using Pos.Devices.Zpl;
using Pos.Web.Terminal;

namespace Pos.Web.Tests;

/// <summary>
/// A JS runtime that answers only the local-storage calls the label settings make.
/// </summary>
/// <remarks>
/// <c>import</c> returns a reference that accepts any call and answers with a default value, so a
/// component that loads a module — the customer display and the label printer both do — can be
/// rendered without a browser. A null reference would instead look like a broken page.
/// </remarks>
internal sealed class FakeJsRuntime(
    Dictionary<string, string?>? storage = null,
    Dictionary<string, string?>? sessionStorage = null) : IJSRuntime
{
    private readonly Dictionary<string, string?> _storage = storage ?? [];

    // Session storage is modelled as a separate dictionary, as the browser keeps it: it
    // survives a page refresh but not a new tab, and nothing written to one is visible
    // in the other.
    private readonly Dictionary<string, string?> _session = sessionStorage ?? [];

    /// <summary>
    /// The module handed back for every <c>import</c>, so a test can inspect what was sent to it.
    /// </summary>
    /// <remarks>
    /// One instance for all imports rather than one per call: the app imports the same module from
    /// several components, and a test asserting on what the customer display received wants the
    /// combined record rather than whichever reference happened to be handed to the publisher.
    /// </remarks>
    public FakeJsObjectReference Module { get; } = new();

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(
        string identifier,
        CancellationToken cancellationToken,
        object?[]? args)
    {
        var key = args is { Length: > 0 } ? args[0]?.ToString() ?? string.Empty : string.Empty;

        object? result = identifier switch
        {
            "localStorage.getItem" => _storage.TryGetValue(key, out var value) ? value : null,
            "localStorage.setItem" => Store(key, args),
            "localStorage.removeItem" => Remove(key),
            "sessionStorage.getItem" => _session.TryGetValue(key, out var value) ? value : null,
            "sessionStorage.setItem" => StoreSession(key, args),
            "sessionStorage.removeItem" => RemoveSession(key),
            "import" => Module,
            _ => null,
        };

        return ValueTask.FromResult((TValue)result!);
    }

    /// <summary>What was written to local storage, for assertions about persistence.</summary>
    public Dictionary<string, string?> Written => _storage;

    /// <summary>What was written to session storage, for assertions about the sign-in marker.</summary>
    public Dictionary<string, string?> Session => _session;

    private object? Store(string key, object?[]? args)
    {
        if (args is { Length: > 1 })
        {
            _storage[key] = args[1]?.ToString();
        }

        return null;
    }

    private object? Remove(string key)
    {
        _storage.Remove(key);

        return null;
    }

    private object? StoreSession(string key, object?[]? args)
    {
        if (args is { Length: > 1 })
        {
            _session[key] = args[1]?.ToString();
        }

        return null;
    }

    private object? RemoveSession(string key)
    {
        _session.Remove(key);

        return null;
    }
}

/// <summary>
/// A loaded JS module that accepts every call and answers with a default value.
/// </summary>
/// <remarks>
/// Answers rather than throws, because the point is to render a page whose module happens to be
/// unavailable. A capability probe returning <c>false</c> is the honest answer for a browser with no
/// customer display attached, and it is the path a shop without one actually takes.
/// </remarks>
internal sealed class FakeJsObjectReference : IJSObjectReference
{
    // The heartbeat loop calls InvokeAsync from a thread-pool thread while the test thread is
    // reading the lists, so every access goes through the gate. The public views are snapshots:
    // handing out the live lists would just move the race to the caller's foreach.
    private readonly object _gate = new();
    private readonly List<string> _calls = [];
    private readonly List<(string Identifier, object?[] Args)> _invocations = [];

    /// <summary>Every call made, as the identifier, so a test can assert what was attempted.</summary>
    public IReadOnlyList<string> Calls
    {
        get { lock (_gate) { return [.. _calls]; } }
    }

    /// <summary>
    /// Every call with its arguments, so a test can assert what was <em>sent</em>.
    /// </summary>
    /// <remarks>
    /// The identifiers alone prove a call happened; the arguments are what prove it carried the right
    /// payload. For the customer display that is the whole question — a basket published with the
    /// wrong total is a call that looks identical to a correct one.
    /// </remarks>
    public IReadOnlyList<(string Identifier, object?[] Args)> Invocations
    {
        get { lock (_gate) { return [.. _invocations]; } }
    }

    /// <summary>Every payload passed to a named method, in order.</summary>
    public IReadOnlyList<string> PayloadsFor(string identifier)
    {
        lock (_gate)
        {
            return [.. _invocations
                .Where(i => string.Equals(i.Identifier, identifier, StringComparison.Ordinal))
                .Select(i => i.Args is { Length: > 0 } ? i.Args[^1]?.ToString() ?? string.Empty : string.Empty)];
        }
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(
        string identifier,
        CancellationToken cancellationToken,
        object?[]? args)
    {
        // The void-returning overloads are extension methods built on this one, so every call a
        // component makes lands here and is recorded — including the ones that return nothing.
        lock (_gate)
        {
            _calls.Add(identifier);
            _invocations.Add((identifier, args ?? []));
        }

        return ValueTask.FromResult(default(TValue)!);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A transport that accepts everything and records what it was sent.</summary>
internal sealed class RecordingTransport : IDeviceTransport
{
    public List<byte[]> Writes { get; } = [];

    public Exception? FailWith { get; set; }

    public string TransportId => "recording";

    public string DisplayName => "Recording printer";

    public DeviceState State { get; private set; } = new(DeviceStatus.Ready, "Recording printer", "Test printer");

    public PrinterCapabilities Capabilities { get; } = new(true, true, true, true, 48);

    public event Action<DeviceState>? StateChanged;

    public ValueTask<bool> IsAvailableAsync(CancellationToken ct = default) => ValueTask.FromResult(true);

    public ValueTask<bool> TryReconnectAsync(CancellationToken ct = default) => ValueTask.FromResult(true);

    public ValueTask<bool> RequestDeviceAsync(CancellationToken ct = default) => ValueTask.FromResult(true);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (FailWith is { } failure)
        {
            throw failure;
        }

        Writes.Add(payload.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Raise(DeviceState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }
}

/// <summary>
/// Tests for what the label printer sends, and what it does when there is nothing to send to.
/// </summary>
/// <remarks>
/// A label that does not print is an inconvenience — the shelf keeps its old label until someone
/// tries again — which is a completely different situation from a sale that cannot complete. So
/// the behaviour when nothing is paired is that it returns false rather than throwing, and that is
/// pinned here.
/// </remarks>
public sealed class LabelPrinterTests
{
    private static readonly string DefaultStockJson = """{"widthMm":50,"heightMm":25,"dpi":203}""";

    private static (LabelPrinter Printer, RecordingTransport Transport, FakeJsRuntime Js) NewPrinter(
        string? stockJson = null,
        bool paired = true)
    {
        var transport = new RecordingTransport();
        var js = new FakeJsRuntime(new Dictionary<string, string?>
        {
            ["pos.printer.label.stock"] = stockJson ?? DefaultStockJson,
        });

        var resolver = new PrinterResolver().Add(1, "Test", _ =>
            ValueTask.FromResult<IDeviceTransport?>(paired ? transport : null));

        var provider = new TerminalPrinterProvider(
            resolver,
            new ReceiptRenderer(),
            NullLogger<TerminalPrinterProvider>.Instance,
            PrinterRole.Label);

        var printer = new LabelPrinter(provider, new LabelSettings(js), NullLogger<LabelPrinter>.Instance);

        return (printer, transport, js);
    }

    private static string Text(IEnumerable<byte> bytes) => Encoding.UTF8.GetString([.. bytes]);

    [Fact]
    public async Task A_shelf_label_is_sent_as_a_zpl_document()
    {
        var (printer, transport, _) = NewPrinter();

        var printed = await printer.PrintShelfLabelAsync("Cola 500ml", "6001000000017", 15.00m);

        Assert.True(printed);

        var zpl = Text(Assert.Single(transport.Writes));

        // ZPL, not ESC/POS. Sending the wrong language produces a page of literal command text on
        // a receipt printer and nothing at all on a label printer.
        Assert.StartsWith("^XA", zpl, StringComparison.Ordinal);
        Assert.EndsWith("^XZ\n", zpl.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);

        Assert.Contains("15.00", zpl, StringComparison.Ordinal);
        Assert.Contains("6001000000017", zpl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_paired_returns_false_rather_than_throwing()
    {
        // A shop without a label printer is an ordinary shop. Throwing would present a missing
        // shelf label as a fault of the same severity as a sale that could not complete.
        var (printer, transport, _) = NewPrinter(paired: false);

        var printed = await printer.PrintShelfLabelAsync("Cola 500ml", "6001000000017", 15.00m);

        Assert.False(printed);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task A_printer_fault_returns_false_rather_than_throwing()
    {
        var (printer, transport, _) = NewPrinter();
        transport.FailWith = new IOException("The printer is out of paper.");

        var printed = await printer.PrintShelfLabelAsync("Cola 500ml", "6001000000017", 15.00m);

        Assert.False(printed);
    }

    [Fact]
    public async Task Several_copies_are_sent_as_separate_labels()
    {
        // Rendered per copy rather than one document repeated. A label printer's buffer is small,
        // and a shop printing forty shelf labels in one job should not have the 39th dropped.
        var (printer, transport, _) = NewPrinter();

        await printer.PrintShelfLabelAsync("Cola 500ml", "6001000000017", 15.00m, copies: 5);

        Assert.Equal(5, transport.Writes.Count);

        var first = Text(transport.Writes[0]);
        Assert.Equal(first, Text(transport.Writes[4]));
    }

    [Fact]
    public async Task No_copies_is_a_programming_error_rather_than_a_silent_no_op()
    {
        var (printer, _, _) = NewPrinter();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await printer.PrintShelfLabelAsync("Cola", "6001000000017", 15.00m, copies: 0));
    }

    [Fact]
    public async Task The_configured_stock_decides_the_labels_physical_size()
    {
        // A 100×50mm roll is a different label, not a scaled one.
        var (printer, transport, _) = NewPrinter("""{"widthMm":100,"heightMm":50,"dpi":300}""");

        await printer.PrintShelfLabelAsync("Cola 500ml", "6001000000017", 15.00m);

        var zpl = Text(Assert.Single(transport.Writes));

        // ^PW is the print width in dots: 100mm at 300 dpi, which is 11.81 dots/mm.
        Assert.Contains("^PW1181", zpl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unusable_stored_stock_prints_nothing_rather_than_a_wrong_sized_label()
    {
        // A mis-sized label is worse than none: it comes out looking like a successful print and
        // will not fit the product it was printed for. Nothing stored would have defaulted; a
        // value somebody configured is honoured by refusing.
        var (printer, transport, _) = NewPrinter("""{"widthMm":0,"heightMm":0,"dpi":203}""");

        var printed = await printer.PrintShelfLabelAsync("Cola 500ml", "6001000000017", 15.00m);

        Assert.False(printed);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task A_configured_but_unusable_stock_is_surfaced_rather_than_replaced()
    {
        // The settings screen reads this back, so the operator sees the value that is wrong and can
        // correct it — instead of the till silently printing a size they never chose.
        var (printer, _, _) = NewPrinter("""{"widthMm":0,"heightMm":0,"dpi":203}""");

        var stock = await printer.StockAsync();

        Assert.False(stock.IsValid(out var reason));
        Assert.Contains("width", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_default_stock_is_used_when_nothing_has_been_configured()
    {
        var (printer, transport, _) = NewPrinter(stockJson: string.Empty);

        var printed = await printer.PrintShelfLabelAsync("Cola 500ml", "6001000000017", 15.00m);

        Assert.True(printed);
        Assert.Equal(LabelStock.Default, await printer.StockAsync());
    }

    [Fact]
    public async Task Corrupt_stored_settings_fall_back_to_the_default()
    {
        // Storage can be edited by hand or left over from an older build. Falling back is right;
        // crashing the catalogue screen over a settings row would not be.
        var (printer, _, _) = NewPrinter("not JSON at all");

        Assert.Equal(LabelStock.Default, await printer.StockAsync());
    }

    [Fact]
    public async Task A_barcode_label_is_sent_without_a_price()
    {
        var (printer, transport, _) = NewPrinter();

        var printed = await printer.PrintBarcodeLabelAsync("6001000000017", caption: "Stock tag");

        Assert.True(printed);

        var zpl = Text(Assert.Single(transport.Writes));

        Assert.Contains("6001000000017", zpl, StringComparison.Ordinal);
        Assert.Contains("Stock tag", zpl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saving_an_unusable_stock_is_refused_with_an_explanation()
    {
        var js = new FakeJsRuntime();
        var settings = new LabelSettings(js);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await settings.SaveAsync(new LabelStock(5, 25, 203)));

        Assert.Contains("width", exception.Message, StringComparison.OrdinalIgnoreCase);

        // Nothing was written, so the previous configuration is still in force.
        Assert.Empty(js.Written);
    }

    [Fact]
    public async Task A_saved_stock_round_trips()
    {
        var js = new FakeJsRuntime();
        var settings = new LabelSettings(js);

        await settings.SaveAsync(new LabelStock(100, 50, 300));

        Assert.Equal(new LabelStock(100, 50, 300), await settings.GetAsync());
    }
}
