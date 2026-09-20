// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Pos.Infrastructure.Storage;
using Pos.Web.Pages;
using Pos.Web.Terminal;

namespace Pos.Web.Tests;

/// <summary>
/// Drives catalogue management and the stock ledger.
/// </summary>
/// <remarks>
/// The rule this screen exists to get right is what a physical count <em>records</em>. Storing the
/// counted figure would only produce a correct level if the level were already correct — which is the
/// thing a count is there to question. What has to be stored is the difference between what was
/// counted and what the ledger said.
/// </remarks>
public sealed class CatalogScreenTests : BunitContext
{
    private const string ColaBarcode = "6001000000017";

    private readonly InMemoryLocalStore _store = new();
    private readonly InMemoryShiftStore _roster;

    public CatalogScreenTests()
    {
        _roster = new InMemoryShiftStore(_store);

        JSInterop.Mode = JSRuntimeMode.Loose;

        TerminalSeed.Compose(Services, _store, _roster, [], new FakeNavigationManager());
    }

    /// <summary>
    /// A count records the difference, so the level ends up at what was counted.
    /// </summary>
    /// <remarks>
    /// The product is seeded at 20 by a goods receipt, then counted at 17. The movement recorded must
    /// be −3: recording 17 would leave the shelf showing 37, and recording 3 would leave it showing
    /// 23. Only the difference lands the level on the counted figure.
    /// </remarks>
    [Fact]
    public async Task APhysicalCount_RecordsTheDifference()
    {
        var cut = await OpenCatalogAsync();

        SelectProduct(cut, "Cola 500ml");

        // It arrives at 20 from the goods receipt the terminal seeded.
        Assert.Equal(20m, await LevelAsync("Cola 500ml"));

        RecordMovement(cut, kind: "count", quantity: 17m, reason: "stocktake");

        Assert.Equal(17m, await LevelAsync("Cola 500ml"));
    }

    /// <summary>A count that finds more than the ledger expects records a positive difference.</summary>
    [Fact]
    public async Task ACountThatFindsMore_RecordsAPositiveDifference()
    {
        var cut = await OpenCatalogAsync();

        SelectProduct(cut, "Cola 500ml");

        RecordMovement(cut, kind: "count", quantity: 24m, reason: "stocktake");

        Assert.Equal(24m, await LevelAsync("Cola 500ml"));
    }

    /// <summary>A write-off takes stock off the shelf and says why.</summary>
    [Fact]
    public async Task AWriteOff_ReducesTheLevelAndKeepsTheReason()
    {
        var cut = await OpenCatalogAsync();

        SelectProduct(cut, "Cola 500ml");

        RecordMovement(cut, kind: "writeoff", quantity: 2m, reason: "Damaged in transit");

        Assert.Equal(18m, await LevelAsync("Cola 500ml"));

        // The ledger is the explanation of the current figure, so the reason has to be in it.
        var history = await _store.GetStockMovementsAsync(StoreId, (await ProductIdOfAsync("Cola 500ml")));

        Assert.Contains(history, m => string.Equals(m.Reference, "Damaged in transit", StringComparison.Ordinal));
    }

