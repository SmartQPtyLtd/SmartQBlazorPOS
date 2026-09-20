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
using Pos.Core.Domain;
using Pos.Infrastructure.Checkout;
using Pos.Infrastructure.Storage;
using Pos.Web.Pages;
using Pos.Web.Terminal;

namespace Pos.Web.Tests;

/// <summary>
/// Drives the transfers screen the way a shop does, and checks what it records.
/// </summary>
/// <remarks>
/// <para>
/// The screen was written and then only compiled. Everything in it that could be wrong — building a
/// draft from a search, deciding that a blank count box means "arrived in full", noticing that a
/// shortage is a discrepancy, refusing to move stock without authority — lived in a page nobody had
/// ever clicked.
/// </para>
/// <para>
/// These tests go through the controls rather than around them. The quantity boxes are typed into,
/// the buttons are found by their label and clicked, and the assertions are made against the ledger
/// and the stored document afterwards — so what is verified is the behaviour a shop gets, not the
/// shape of the markup.
/// </para>
/// </remarks>
public sealed class TransfersScreenTests : BunitContext
{
    /// <summary>The branch this terminal belongs to, and the one it sends stock to.</summary>
    private static readonly Guid OtherStore = Guid.Parse("0193f2c1-4d5e-7a8b-9c0d-1e2f3a4b5c6e");

    private readonly InMemoryLocalStore _store = new();
    private readonly InMemoryShiftStore _roster;
    private readonly Dictionary<string, string?> _browser = [];
    private readonly StubHubHandler _hub;

    private Store? _ownStore;

    public TransfersScreenTests()
    {
        _roster = new InMemoryShiftStore(_store);

        // The hub is told the till's own store id only once the terminal has started, because that
        // id is what the till was issued and the directory has to agree with it.
        _hub = new StubHubHandler(
            () => _ownStore?.Id.ToString() ?? string.Empty,
            [(OtherStore.ToString("N"), "JN01", "Johannesburg", true)]);

        JSInterop.Mode = JSRuntimeMode.Loose;

        TerminalSeed.Compose(Services, _store, _roster, _browser, new FakeNavigationManager(), _hub);
    }

    // ------------------------------------------------------------------ raising a transfer

    /// <summary>
    /// Raising with "send now" takes the stock out of this store's ledger.
    /// </summary>
    /// <remarks>
    /// The behavioural claim the whole feature rests on: a dispatch moves stock. Asserting on the row
    /// that appears would only prove the screen drew something.
    /// </remarks>
    [Fact]
    public async Task SendingStock_RemovesItFromThisStoresLedger()
    {
        var cut = await OpenAsync(EmployeePermissions.Manager);

        var before = await LevelAsync("Cola 500ml");

        AddToDraft(cut, "Cola");
        ClickButton(cut, "Raise and send");

        var after = await LevelAsync("Cola 500ml");

        Assert.Equal(before - 1m, after);
    }

    /// <summary>The dispatch is recorded against the branch it was addressed to, and shows as sent.</summary>
    [Fact]
    public async Task ADispatchedTransfer_NamesItsDestinationAndShowsAsInTransit()
    {
        var cut = await OpenAsync(EmployeePermissions.Manager);

        AddToDraft(cut, "Cola");
        ClickButton(cut, "Raise and send");

        var sent = Assert.Single(await OutgoingAsync());

        Assert.Equal("Dispatched", sent.Status);
        Assert.Equal(OtherStore, Guid.Parse(sent.ToStoreId));
        Assert.Equal("CT01", sent.Reference.Split('-')[1]);
        Assert.Equal("JN01", sent.Reference.Split('-')[2]);
    }

    /// <summary>
    /// A draft moves nothing and is not queued for the destination.
    /// </summary>
    /// <remarks>
    /// The reason drafts exist. Goods being staged for a later collection must stay on this store's
    /// books and stay invisible to the branch expecting them.
    /// </remarks>
    [Fact]
    public async Task ADraft_MovesNoStock()
    {
        var cut = await OpenAsync(EmployeePermissions.Manager);

        var before = await LevelAsync("Cola 500ml");

        AddToDraft(cut, "Cola");
        SaveAsDraft(cut);
        ClickButton(cut, "Save draft");

        var after = await LevelAsync("Cola 500ml");
        var draft = Assert.Single(await OutgoingAsync());

        Assert.Equal(before, after);
        Assert.Equal("Draft", draft.Status);
    }

