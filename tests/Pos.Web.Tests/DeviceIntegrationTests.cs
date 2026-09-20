// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Pos.Devices.Transport;
using Pos.Infrastructure.Storage;
using Pos.Web.Pages;
using Pos.Web.Terminal;

namespace Pos.Web.Tests;

/// <summary>
/// Rings up a sale and inspects what actually reached each printer.
/// </summary>
/// <remarks>
/// <para>
/// The transports and the renderers have their own tests, and between them they prove a receipt looks
/// right and that a WebUSB write carries the right bytes. Neither proves the till ever <em>sends</em>
/// one, to the right machine. That wiring — which role resolves to which device, whether the drawer
/// is kicked, whether a kitchen ticket goes to the kitchen — is the part that only a whole sale can
/// exercise.
/// </para>
/// <para>
/// The receipt printer is unbound on purpose: it takes whatever device it can open, because a sale
/// that cannot print is a sale the customer cannot prove. The kitchen and label printers are paired
/// to named connections here, which is what an operator does in device settings.
/// </para>
/// </remarks>
public sealed class DeviceIntegrationTests : BunitContext
{
    private const string ColaBarcode = "6001000000017";
    private const string BurgerBarcode = "6001000000093";

    private const string ReceiptConnection = "usb-receipt";
    private const string KitchenConnection = "usb-kitchen";
    private const string LabelConnection = "usb-label";

    private readonly InMemoryLocalStore _store = new();
    private readonly InMemoryShiftStore _roster;
    private readonly RecordingDeviceBridge _bridge = new();

    public DeviceIntegrationTests()
    {
        _roster = new InMemoryShiftStore(_store);

        JSInterop.Mode = JSRuntimeMode.Loose;

        _bridge.AuthorisedConnections.Add(ReceiptConnection);
        _bridge.Labels[ReceiptConnection] = "Epson TM-T20III";
        _bridge.Labels[KitchenConnection] = "Kitchen printer";
        _bridge.Labels[LabelConnection] = "Zebra ZD421";

        // The kitchen and label devices are authorised too, so a paired binding can reach them.
        _bridge.AuthorisedConnections.Add(KitchenConnection);
        _bridge.AuthorisedConnections.Add(LabelConnection);

        TerminalSeed.Compose(
            Services, _store, _roster, [], new FakeNavigationManager(), bridge: _bridge);
    }

    // ------------------------------------------------------------------------ the receipt

    /// <summary>
    /// Completing a sale sends a receipt to the receipt printer, and cuts it.
    /// </summary>
    /// <remarks>
    /// The customer's proof of purchase, and the one document that must come out of the right
    /// machine. The receipt is unbound, so it takes the first device it can open — here the Epson.
    /// </remarks>
    [Fact]
    public async Task ACompletedSale_PrintsAReceiptAndCutsIt()
    {
        var cut = await OpenTillAsync();

        await SellAsync(cut, ColaBarcode);

        var receipt = _bridge.TextFor(ReceiptConnection);

        Assert.Contains("Cola 500ml", receipt, StringComparison.Ordinal);
        Assert.Contains(RecordingDeviceBridge.Esc.Cut, receipt, StringComparison.Ordinal);
    }

    /// <summary>
    /// No sale, no drawer. The drawer is kicked as part of taking cash, not on every page load.
    /// </summary>
    /// <remarks>
    /// A drawer that opens when nothing was sold is the single most useful thing a thief at a till
    /// could ask for, so the kick is tied to the completion of a sale and to nothing else.
    /// </remarks>
    [Fact]
    public async Task OpeningTheTill_DoesNotKickTheDrawer()
    {
        await OpenTillAsync();

        Assert.DoesNotContain(
            RecordingDeviceBridge.Esc.DrawerKick,
            _bridge.TextFor(ReceiptConnection),
            StringComparison.Ordinal);
    }

