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
using Pos.Core.Domain;
using Pos.Infrastructure.Storage;
using Pos.Web.Pages;
using Pos.Web.Terminal;

namespace Pos.Web.Tests;

/// <summary>
/// Drives the till the way a shop does: scan, then take the money.
/// </summary>
/// <remarks>
/// <para>
/// The checkout screen is the one path in this system that has to work every time, and until now it
/// had been checked by compiling it and by looking at it once in a browser. Neither says whether
/// scanning a barcode actually puts a line in the basket, or whether taking payment actually records
/// a sale.
/// </para>
/// <para>
/// The terminal here is <b>not</b> enrolled. That is deliberate: an unenrolled till seeds the
/// demonstration catalogue, so these tests scan a real barcode against real products rather than
/// against fixtures they wrote themselves.
/// </para>
/// </remarks>
public sealed class CheckoutScreenTests : BunitContext
{
    /// <summary>A barcode from the seeded demonstration catalogue.</summary>
    private const string ColaBarcode = "6001000000017";

    private const string BreadBarcode = "6001000000031";

    private readonly InMemoryLocalStore _store = new();
    private readonly InMemoryShiftStore _roster;

    public CheckoutScreenTests()
    {
        _roster = new InMemoryShiftStore(_store);

        JSInterop.Mode = JSRuntimeMode.Loose;

        TerminalSeed.Compose(Services, _store, _roster, [], new FakeNavigationManager());
    }

    /// <summary>
    /// Scanning a barcode puts the product in the basket.
    /// </summary>
    /// <remarks>
    /// The first thing a cashier does, all day. A barcode that matches exactly goes straight into the
    /// basket — presenting a one-item list to confirm would double the work of every scan.
    /// </remarks>
    [Fact]
    public async Task ScanningABarcode_PutsItInTheBasket()
    {
        var cut = await OpenTillAsync();

        await ScanAsync(cut, ColaBarcode);

        var basket = cut.Find(".pos-lines").TextContent;

        Assert.Contains("Cola 500ml", basket, StringComparison.Ordinal);
    }

    /// <summary>Scanning the same barcode twice makes it a quantity of two, not two lines.</summary>
    [Fact]
    public async Task ScanningTheSameBarcodeTwice_MakesItAQuantityOfTwo()
    {
        var cut = await OpenTillAsync();

        await ScanAsync(cut, ColaBarcode);
        await ScanAsync(cut, ColaBarcode);

        var sale = await CompleteByCardAsync(cut);

        var line = Assert.Single(sale.Lines);

        Assert.Equal(2m, line.Quantity);
        Assert.Equal(30.00m, sale.Total);
    }

    /// <summary>Two different products make two lines, and the total is their sum.</summary>
    [Fact]
    public async Task TwoProducts_MakeTwoLinesAndATotal()
    {
        var cut = await OpenTillAsync();

        await ScanAsync(cut, ColaBarcode);
        await ScanAsync(cut, BreadBarcode);

        var sale = await CompleteByCardAsync(cut);

        Assert.Equal(2, sale.Lines.Count);
        Assert.Equal(15.00m + 18.99m, sale.Total);
    }