    /// <summary>
    /// A movement with no reason is refused.
    /// </summary>
    /// <remarks>
    /// A correction with no explanation is indistinguishable from a mistake or a theft, and the
    /// ledger is append-only — so the explanation cannot be added afterwards.
    /// </remarks>
    [Fact]
    public async Task AMovementWithNoReason_IsRefused()
    {
        var cut = await OpenCatalogAsync();

        SelectProduct(cut, "Cola 500ml");

        RecordMovement(cut, kind: "writeoff", quantity: 1m, reason: "   ");

        Assert.Equal(20m, await LevelAsync("Cola 500ml"));
        Assert.Contains("reason", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The ledger keeps every movement, so the level can still be explained afterwards.
    /// </summary>
    /// <remarks>
    /// Corrections are appended, never written over. A screen that replaced the last figure would
    /// leave a level nobody could account for.
    /// </remarks>
    [Fact]
    public async Task Movements_AreAppendedAndNeverOverwritten()
    {
        var cut = await OpenCatalogAsync();

        SelectProduct(cut, "Cola 500ml");

        RecordMovement(cut, kind: "writeoff", quantity: 1m, reason: "first");
        RecordMovement(cut, kind: "writeoff", quantity: 2m, reason: "second");

        var history = await _store.GetStockMovementsAsync(StoreId, (await ProductIdOfAsync("Cola 500ml")));

        Assert.Contains(history, m => string.Equals(m.Reference, "first", StringComparison.Ordinal));
        Assert.Contains(history, m => string.Equals(m.Reference, "second", StringComparison.Ordinal));
        Assert.Equal(17m, await LevelAsync("Cola 500ml"));
    }

    /// <summary>
    /// Printing a shelf label with no label printer reports the fact rather than throwing.
    /// </summary>
    /// <remarks>
    /// A shop with no label printer is an ordinary shop. The shelf keeps its old label until somebody
    /// tries again, and the operator is told why nothing happened.
    /// </remarks>
    [Fact]
    public async Task PrintingAShelfLabelWithNoPrinter_SaysSo()
    {
        var cut = await OpenCatalogAsync();

        SelectProduct(cut, "Cola 500ml");

        ClickButton(cut, "Print shelf label");

        Assert.Contains("did not print", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A withdrawn product stays out of the list even when withdrawn ones are shown.</summary>
    /// <remarks>
    /// A product withdrawn <em>and deleted</em> is gone, and no filter brings it back. This was a
    /// real defect: asking for inactive products returned deleted ones too, so a shop could edit and
    /// re-sell something head office had removed.
    /// </remarks>
    [Fact]
    public async Task ADeletedProduct_IsNeverListed()
    {
        await TerminalSeed.StartAsync(Services);

        await _store.UpsertProductsAsync(
        [
            new StoredProduct
            {
                Id = Guid.CreateVersion7().ToString("N"),
                StoreId = StoreId,
                Barcode = "6001000000999",
                Name = "Withdrawn Line",
                UnitPrice = 5m,
                TaxName = "VAT",
                TaxRate = 0.15m,
                IsActive = false,
                IsDeleted = true,
            },
        ]);

        var cut = Render<Catalog>();

        ClickButton(cut, "Hiding withdrawn");

        Assert.DoesNotContain("Withdrawn Line", cut.Markup, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------- helpers

    private string StoreId => Services.GetRequiredService<CheckoutSession>().Store!.Id.ToString();

    /// <summary>Starts the terminal and opens the catalogue.</summary>
    private async Task<IRenderedComponent<Catalog>> OpenCatalogAsync()
    {
        await TerminalSeed.StartAsync(Services);

        // The catalogue seed puts 20 Colas on the shelf, so a count has something to disagree with.
        var cola = (await _store.GetProductsAsync(StoreId, false, 500))
            .Single(p => p.Barcode == ColaBarcode);

        await Services
            .GetRequiredService<Pos.Infrastructure.Checkout.StockService>()
            .RecordGoodsReceiptAsync(cola.Id, 20m, "OPENING");

        return Render<Catalog>();
    }

    /// <summary>Finds a product by name and opens its management panel.</summary>
    private static void SelectProduct(IRenderedComponent<Catalog> cut, string name)
    {
        var rows = cut.FindAll("tbody tr");

        foreach (var row in rows)
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

    /// <summary>Fills in the movement form and records it.</summary>
    private static void RecordMovement(
        IRenderedComponent<Catalog> cut,
        string kind,
        decimal quantity,
        string reason)
    {
        cut.Find("#kind").Change(kind);

        cut.Find("#qty").Change(quantity.ToString(System.Globalization.CultureInfo.InvariantCulture));

        cut.Find("#reason").Change(reason);

        ClickButton(cut, "Record movement");
    }

    private async Task<decimal> LevelAsync(string productName) =>
        _store.DeriveStockLevels(StoreId)[await ProductIdOfAsync(productName)].Quantity;

    /// <summary>
    /// Finds a product's id by name.
    /// </summary>
    /// <remarks>
    /// Asynchronous all the way down. Blocking on async work is forbidden in this codebase for a
    /// reason — Blazor WebAssembly has one thread — and a test is not a licence to demonstrate the
    /// pattern the production code is not allowed to use.
    /// </remarks>
    private async Task<string> ProductIdOfAsync(string productName)
    {
        var products = await _store.GetProductsAsync(StoreId, false, 500);

        return products.Single(p => string.Equals(p.Name, productName, StringComparison.Ordinal)).Id;
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
