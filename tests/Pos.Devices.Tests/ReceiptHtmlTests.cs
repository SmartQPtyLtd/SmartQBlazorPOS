// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;
using Pos.Devices.EscPos;
using Pos.Devices.Transport;

namespace Pos.Devices.Tests;

/// <summary>
/// Tests for the HTML document renderer and the transport routing that uses it.
/// </summary>
/// <remarks>
/// This is the half of the fallback that works in every browser. WebUSB and Web Serial cover
/// Chromium and Edge; Safari and Firefox have neither, so the browser's own print dialog is the only
/// route to paper — and for a long time nothing produced the HTML or called the method that sends it,
/// so the fallback was selected and then failed, leaving the customer with no receipt at all.
/// </remarks>
public sealed class ReceiptHtmlTests
{
    private const string Zar = "ZAR";
    private static readonly TaxRate Vat15 = new("VAT", 0.15m);

    private static Store NewStore() => new()
    {
        Id = StoreId.New(),
        Name = "CORNER STORE",
        Code = "CT01",
        Currency = Zar,
        TaxMode = TaxMode.Inclusive,
        DefaultTaxRate = Vat15,
        AddressLines = ["12 Main Road", "Cape Town"],
        Phone = "021 555 0100",
        TaxRegistrationNumber = "4123456789",
        ReceiptFooter = "Thank you for your business!",
        ReceiptColumns = 48,
    };

    private static Sale NewSale(
        decimal price = 115.00m,
        decimal quantity = 1m,
        string name = "Cola 500ml",
        string? note = null,
        string? station = null,
        TenderType tender = TenderType.Cash,
        IReadOnlyList<TaxComponent>? taxComponents = null)
    {
        var lineTotal = CartLine.Round(price * quantity);
        var tax = CartLine.Round(lineTotal / 1.15m * 0.15m);

        return new Sale
        {
            Id = SaleId.New(),
            StoreId = StoreId.New(),
            Number = new SaleNumber("CT01", new DateOnly(2026, 3, 25), 7),
            CompletedAt = new DateTimeOffset(2026, 3, 25, 14, 30, 0, TimeSpan.FromHours(2)),
            BusinessDate = new DateOnly(2026, 3, 25),
            Currency = Zar,
            TaxMode = TaxMode.Inclusive,
            Lines =
            [
                new SaleLine(
                    ProductId.New(), "6001000000017", name, quantity,
                    new Money(price, Zar), Vat15, 0m, lineTotal, tax, note, station),
            ],
            Tenders = [new Tender(tender, new Money(lineTotal, Zar))],
            Tax = new TaxCalculation(TaxMode.Inclusive, lineTotal - tax, tax, lineTotal, taxComponents ?? []),
            Subtotal = lineTotal,
            TotalDiscount = 0m,
            Total = lineTotal,
        };
    }

    private static string Render(ReceiptDocument document) =>
        new ReceiptHtmlRenderer(Zar).Render(document);

    // ------------------------------------------------------------------------- the receipt