    /// <summary>
    /// A completed sale is written to the day's trading, and the basket is cleared.
    /// </summary>
    /// <remarks>
    /// The point of the whole screen. Taking the money and recording the sale are separate acts —
    /// payment first, then the record — and both have to have happened by the time the operator sees
    /// the receipt.
    /// </remarks>
    [Fact]
    public async Task ACompletedSale_IsRecordedForTheDay()
    {
        var cut = await OpenTillAsync();

        await ScanAsync(cut, ColaBarcode);

        var sale = await CompleteByCardAsync(cut);

        var storeId = Services.GetRequiredService<CheckoutSession>().Store!.Id.ToString();

        var businessDate = DateOnly.ParseExact(
            sale.BusinessDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        var recorded = await _store.GetSalesForDateAsync(storeId, businessDate);

        Assert.Single(recorded);
        Assert.Equal(sale.Id, recorded[0].Id);

        // The total that was charged is the total that was written down.
        Assert.Equal(15.00m, recorded[0].Total);

        // The basket is empty again, ready for the next customer.
        Assert.Empty(cut.FindAll(".pos-lines tbody tr"));
    }

    /// <summary>
    /// Once money has been taken against a basket, the items cannot be changed.
    /// </summary>
    /// <remarks>
    /// A part-paid basket that can still be scanned into is how a shop loses money without anybody
    /// deciding to: the customer pays for two items, the operator adds a third, and the total on the
    /// receipt is the total they already agreed to. The lock is on the <em>tender</em>, not on the
    /// sale — so it holds while the operator is still assembling payment, which is the window where
    /// the mistake actually happens.
    /// </remarks>
    [Fact]
    public async Task OnceMoneyIsTaken_TheItemsCannotBeChanged()
    {
        var cut = await OpenTillAsync();

        await ScanAsync(cut, ColaBarcode);

        // A part payment, so the sale is not settled and the basket stays on screen.
        cut.Find("#tendered").Input("5.00");
        ClickButton(cut, "Add cash");

        await ScanAsync(cut, BreadBarcode);

        Assert.DoesNotContain("White Bread", cut.Find(".pos-lines").TextContent, StringComparison.Ordinal);
        Assert.Contains("Remove them before changing the items", cut.Markup, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the operator pill

    /// <summary>
    /// The operator pill signs out from its menu, and the till stays exactly where it was.
    /// </summary>
    /// <remarks>
    /// A plain sign-out is a handover: the basket and the screen are untouched, only the
    /// attribution is gone, and the shift keeps running for whoever signs in next.
    /// </remarks>
    [Fact]
    public async Task SigningOutFromThePillMenu_ClearsTheSessionButLeavesTheShiftOpen()
    {
        var cut = await OpenTillAsync();

        var session = Services.GetRequiredService<CheckoutSession>();

        await TerminalSeed.SignInAsync(
            Services, session, session.Store!, EmployeePermissions.Supervisor);

        // The session changed outside a UI event, so the screen is told to draw it.
        cut.Render();

        ClickButton(cut, "Anele Botha");
        ClickButton(cut, "Sign out");

        Assert.Null(session.Employee);
        Assert.Null(session.Shift);
        Assert.False(session.IsSignedIn);

        // The drawer itself is untouched — still open for the next operator.
        var open = await _roster.GetOpenShiftAsync(
            Services.GetRequiredService<TerminalIdentity>().TerminalId);

        Assert.NotNull(open);

        // And the pill is back to offering a sign-in.
        Assert.Contains("No operator signed in", cut.Markup, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------- helpers

    /// <summary>Renders the till and waits for it to finish starting up.</summary>
    private async Task<IRenderedComponent<Checkout>> OpenTillAsync()
    {
        await TerminalSeed.StartAsync(Services);

        // The scan box stays disabled until the terminal has loaded its store and catalogue, so
        // rendering the screen is waiting for it to be usable — which is what a cashier waits for.
        return Render<Checkout>();
    }

    /// <summary>
    /// Types a barcode into the scan box and presses Enter, as a scanner does.
    /// </summary>
    /// <remarks>
    /// The keystroke matters rather than a direct method call. A keyboard-wedge scanner types the
    /// barcode and sends Enter, and the screen decides between "add it" and "search for it" on the
    /// strength of that key — so a test that skipped it would not be testing the scan path at all.
    /// </remarks>
    private static async Task ScanAsync(IRenderedComponent<Checkout> cut, string barcode)
    {
        var box = cut.Find(".pos-scan__input");

        box.Input(barcode);

        await box.TriggerEventAsync("onkeydown", new KeyboardEventArgs { Key = "Enter" });
    }

    /// <summary>Clicks the first button whose label contains the given text.</summary>
    private static void ClickButton(IRenderedComponent<Checkout> cut, string label)
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

    /// <summary>Pays the whole balance by card and returns the sale that was recorded.</summary>
    private async Task<StoredSale> CompleteByCardAsync(IRenderedComponent<Checkout> cut)
    {
        var session = Services.GetRequiredService<CheckoutSession>();

        // The button settles exactly what is owed, which is the ordinary case: the whole balance on
        // one method. It is only enabled once the basket has something in it.
        var pay = cut.FindAll("button")
            .First(b => b.TextContent.Contains("Pay the whole balance by card", StringComparison.Ordinal));

        pay.Click();

        return session.LastSale?.Stored
            ?? throw new InvalidOperationException("The sale was not completed.");
    }
}
