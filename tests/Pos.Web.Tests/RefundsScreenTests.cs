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
/// Drives refunding a sale from the receipt number on the customer's slip.
/// </summary>
/// <remarks>
/// <para>
/// Refunds are where a shop is most exposed, and where the screen's job is mostly to <em>refuse</em>:
/// more than was bought, more than remains, twice over, or by someone who is not allowed. Each of
/// those is a way money leaves the drawer without a sale to account for it.
/// </para>
/// <para>
/// The sales here are made through the till rather than written straight into the database, because
/// the receipt number is the link between the two screens. A refund test that invented its own sale
/// would not notice the two screens disagreeing about what that number looks like.
/// </para>
/// </remarks>
public sealed class RefundsScreenTests : BunitContext
{
    private const string ColaBarcode = "6001000000017";

    private readonly InMemoryLocalStore _store = new();
    private readonly InMemoryShiftStore _roster;

    public RefundsScreenTests()
    {
        _roster = new InMemoryShiftStore(_store);

        JSInterop.Mode = JSRuntimeMode.Loose;

        TerminalSeed.Compose(Services, _store, _roster, [], new FakeNavigationManager());
    }

    // -------------------------------------------------------------------------- lookups

    /// <summary>
    /// A sale is found by the receipt number printed on the customer's slip.
    /// </summary>
    /// <remarks>
    /// This is how a refund starts in practice: the customer presents a receipt and the operator
    /// types the number from it. Requiring the internal id instead would mean a refund could only be
    /// processed by somebody with database access.
    /// </remarks>
    [Fact]
    public async Task ASale_IsFoundByItsReceiptNumber()
    {
        var (number, _) = await SellOneColaAsync();

        var cut = await OpenRefundsAsync(EmployeePermissions.Supervisor);

        Lookup(cut, number);

        Assert.Contains("Cola 500ml", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The receipt number can also be submitted with Enter, which is what a scanner sends.
    /// </summary>
    /// <remarks>
    /// A shop that prints a barcode of the receipt number on the slip scans it rather than typing it,
    /// and a box that only responded to the button would leave that operator stuck.
    /// </remarks>
    [Fact]
    public async Task AReceiptNumber_CanBeSubmittedWithEnter()
    {
        var (number, _) = await SellOneColaAsync();

        var cut = await OpenRefundsAsync(EmployeePermissions.Supervisor);

        await LookupByEnterAsync(cut, number);

        Assert.Contains("Cola 500ml", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>A number that matches nothing is refused rather than silently ignored.</summary>
    [Fact]
    public async Task AnUnknownReceiptNumber_IsRefused()
    {
        await SellOneColaAsync();

        var cut = await OpenRefundsAsync(EmployeePermissions.Supervisor);

        Lookup(cut, "CT01-19990101-9999");

        Assert.Contains("no sale", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The day's sales are listed, for a customer who has lost the receipt.</summary>
    [Fact]
    public async Task TodaysSales_AreListedForALostReceipt()
    {
        await SellOneColaAsync();

        var cut = await OpenRefundsAsync(EmployeePermissions.Supervisor);

        Assert.NotEmpty(cut.FindAll(".ref-recent__item"));
    }

    // ------------------------------------------------------------------------- refunding

    /// <summary>
    /// Refunding a line records the refund and puts the goods back into stock.
    /// </summary>
    /// <remarks>
    /// Both halves matter. A refund that moved money but not stock would leave the shelf wrong
    /// forever; one that moved stock but not money would be a shop giving goods away.
    /// </remarks>
    [Fact]
    public async Task RefundingALine_RecordsItAndReturnsTheStock()
    {
        var (number, sale) = await SellOneColaAsync();

        var cut = await OpenRefundsAsync(EmployeePermissions.Supervisor);

        Lookup(cut, number);

        var before = await LevelAsync("Cola 500ml");

        SetReturnQuantity(cut, 1m);
        ClickButton(cut, "Refund and print slip");

        var refunds = await ReturnsForSaleAsync(sale.Id);

        var refund = Assert.Single(refunds);

        Assert.Equal(sale.Total, refund.TotalRefund);
        Assert.Equal(before + 1m, await LevelAsync("Cola 500ml"));
    }

    /// <summary>
    /// A refunded sale cannot be refunded again.
    /// </summary>
    /// <remarks>
    /// The abuse the whole returns model exists to prevent: present the same receipt twice and be
    /// paid twice for one purchase.
    /// </remarks>
    [Fact]
    public async Task ARefundedSale_CannotBeRefundedAgain()
    {
        var (number, _) = await SellOneColaAsync();

        var cut = await OpenRefundsAsync(EmployeePermissions.Supervisor);

        Lookup(cut, number);
        SetReturnQuantity(cut, 1m);
        ClickButton(cut, "Refund and print slip");

        Lookup(cut, number);

        Assert.Contains("already been refunded", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// A partial refund leaves the rest of the sale refundable.
    /// </summary>
    /// <remarks>
    /// The ordinary case for a multi-item sale: one thing comes back and the rest does not.
    /// </remarks>
    [Fact]
    public async Task APartialRefund_LeavesTheRestRefundable()
    {
        var (number, sale) = await SellTwoColasAsync();

        var cut = await OpenRefundsAsync(EmployeePermissions.Supervisor);

        Lookup(cut, number);
        SetReturnQuantity(cut, 1m);
        ClickButton(cut, "Refund and print slip");

        var refunds = await ReturnsForSaleAsync(sale.Id);

        Assert.Equal(15.00m, Assert.Single(refunds).TotalRefund);

        // One of the two is still on the customer's hands, and the screen still offers it back.
        Lookup(cut, number);

        Assert.DoesNotContain("already been refunded", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("refunded in part", cut.Markup, StringComparison.Ordinal);

        // And the line is still selectable, which is what makes the remainder refundable at all.
        Assert.NotEmpty(cut.FindAll("input.pos-qty"));
    }

    /// <summary>
    /// Asking for more than was bought refunds only what was bought.
    /// </summary>
    /// <remarks>
    /// The screen caps the box, but a cap in markup is a convenience, not a control — the figure has
    /// to be refused by the code that records it.
    /// </remarks>
    [Fact]
    public async Task AskingForMoreThanWasBought_RefundsOnlyWhatRemains()
    {
        var (number, sale) = await SellOneColaAsync();

        var cut = await OpenRefundsAsync(EmployeePermissions.Supervisor);

        Lookup(cut, number);

        SetReturnQuantity(cut, 9m);
        ClickButton(cut, "Refund and print slip");

        var refunds = await ReturnsForSaleAsync(sale.Id);

        // Either it was refused outright, or it was capped at the one that was sold. What it must
        // never be is nine.
        if (refunds.Count > 0)
        {
            Assert.True(
                refunds.Sum(r => r.Lines.Sum(l => l.Quantity)) <= 1m,
                "More was refunded than was ever sold.");
        }
    }

    /// <summary>An operator without the refund permission cannot refund.</summary>
    /// <remarks>
    /// Refunding is one of the few ways money leaves a till with no sale behind it, so it is a
    /// supervisor act rather than something every cashier inherits.
    /// </remarks>
    [Fact]
    public async Task AnOperatorWithoutAuthority_CannotRefund()
    {
        var (number, sale) = await SellOneColaAsync();

        var cut = await OpenRefundsAsync(EmployeePermissions.Sell);

        Lookup(cut, number);
        SetReturnQuantity(cut, 1m);
        ClickButton(cut, "Refund and print slip");

        Assert.Empty(await ReturnsForSaleAsync(sale.Id));
        Assert.Contains("not authorised", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------------------- helpers

    /// <summary>Renders the refunds screen with an operator signed in.</summary>
    private async Task<IRenderedComponent<Refunds>> OpenRefundsAsync(EmployeePermissions permissions)
    {
        var session = Services.GetRequiredService<CheckoutSession>();

        await TerminalSeed.SignInAsync(Services, session, session.Store!, permissions);

        return Render<Refunds>();
    }

    /// <summary>
    /// Sells one Cola through the till and returns its receipt number and stored sale.
    /// </summary>
    private async Task<(string Number, StoredSale Sale)> SellOneColaAsync() =>
        await SellAsync(scanTwice: false);

    /// <summary>Sells two Colas as one line of quantity two.</summary>
    private async Task<(string Number, StoredSale Sale)> SellTwoColasAsync() =>
        await SellAsync(scanTwice: true);

    private async Task<(string Number, StoredSale Sale)> SellAsync(bool scanTwice)
    {
        await TerminalSeed.StartAsync(Services);

        var cut = Render<Checkout>();

        await ScanAsync(cut, ColaBarcode);

        if (scanTwice)
        {
            await ScanAsync(cut, ColaBarcode);
        }

        ClickButton(cut, "Pay the whole balance by card");

        var session = Services.GetRequiredService<CheckoutSession>();

        var completed = session.LastSale
            ?? throw new InvalidOperationException("The sale was not completed.");

        return (completed.Stored.Number, completed.Stored);
    }

    /// <summary>
    /// Types a receipt number and looks it up with the button.
    /// </summary>
    /// <remarks>
    /// The explicit button, because that is what an operator with the slip in their hand uses. The
    /// box also submits on Enter; that route is covered by its own test so a change that broke one
    /// of them could not hide behind the other.
    /// </remarks>
    private static void Lookup(IRenderedComponent<Refunds> cut, string number)
    {
        cut.Find(".pos-scan__input").Change(number);

        ClickButton(cut, "Find");
    }

    /// <summary>Types a receipt number and presses Enter, the way a scanner would.</summary>
    private static async Task LookupByEnterAsync(IRenderedComponent<Refunds> cut, string number)
    {
        var box = cut.Find(".pos-scan__input");

        box.Change(number);

        await box.TriggerEventAsync("onkeydown", new KeyboardEventArgs { Key = "Enter" });
    }

    /// <summary>Types a quantity into the only refundable line.</summary>
    private static void SetReturnQuantity(IRenderedComponent<Refunds> cut, decimal quantity)
    {
        var boxes = cut.FindAll("input.pos-qty");

        Assert.NotEmpty(boxes);

        boxes[0].Change(quantity.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<IReadOnlyList<StoredReturn>> ReturnsForSaleAsync(string saleId) =>
        await _store.GetReturnsForSaleAsync(saleId);

    private async Task<decimal> LevelAsync(string productName)
    {
        var storeId = Services.GetRequiredService<CheckoutSession>().Store!.Id.ToString();

        var catalogue = await Services
            .GetRequiredService<Pos.Infrastructure.Checkout.StockService>()
            .GetCatalogueAsync(storeId, productName);

        return catalogue.Single().Stock.Quantity;
    }

    private static async Task ScanAsync(IRenderedComponent<Checkout> cut, string barcode)
    {
        var box = cut.Find(".pos-scan__input");

        box.Input(barcode);

        await box.TriggerEventAsync("onkeydown", new KeyboardEventArgs { Key = "Enter" });
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