    /// <summary>A draft can be sent later, and only then does the stock leave.</summary>
    [Fact]
    public async Task ADraft_CanBeSentAfterwards()
    {
        var cut = await OpenAsync(EmployeePermissions.Manager);

        var before = await LevelAsync("Cola 500ml");

        AddToDraft(cut, "Cola");
        SaveAsDraft(cut);
        ClickButton(cut, "Save draft");

        ClickButton(cut, "Send");

        var after = await LevelAsync("Cola 500ml");

        Assert.Equal(before - 1m, after);
    }

    /// <summary>The quantity typed into the draft is the quantity that moves.</summary>
    [Fact]
    public async Task TheQuantityTyped_IsTheQuantitySent()
    {
        var cut = await OpenAsync(EmployeePermissions.Manager);

        var before = await LevelAsync("Cola 500ml");

        AddToDraft(cut, "Cola");
        SetQuantity(cut, 6);

        ClickButton(cut, "Raise and send");

        Assert.Equal(before - 6m, await LevelAsync("Cola 500ml"));
    }

    // -------------------------------------------------------------------- booking goods in

    /// <summary>
    /// Booking in with every box left blank receives the transfer in full.
    /// </summary>
    /// <remarks>
    /// The ordinary case, and the one most likely to be broken by treating an empty box as zero —
    /// which would record every line of every transfer as a total loss, and only be noticed when
    /// somebody reconciled the stock.
    /// </remarks>
    [Fact]
    public async Task BookingInWithNoCounts_ReceivesEverything()
    {
        await ExpectTransferAsync(sent: 5);

        var cut = await OpenAsync(EmployeePermissions.Manager);

        var before = await LevelAsync("Cola 500ml");

        ClickButton(cut, "Book in");
        ClickButton(cut, "Book in");

        var received = Assert.Single(await IncomingReceivedAsync());

        Assert.Equal(5m, received.Lines.Single().QuantityCounted);
        Assert.Equal(before + 5m, await LevelAsync("Cola 500ml"));
    }

    /// <summary>
    /// A shortage is recorded, and only what arrived is credited.
    /// </summary>
    /// <remarks>
    /// The number this document exists to produce. Crediting the full quantity would hide the loss;
    /// recording nothing would leave the destination short with no explanation.
    /// </remarks>
    [Fact]
    public async Task AShortCount_IsRecordedAsADiscrepancy()
    {
        await ExpectTransferAsync(sent: 5);

        var cut = await OpenAsync(EmployeePermissions.Manager);

        var before = await LevelAsync("Cola 500ml");

        ClickButton(cut, "Book in");
        SetFirstCount(cut, 3);
        ClickButton(cut, "Book in");

        var received = Assert.Single(await IncomingReceivedAsync());

        Assert.Equal(3m, received.Lines.Single().QuantityCounted);

        // Only what actually arrived reaches the ledger.
        Assert.Equal(before + 3m, await LevelAsync("Cola 500ml"));
    }

    /// <summary>
    /// The shortage is shown as it is typed, signed the way a count is read.
    /// </summary>
    /// <remarks>
    /// The operator is the only person who can explain a shortage, and they are standing at the
    /// screen when they discover it. Being told afterwards, by a report, is too late. The figure is
    /// negative because it is what was counted minus what was sent — five sent, two counted, three
    /// missing, shown as −3.
    /// </remarks>
    [Fact]
    public async Task TheShortfall_IsShownAsItIsTyped()
    {
        await ExpectTransferAsync(sent: 5);

        var cut = await OpenAsync(EmployeePermissions.Manager);

        ClickButton(cut, "Book in");
        SetFirstCount(cut, 2);

        var marked = cut.FindAll("td.transfer-short").Select(td => td.TextContent.Trim()).ToList();

        Assert.Contains("-3", marked);
    }

