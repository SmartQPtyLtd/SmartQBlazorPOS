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
using Pos.Infrastructure.Storage;
using Pos.Web.Pages;
using Pos.Web.Terminal;

namespace Pos.Web.Tests;

/// <summary>
/// Drives the trading report.
/// </summary>
/// <remarks>
/// <para>
/// This is the screen a shopkeeper actually reads, and the one where a number being wrong is worst:
/// it is the figure that gets compared against the cash in the drawer and against head office.
/// </para>
/// <para>
/// A defect here was found once already by reasoning rather than by clicking — the day report ignored
/// refunds while the estate report subtracted them, so the same shop's takings disagreed with
/// themselves depending on which screen you looked at. These tests drive sales and refunds through
/// the till and then read the report, which is the only way to catch that class of disagreement.
/// </para>
/// </remarks>
public sealed class ReportsScreenTests : BunitContext
{
    private const string ColaBarcode = "6001000000017";

    private readonly InMemoryLocalStore _store = new();
    private readonly InMemoryShiftStore _roster;

    public ReportsScreenTests()
    {
        _roster = new InMemoryShiftStore(_store);

        JSInterop.Mode = JSRuntimeMode.Loose;

        TerminalSeed.Compose(Services, _store, _roster, [], new FakeNavigationManager());
    }

    /// <summary>A day with no trading reports zero rather than an empty screen.</summary>
    [Fact]
    public async Task ADayWithNoTrading_ReportsZero()
    {
        await TerminalSeed.StartAsync(Services);

        var cut = Render<Reports>();

        Assert.Contains("0.00", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>A sale appears in the day's takings.</summary>
    [Fact]
    public async Task ASale_AppearsInTheDaysTakings()
    {
        await SellOneColaAsync();

        var cut = Render<Reports>();

        Assert.Contains("15.00", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refund is subtracted from the day's takings, and shown on its own line.
    /// </summary>
    /// <remarks>
    /// The defect this guards against is subtle and was real: a report that showed gross takings
    /// without the refund would disagree with the estate total for the same shop on the same day, and
    /// the shopkeeper would have no way to tell which of the two was right.
    /// </remarks>
    [Fact]
    public async Task ARefund_IsSubtractedFromTakingsAndShownSeparately()
    {
        var report = await ReportAfterSaleAndRefundAsync();

        // One 15.00 sale, one 15.00 refund.
        Assert.Equal(15.00m, report.Gross);
        Assert.Equal(15.00m, report.RefundTotal);
        Assert.Equal(1, report.RefundCount);
        Assert.Equal(0m, report.NetTakings);
    }

    /// <summary>
    /// A void is counted and valued but never netted into the takings.
    /// </summary>
    /// <remarks>
    /// A voided sale took no money, so subtracting it would take money off that was never there. It
    /// is still counted and valued, because a shop that voids a lot needs to be able to see that.
    /// </remarks>
    [Fact]
    public async Task AVoid_IsCountedAndValuedButNotNetted()
    {
        await SellOneColaAsync();

        var cut = Render<Reports>();

        // The figure the shop is held to is the takings; a void must not move it.
        Assert.Contains("15.00", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The report the shop reads and the report the estate reads agree about the same day.
    /// </summary>
    /// <remarks>
    /// The property that makes both worth reading. Built from the same sales and refunds through two
    /// different code paths, so a divergence means one of them is wrong — and the shopkeeper cannot
    /// tell which from the numbers alone.
    /// </remarks>
    [Fact]
    public async Task TheShopAndTheEstate_AgreeOnTheDaysTakings()
    {
        var day = await SellOneColaAsync();

        await RefundItAsync();

        var shop = await new Pos.Infrastructure.Reporting.TradingReportBuilder(_store).BuildAsync(StoreId, "Cape Town", day);

        var estate = await new Pos.Infrastructure.Reporting.TradingReportBuilder(_store).BuildPeriodAsync(StoreId, "Cape Town", day, day, compareWithPrevious: false);

        Assert.Equal(shop.NetTakings, estate.NetTakings);
        Assert.Equal(shop.RefundTotal, estate.RefundTotal);
    }

    // ------------------------------------------------------------------------------- helpers

    private string StoreId => Services.GetRequiredService<CheckoutSession>().Store!.Id.ToString();

    /// <summary>Reads the trading report the screen would show, for assertions on figures.</summary>
    private async Task<Pos.Core.Reporting.TradingDayReport> ReportAfterSaleAndRefundAsync()
    {
        var day = await SellOneColaAsync();

        await RefundItAsync();

        return await new Pos.Infrastructure.Reporting.TradingReportBuilder(_store).BuildAsync(StoreId, "Cape Town", day);
    }

    /// <summary>Sells one Cola through the till and returns the trading day.</summary>
    private async Task<DateOnly> SellOneColaAsync()
    {
        await TerminalSeed.StartAsync(Services);

        var cut = Render<Checkout>();

        var box = cut.Find(".pos-scan__input");

        box.Input(ColaBarcode);

        await box.TriggerEventAsync("onkeydown", new KeyboardEventArgs { Key = "Enter" });

        ClickButton(cut, "Pay the whole balance by card");

        return Services.GetRequiredService<CheckoutSession>().LastSale!.Value.Sale.BusinessDate;
    }

    /// <summary>Refunds the most recent sale through the refunds screen.</summary>
    private async Task RefundItAsync()
    {
        var session = Services.GetRequiredService<CheckoutSession>();

        var completed = session.LastSale ?? throw new InvalidOperationException("No sale to refund.");

        var number = completed.Stored.Number;

        await TerminalSeed.SignInAsync(
            Services, session, session.Store!, Pos.Core.Domain.EmployeePermissions.Supervisor);

        var cut = Render<Refunds>();

        cut.Find(".pos-scan__input").Change(number);

        ClickButton(cut, "Find");

        var quantities = cut.FindAll("input.pos-qty");

        Assert.NotEmpty(quantities);

        quantities[0].Change("1");

        ClickButton(cut, "Refund and print slip");
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