    /// <summary>A completed cash sale kicks the drawer.</summary>
    [Fact]
    public async Task ACompletedSale_KicksTheDrawer()
    {
        var cut = await OpenTillAsync();

        await SellAsync(cut, ColaBarcode, cash: true);

        Assert.Contains(
            RecordingDeviceBridge.Esc.DrawerKick,
            _bridge.TextFor(ReceiptConnection),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A card sale does not open the drawer.
    /// </summary>
    /// <remarks>
    /// No cash went in, so there is nothing to put away — and a drawer that opens on card sales is
    /// one an operator opens dozens of times a day for no reason, which is exactly how a real
    /// unexplained opening hides.
    /// </remarks>
    [Fact]
    public async Task ACardSale_DoesNotKickTheDrawer()
    {
        var cut = await OpenTillAsync();

        await SellAsync(cut, ColaBarcode, cash: false);

        Assert.DoesNotContain(
            RecordingDeviceBridge.Esc.DrawerKick,
            _bridge.TextFor(ReceiptConnection),
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------ the kitchen

    /// <summary>
    /// A prepared item produces a kitchen ticket, and a shelf item does not.
    /// </summary>
    /// <remarks>
    /// The routing rule the whole station feature rests on. A ticket for a bottled drink sends
    /// somebody to the kitchen for nothing, and a kitchen that receives pointless tickets learns to
    /// ignore the printer.
    /// </remarks>
    [Fact]
    public async Task APreparedItem_PrintsAKitchenTicket_AndAShelfItemDoesNot()
    {
        await OpenTillAsync();
        await TerminalSeed.PairPrinterAsync(
            Services, PrinterRole.Kitchen, KitchenConnection, "Kitchen printer");

        var cut = Render<Checkout>();

        await SellAsync(cut, BurgerBarcode);

        var ticket = _bridge.TextFor(KitchenConnection);

        Assert.Contains("Chicken Burger", ticket, StringComparison.Ordinal);

        // The shelf item, sold on its own, must not reach the kitchen at all.
        _bridge.Writes.Clear();

        await SellAsync(cut, ColaBarcode);

        Assert.Empty(_bridge.Writes.GetValueOrDefault(KitchenConnection, []));
    }

    /// <summary>
    /// The kitchen ticket goes to the kitchen printer and not to the receipt printer.
    /// </summary>
    /// <remarks>
    /// The failure this guards against is silent and expensive: two roles resolving to the same
    /// device, so the order ticket comes out of the customer's receipt printer and the kitchen never
    /// sees it. The pairings are what prevent it.
    /// </remarks>
    [Fact]
    public async Task AKitchenTicket_DoesNotComeOutOfTheReceiptPrinter()
    {
        await OpenTillAsync();
        await TerminalSeed.PairPrinterAsync(
            Services, PrinterRole.Kitchen, KitchenConnection, "Kitchen printer");

        var cut = Render<Checkout>();

        await SellAsync(cut, BurgerBarcode);

        // The receipt names the burger because the customer bought one.
        Assert.Contains("Chicken Burger", _bridge.TextFor(ReceiptConnection), StringComparison.Ordinal);

        // But the kitchen document — which is headed as a ticket and carries no prices — did not.
        Assert.DoesNotContain("KITCHEN", _bridge.TextFor(ReceiptConnection), StringComparison.Ordinal);
        Assert.Contains("KITCHEN", _bridge.TextFor(KitchenConnection), StringComparison.Ordinal);
    }

    /// <summary>
    /// With no kitchen printer paired, the sale completes and nothing is sent.
    /// </summary>
    /// <remarks>
    /// A shop that prepares food and has not paired a printer is a shop that hands orders over the
    /// counter. The sale must not fail for it.
    /// </remarks>
    [Fact]
    public async Task WithNoKitchenPrinter_TheSaleStillCompletes()
    {
        var cut = await OpenTillAsync();

        await SellAsync(cut, BurgerBarcode);

        Assert.Empty(_bridge.Writes.GetValueOrDefault(KitchenConnection, []));

        var session = Services.GetRequiredService<CheckoutSession>();

        Assert.NotNull(session.LastSale);
    }

    // -------------------------------------------------------------------------- the labels

    /// <summary>
    /// A shelf label goes to the label printer as ZPL, and nowhere near the receipt printer.
    /// </summary>
    /// <remarks>
    /// The two printers speak different languages. ZPL sent to a receipt printer prints a page of
    /// literal command text; ESC/POS sent to a label printer prints nothing at all. Keeping them
    /// apart is the entire reason the label printer is a separate role.
    /// </remarks>
    [Fact]
    public async Task AShelfLabel_IsZplOnTheLabelPrinterOnly()
    {
        await TerminalSeed.StartAsync(Services);
        await TerminalSeed.PairPrinterAsync(
            Services, PrinterRole.Label, LabelConnection, "Zebra ZD421");

        var cut = Render<Catalog>();

        SelectProduct(cut, "Cola 500ml");

        ClickButton(cut, "Print shelf label");

        var label = _bridge.TextFor(LabelConnection);

        Assert.Contains(RecordingDeviceBridge.Esc.ZplStart, label, StringComparison.Ordinal);
        Assert.Contains("Cola 500ml", label, StringComparison.Ordinal);

        // Nothing went to the receipt printer, and no ESC/POS initialise reached the label printer.
        Assert.Empty(_bridge.BytesFor(ReceiptConnection));
        Assert.DoesNotContain(
            RecordingDeviceBridge.Esc.Initialise, label, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------ degradation

    /// <summary>
    /// A browser with no device APIs still completes a sale.
    /// </summary>
    /// <remarks>
    /// Safari and Firefox have neither WebUSB nor Web Serial. Browser printing is the only transport
    /// that works everywhere, and it cannot cut or open a drawer — the capability report says so
    /// rather than the till pretending otherwise.
    /// </remarks>
    [Fact]
    public async Task WithoutDeviceApis_TheSaleStillCompletes()
    {
        _bridge.UsbSupported = false;
        _bridge.SerialSupported = false;

        var cut = await OpenTillAsync();

        await SellAsync(cut, ColaBarcode);

        var session = Services.GetRequiredService<CheckoutSession>();

        Assert.NotNull(session.LastSale);

        // It fell back to the browser's own print dialog, which is what works in every browser.
        Assert.NotEmpty(_bridge.BrowserPrints);

        // And nothing was written over a device API that does not exist.
        Assert.Empty(_bridge.Writes);
    }

    /// <summary>
    /// A printer that fails does not fail the sale.
    /// </summary>
    /// <remarks>
    /// The money is already recorded by the time the printer is asked. A printer fault is a reason to
    /// fetch a paper roll, not a reason to tell the customer their payment did not go through.
    /// </remarks>
    [Fact]
    public async Task APrinterFault_DoesNotFailTheSale()
    {
        _bridge.WriteFailure = new InvalidOperationException("The printer is switched off.");

        var cut = await OpenTillAsync();

        await SellAsync(cut, ColaBarcode);

        var session = Services.GetRequiredService<CheckoutSession>();

        Assert.NotNull(session.LastSale);

        var storeId = session.Store!.Id.ToString();
        var sale = session.LastSale!.Value.Stored;

        var recorded = await _store.GetSalesForDateAsync(
            storeId,
            DateOnly.ParseExact(sale.BusinessDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

        Assert.Single(recorded);
    }

    // ------------------------------------------------------------------------------- helpers

    /// <summary>Starts the terminal and renders the till.</summary>
    private async Task<IRenderedComponent<Checkout>> OpenTillAsync()
    {
        await TerminalSeed.StartAsync(Services);

        return Render<Checkout>();
    }

    /// <summary>Scans a barcode and pays for it.</summary>
    private static async Task SellAsync(
        IRenderedComponent<Checkout> cut,
        string barcode,
        bool cash = true)
    {
        var box = cut.Find(".pos-scan__input");

        box.Input(barcode);

        await box.TriggerEventAsync("onkeydown", new KeyboardEventArgs { Key = "Enter" });

        ClickButton(cut, cash ? "Complete sale" : "Pay the whole balance by card");
    }

    /// <summary>Finds a product by name in the catalogue and opens its panel.</summary>
    private static void SelectProduct(IRenderedComponent<Catalog> cut, string name)
    {
        foreach (var row in cut.FindAll("tbody tr"))
        {
            if (!row.TextContent.Contains(name, StringComparison.Ordinal))
            {
                continue;
            }

            var manage = row.QuerySelector("button");

            Assert.NotNull(manage);

            manage.Click();

            return;
        }

        Assert.Fail($"No catalogue row for '{name}'.");
    }

    private static void ClickButton<TComponent>(IRenderedComponent<TComponent> cut, string label)
        where TComponent : Microsoft.AspNetCore.Components.IComponent
    {
        var buttons = cut.FindAll("button");

        foreach (var candidate in buttons)
        {
            if (!candidate.TextContent.Contains(label, StringComparison.Ordinal))
            {
                continue;
            }

            candidate.Click();

            return;
        }

        Assert.Fail($"No button labelled '{label}'. Present: "
            + string.Join(", ", buttons.Select(b => $"'{b.TextContent.Trim()}'")));
    }
}