    /// <summary>
    /// A transfer that has been booked in cannot be booked in a second time.
    /// </summary>
    /// <remarks>
    /// Receiving twice would credit the destination with the whole transfer again — the same rule as
    /// closing a shift twice, and the same consequence: stock that exists only on paper.
    /// </remarks>
    [Fact]
    public async Task AReceivedTransfer_LeavesNothingToBookIn()
    {
        await ExpectTransferAsync(sent: 5);

        var cut = await OpenAsync(EmployeePermissions.Manager);

        ClickButton(cut, "Book in");
        ClickButton(cut, "Book in");

        // Nothing is awaiting receipt any more, so the offer is gone and the screen says so.
        Assert.DoesNotContain(
            "Book in",
            cut.FindAll("section.dev-card")[0].TextContent,
            StringComparison.Ordinal);

        Assert.Contains("Nothing is on its way here", cut.Markup, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------- authority

    /// <summary>
    /// An operator without the transfer permission cannot send stock, and is told why.
    /// </summary>
    /// <remarks>
    /// Moving goods between shops is one of the few operations that makes stock vanish from one set
    /// of books entirely, so it is a supervisor act rather than something anyone who can edit a
    /// price inherits. The button is shown but refuses, so the screen explains the refusal rather
    /// than hiding a feature the operator has been told exists.
    /// </remarks>
    [Fact]
    public async Task AnOperatorWithoutAuthority_CannotSendStock()
    {
        var cut = await OpenAsync(EmployeePermissions.Sell);

        AddToDraft(cut, "Cola");

        var raise = cut.FindAll("button")
            .FirstOrDefault(b => b.TextContent.Contains("Raise and send", StringComparison.Ordinal));

        Assert.NotNull(raise);
        Assert.True(raise.HasAttribute("disabled"));
        Assert.Contains("needs a supervisor", cut.Markup);
    }

    // ------------------------------------------------------------------------------- helpers

    /// <summary>
    /// Starts the terminal enrolled, signs in, and opens the transfers screen.
    /// </summary>
    /// <remarks>
    /// Enrolment is not decoration. The branch list comes from the hub, and a till with no hub knows
    /// of nowhere to send stock — so without this the screen renders its "no destinations" state and
    /// every test below would be exercising a form that is not there.
    /// </remarks>
    private async Task<IRenderedComponent<Transfers>> OpenAsync(EmployeePermissions permissions)
    {
        await PrepareAsync();

        await TerminalSeed.SignInAsync(
            Services, Services.GetRequiredService<CheckoutSession>(), _ownStore!, permissions);

        return Render<Transfers>();
    }

    /// <summary>
    /// Enrols and starts the terminal exactly once per test.
    /// </summary>
    /// <remarks>
    /// Once is load-bearing. Enrolment decides the store id, so enrolling a second time would move
    /// the terminal to a different store and strand everything the test had already seeded — which
    /// would look like the screen losing a transfer rather than the harness changing the books
    /// underneath it.
    /// </remarks>
    private async Task PrepareAsync()
    {
        if (_ownStore is not null)
        {
            return;
        }

        await TerminalSeed.StartEnrolledAsync(Services);

        _ownStore = Services.GetRequiredService<CheckoutSession>().Store;

        await SeedStockedProductAsync();
    }

    /// <summary>
    /// Puts one product on the shelf with stock behind it.
    /// </summary>
    /// <remarks>
    /// Seeded rather than relied on. The terminal is enrolled, and an enrolled till does not get the
    /// demonstration catalogue — its products come from head office. That is the right behaviour, and
    /// it means a test about transferring stock has to supply the stock it is transferring.
    /// </remarks>
    private async Task SeedStockedProductAsync()
    {
        var storeId = _ownStore!.Id.ToString();

        await _store.UpsertProductsAsync(
        [
            new StoredProduct
            {
                Id = ProductId.New().ToString(),
                StoreId = storeId,
                Barcode = "6001000000017",
                Name = "Cola 500ml",
                UnitPrice = 15.00m,
                TaxName = "VAT",
                TaxRate = 0.15m,
                IsActive = true,
            },
        ]);

        var product = (await _store.GetProductsAsync(storeId, false, 500)).Single();

        await Services
            .GetRequiredService<StockService>()
            .RecordGoodsReceiptAsync(product.Id, 20m, "OPENING");
    }

    /// <summary>Types into the product search and clicks the first match.</summary>
    private static void AddToDraft(IRenderedComponent<Transfers> cut, string search)
    {
        cut.Find("#findProduct").Input(search);

        var matches = cut.FindAll(".transfer-matches button");

        Assert.NotEmpty(matches);

        matches[0].Click();
    }

    /// <summary>Types a quantity into the only draft line.</summary>
    private static void SetQuantity(IRenderedComponent<Transfers> cut, decimal quantity)
    {
        var box = cut.FindAll("input.transfer-count").Single();

        box.Change(quantity.ToString(System.Globalization.CultureInfo.CurrentCulture));
    }

    /// <summary>
    /// Unticks "send now", so raising leaves a draft instead of putting goods on a van.
    /// </summary>
    /// <remarks>
    /// Ticked is the default, because the ordinary case is handing a box to a driver who is already
    /// waiting. Staging goods for a later collection is the exception, so a test for it has to say
    /// so explicitly — which is also what an operator does.
    /// </remarks>
    private static void SaveAsDraft(IRenderedComponent<Transfers> cut) =>
        cut.Find(".transfer-check input").Change(false);

    /// <summary>Types a counted quantity into the first line awaiting receipt.</summary>
    private static void SetFirstCount(IRenderedComponent<Transfers> cut, decimal counted)
    {
        var boxes = cut.FindAll("input.transfer-count");

        Assert.NotEmpty(boxes);

        boxes[0].Input(counted.ToString(System.Globalization.CultureInfo.CurrentCulture));
    }

    /// <summary>Clicks the first enabled button whose label contains the given text.</summary>
    /// <remarks>
    /// Found by label rather than position because the same words appear more than once — "Book in"
    /// is both the offer in the list and the confirmation on the form — and a test that clicked
    /// whichever came first would be asserting on the order the markup happens to be written in.
    /// </remarks>
    private static void ClickButton(IRenderedComponent<Transfers> cut, string label)
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

    /// <summary>The derived stock level for a product, by name.</summary>
    private async Task<decimal> LevelAsync(string productName)
    {
        var catalogue = await Services
            .GetRequiredService<StockService>()
            .GetCatalogueAsync(_ownStore!.Id.ToString(), productName);

        return catalogue.Single().Stock.Quantity;
    }

    private async Task<IReadOnlyList<StoredStockTransfer>> OutgoingAsync() =>
        await _store.GetTransfersAsync(_ownStore!.Id.ToString(), TransferDirection.Outgoing);

    private async Task<IReadOnlyList<StoredStockTransfer>> IncomingReceivedAsync() =>
        await _store.GetTransfersAsync(_ownStore!.Id.ToString(), TransferDirection.Incoming);

    /// <summary>
    /// Puts a dispatched transfer on this store's books, as the hub's relay would.
    /// </summary>
    /// <remarks>
    /// Written straight to the store because this is the receiving side: the document arrives from
    /// another shop, and the applier that stores it is what the relay tests cover. What is under test
    /// here is what the screen does with it.
    /// </remarks>
    private async Task ExpectTransferAsync(decimal sent)
    {
        await PrepareAsync();

        var product = (await Services
            .GetRequiredService<StockService>()
            .GetCatalogueAsync(_ownStore!.Id.ToString(), "Cola 500ml")).Single().Product;

        var transfer = new StockTransfer
        {
            Id = Guid.CreateVersion7().ToString("N"),
            FromStoreId = new StoreId(OtherStore),
            ToStoreId = _ownStore.Id,
            Reference = "TR-JN01-CT01-0001",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            CreatedByEmployeeId = "emp-jn",
            Lines = [new StockTransferLine(product.Id, product.Barcode, product.Name, sent)],
        };

        transfer.Dispatch(DateTimeOffset.UtcNow.AddHours(-1));

        await _store.SaveTransferAsync(StoredStockTransfer.FromDomain(transfer, dispatchTerminalSeq: 1));
    }
}