    /// <summary>
    /// A receipt carries the store, the line, the total, and how it was paid.
    /// </summary>
    /// <remarks>
    /// A receipt the customer cannot use to prove what they bought and what they paid is not a
    /// receipt, whichever transport printed it.
    /// </remarks>
    [Fact]
    public void A_receipt_carries_what_the_customer_needs()
    {
        var html = Render(new ReceiptDocument(NewStore(), NewSale()));

        Assert.Contains("CORNER STORE", html, StringComparison.Ordinal);
        Assert.Contains("12 Main Road", html, StringComparison.Ordinal);
        Assert.Contains("CT01-20260325-0007", html, StringComparison.Ordinal);
        Assert.Contains("Cola 500ml", html, StringComparison.Ordinal);
        Assert.Contains("115.00", html, StringComparison.Ordinal);
        Assert.Contains("TOTAL", html, StringComparison.Ordinal);
        Assert.Contains("Cash", html, StringComparison.Ordinal);
        Assert.Contains("Thank you for your business!", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Text from head office and from operators is escaped.
    /// </summary>
    /// <remarks>
    /// The one assertion here that is about safety rather than correctness. A product name, a line
    /// note, and the store's own footer all arrive as text this app did not author, and the finished
    /// string is handed to a browser to render — so markup in a name would be injected into the
    /// document that gets printed, and into whatever prints it.
    /// </remarks>
    [Fact]
    public void Markup_in_a_product_name_cannot_escape_into_the_page()
    {
        var sale = NewSale(name: "<script>alert('x')</script>", note: "<img src=x onerror=alert(1)>");

        var html = Render(new ReceiptDocument(NewStore(), sale));

        // No tag survives: what would have opened one is encoded.
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);

        // The text is still legible, so escaping has not silently dropped the line.
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    /// <summary>A copy says so, because a duplicate that looks like an original can be refunded twice.</summary>
    [Fact]
    public void A_copy_is_marked_as_one()
    {
        var store = NewStore();
        var sale = NewSale();

        Assert.DoesNotContain(
            "COPY",
            Render(new ReceiptDocument(store, sale)),
            StringComparison.Ordinal);

        Assert.Contains(
            "COPY",
            Render(new ReceiptDocument(store, sale, PosDocumentType.SalesReceiptCopy, CopyIndex: 1)),
            StringComparison.Ordinal);
    }

    /// <summary>A refund slip is headed as a refund, so it cannot pass for a receipt.</summary>
    [Fact]
    public void A_refund_slip_is_unmistakable()
    {
        var html = Render(new ReceiptDocument(NewStore(), NewSale(), PosDocumentType.RefundReceipt));

        Assert.Contains("REFUND", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// An inclusive store does not print tax as though it were added on top.
    /// </summary>
    /// <remarks>
    /// A shop pricing tax-inclusively that printed a tax figure beside a total the customer might read
    /// as an addition would be showing a total that does not match the sum of its parts. The wording
    /// is the same as the thermal receipt's, because this is the same document.
    /// </remarks>
    [Fact]
    public void An_inclusive_store_says_the_tax_is_included()
    {
        var html = Render(new ReceiptDocument(NewStore(), NewSale()));

        Assert.Contains("(includes", html, StringComparison.Ordinal);
        Assert.Contains("tax)", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A multi-rate sale gets a tax summary instead of one unrepresentative line.
    /// </summary>
    /// <remarks>
    /// The case the single line cannot describe. Two rates on one total would otherwise be reported
    /// as though only one applied.
    /// </remarks>
    [Fact]
    public void A_multi_rate_sale_gets_a_tax_summary()
    {
        var vat = new TaxRate("VAT", 0.15m);
        var levy = new TaxRate("Tourism levy", 0.01m);

        var sale = NewSale(
            taxComponents:
            [
                new TaxComponent(vat, 98.00m, 14.70m),
                new TaxComponent(levy, 98.00m, 0.98m),
            ]);

        var html = Render(new ReceiptDocument(NewStore(), sale));

        Assert.Contains("Tax summary", html, StringComparison.Ordinal);
        Assert.Contains("Tourism levy", html, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------- the kitchen ticket

    /// <summary>
    /// A kitchen ticket carries no money.
    /// </summary>
    /// <remarks>
    /// A cook assembles an order; prices and payment are noise on a ticket read under pressure, and
    /// they waste paper on a machine that is not the till's.
    /// </remarks>
    [Fact]
    public void A_kitchen_ticket_carries_no_prices()
    {
        var html = Render(new ReceiptDocument(
            NewStore(), NewSale(name: "Chicken Burger", station: "kitchen"),
            PosDocumentType.KitchenTicket, StationId: "kitchen"));

        Assert.Contains("Chicken Burger", html, StringComparison.Ordinal);
        Assert.DoesNotContain("115.00", html, StringComparison.Ordinal);
        Assert.DoesNotContain("TOTAL", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Cash", html, StringComparison.Ordinal);
    }

    /// <summary>A ticket names its station, so it is put down in the right place.</summary>
    [Fact]
    public void A_kitchen_ticket_names_its_station()
    {
        var store = NewStore();

        store.Stations =
        [
            new PreparationStation("kitchen", "KITCHEN"),
            new PreparationStation("bar", "BAR"),
        ];

        var html = Render(new ReceiptDocument(
            store, NewSale(name: "Flat White", station: "bar"),
            PosDocumentType.KitchenTicket, StationId: "bar"));

        Assert.Contains("BAR", html, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------ job names

    /// <summary>The print dialog shows something an operator can identify.</summary>
    [Fact]
    public void The_job_name_identifies_the_document()
    {
        var store = NewStore();
        var sale = NewSale();

        Assert.Contains("Receipt", ReceiptHtmlRenderer.TitleFor(new ReceiptDocument(store, sale)));
        Assert.Contains(
            "Order",
            ReceiptHtmlRenderer.TitleFor(
                new ReceiptDocument(store, sale, PosDocumentType.KitchenTicket)));
    }

    // ---------------------------------------------------------------- the routing itself

    /// <summary>
    /// The printer sends HTML to a transport that takes HTML, rather than bytes it refuses.
    /// </summary>
    /// <remarks>
    /// The defect this pins. A browser-print transport deliberately throws on raw ESC/POS, because
    /// sending control bytes to a print dialog emits a page of command characters. So a printer that
    /// always rendered bytes could never print through it — it threw every time, and the fallback
    /// that exists for every non-Chromium browser produced nothing while the sale completed.
    /// </remarks>
    [Fact]
    public async Task The_printer_uses_html_for_a_transport_that_takes_html()
    {
        var bridge = new HtmlRecordingBridge();
        var printer = new ReceiptPrinter(new BrowserPrintTransport(bridge));

        await printer.PrintReceiptAsync(new ReceiptDocument(NewStore(), NewSale()), openDrawer: true);

        var printed = Assert.Single(bridge.Printed);

        Assert.Contains("Cola 500ml", printed.Body, StringComparison.Ordinal);

        // Nothing was written as bytes, because this transport has no way to accept them.
        Assert.Empty(bridge.Bytes);
    }

    /// <summary>A transport that cannot reach a drawer is never asked to open one.</summary>
    /// <remarks>
    /// Asking would either throw, failing a receipt that is otherwise fine, or worse, appear to
    /// succeed and send a cashier away from an open till.
    /// </remarks>
    [Fact]
    public async Task A_cash_sale_over_browser_print_does_not_try_the_drawer()
    {
        var bridge = new HtmlRecordingBridge();
        var printer = new ReceiptPrinter(new BrowserPrintTransport(bridge));

        // A cash sale asks for the drawer. This transport cannot open one, and must not pretend to.
        await printer.PrintReceiptAsync(new ReceiptDocument(NewStore(), NewSale()), openDrawer: true);

        Assert.Single(bridge.Printed);
        Assert.Empty(bridge.Bytes);
    }

    /// <summary>A transport that does take bytes still gets bytes.</summary>
    [Fact]
    public async Task The_printer_still_sends_bytes_to_a_byte_transport()
    {
        var transport = new ByteRecordingTransport();
        var printer = new ReceiptPrinter(transport);

        await printer.PrintReceiptAsync(new ReceiptDocument(NewStore(), NewSale()), openDrawer: true);

        Assert.NotEmpty(transport.Written);

        // The drawer kick is present because this transport reports that it can reach one.
        var bytes = transport.Written.SelectMany(b => b).ToArray();

        Assert.Contains((byte)0x1B, bytes);
        Assert.Contains((byte)0x70, bytes);
    }

    /// <summary>A transport that accepts raw bytes and keeps them.</summary>
    private sealed class ByteRecordingTransport : IDeviceTransport
    {
        public List<byte[]> Written { get; } = [];

        public string TransportId => "recording";

        public string DisplayName => "Recording";

        public DeviceState State { get; private set; } = new(DeviceStatus.Ready, "Recording");

        public PrinterCapabilities Capabilities { get; } =
            new(CanCut: true, CanOpenDrawer: true, CanPrintImages: true, CanPrintQrCode: true, Columns: 48);

        public event Action<DeviceState>? StateChanged;

        public ValueTask<bool> IsAvailableAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(true);

        public ValueTask<bool> TryReconnectAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(true);

        public ValueTask<bool> RequestDeviceAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(true);

        public ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        {
            Written.Add(payload.ToArray());

            return ValueTask.CompletedTask;
        }

        public ValueTask DisconnectAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>Kept so the compiler does not warn about an event nothing raises.</summary>
        private void Unused() => StateChanged?.Invoke(State);
    }

    /// <summary>
    /// Every class the renderer emits is one the print frame actually styles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These two files are a contract and neither knows about the other: the renderer emits markup,
    /// and <c>device-bridge.js</c> supplies the stylesheet for the iframe it is written into. A class
    /// renamed on one side produces no error — just an unstyled receipt, readable but with every
    /// price jammed against the item name and the amounts unaligned.
    /// </para>
    /// <para>
    /// The same shape as the catalogue payload's reflection guard: the defect this prevents is a
    /// silent divergence between two halves of one feature, which no single-file test can see.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_renderer_only_emits_classes_the_print_frame_styles()
    {
        var stylesheet = PrintStylesheet.selectors;

        var store = NewStore();

        store.Stations = [new PreparationStation("kitchen", "KITCHEN")];

        var documents = new[]
        {
            new ReceiptDocument(store, NewSale(name: "Chicken Burger", station: "kitchen", note: "no cheese")),
            new ReceiptDocument(store, NewSale(), PosDocumentType.SalesReceiptCopy, CopyIndex: 1),
            new ReceiptDocument(store, NewSale(), PosDocumentType.RefundReceipt),
            new ReceiptDocument(
                store, NewSale(name: "Chicken Burger", station: "kitchen", note: "no cheese"),
                PosDocumentType.KitchenTicket, StationId: "kitchen"),
        };

        var emitted = documents
            .SelectMany(d => System.Text.RegularExpressions.Regex
                .Matches(Render(d), "class=\"([^\"]+)\"")
                .SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(emitted);

        var unstyled = emitted.Where(c => !stylesheet.Contains(c)).ToArray();

        Assert.True(
            unstyled.Length == 0,
            $"The print frame does not style: {string.Join(", ", unstyled)}. "
            + "Its stylesheet in device-bridge.js must cover every class the renderer emits, or the "
            + "receipt prints unstyled.");
    }

    /// <summary>
    /// The class names in the print frame's stylesheet, read out of the source that defines them.
    /// </summary>
    /// <remarks>
    /// Read rather than restated, for the same reason the offline check parses the service worker's
    /// cache patterns: a copy would drift, and the drift is the defect being guarded against.
    /// </remarks>
    private static class PrintStylesheet
    {
        public static HashSet<string> selectors { get; } = Load();

        private static HashSet<string> Load()
        {
            var bridge = File.ReadAllText(Path.Combine(RepositoryRoot(), "src/Pos.Web/wwwroot/js/device-bridge.js"));

            var block = System.Text.RegularExpressions.Regex.Match(
                bridge, @"<style>(.*?)</style>", System.Text.RegularExpressions.RegexOptions.Singleline);

            Assert.True(block.Success, "device-bridge.js no longer writes a print stylesheet.");

            return [.. System.Text.RegularExpressions.Regex
                .Matches(block.Groups[1].Value, @"\.([a-zA-Z][\w-]*)")
                .Select(m => m.Groups[1].Value)];
        }

        /// <summary>Walks up from the test assembly to the repository root.</summary>
        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Pos.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.True(directory is not null, "Could not find the repository root above the test assembly.");

            return directory!.FullName;
        }
    }

    /// <summary>A bridge that keeps what the print dialog was given.</summary>
    private sealed class HtmlRecordingBridge : IDeviceBridge
    {
        public List<(string Title, string Body)> Printed { get; } = [];

        public List<byte[]> Bytes { get; } = [];

        public ValueTask<bool> IsWebUsbSupportedAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(false);

        public ValueTask<bool> IsWebSerialSupportedAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(false);

        public ValueTask<bool> IsBrowserPrintAvailableAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(true);

        public ValueTask PrintViaBrowserAsync(string title, string htmlBody, CancellationToken ct = default)
        {
            Printed.Add((title, htmlBody));

            return ValueTask.CompletedTask;
        }

        public ValueTask BeginWebUsbRequestAsync(string requestId, int? vendorId = null, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask<DeviceRequestResult> PollRequestAsync(string requestId, CancellationToken ct = default) =>
            ValueTask.FromResult(new DeviceRequestResult(DeviceRequestState.Pending));

        public ValueTask<IReadOnlyList<string>> GetAuthorisedWebUsbConnectionsAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask OpenWebUsbAsync(string connectionId, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask WriteWebUsbAsync(string connectionId, byte[] payload, CancellationToken ct = default)
        {
            Bytes.Add(payload);

            return ValueTask.CompletedTask;
        }

        public ValueTask CloseWebUsbAsync(string connectionId, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask<string?> DescribeAsync(string connectionId, CancellationToken ct = default) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask BeginWebSerialRequestAsync(string requestId, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<string>> GetAuthorisedWebSerialConnectionsAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask OpenWebSerialAsync(string connectionId, int baudRate, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask WriteWebSerialAsync(string connectionId, byte[] payload, CancellationToken ct = default)
        {
            Bytes.Add(payload);

            return ValueTask.CompletedTask;
        }

        public ValueTask CloseWebSerialAsync(string connectionId, CancellationToken ct = default) =>
            ValueTask.CompletedTask;
    }
}
