// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Microsoft.Extensions.Logging.Abstractions;
using Pos.Infrastructure.Checkout;
using Pos.Infrastructure.Storage;
using Pos.Web.Terminal;

namespace Pos.Web.Tests;

/// <summary>
/// Tests that a terminal reload does not orphan its own data.
/// </summary>
/// <remarks>
/// <para>
/// Every local query is scoped by store id — sales, refunds, employees, shifts, stock movements, and
/// the sale-number sequence. So whatever value the terminal uses as its store id has to survive a
/// reload, because a browser refresh is the most ordinary event in a shop.
/// </para>
/// <para>
/// A reload is modelled faithfully here: the local store and the browser's local storage both
/// persist, while the object graph — identity, session, startup — is rebuilt from scratch. That is
/// exactly what a refresh does, and the store id is the only thing that has to line up for the
/// second session to see the first one's trading.
/// </para>
/// </remarks>
public sealed class TerminalStartupTests
{
    private static readonly DateOnly TradingDay = new(2026, 1, 15);

    /// <summary>
    /// A reload must not change which store the terminal thinks it is.
    /// </summary>
    /// <remarks>
    /// If it does, the consequences are not cosmetic and they are not gradual. The catalogue empties
    /// and will not re-seed, because the seed checks for a barcode without reference to a store; the
    /// day's sales disappear from every report; the open shift is orphaned; and the sale-number
    /// sequence restarts at one, so the till reissues numbers already printed on customers'
    /// receipts. All of it from a refresh.
    /// </remarks>
    [Fact]
    public async Task ReloadingTheTerminal_KeepsTheSameStoreId()
    {
        var (store, roster, browser) = NewTerminal();

        var first = await StartAsync(store, roster, browser);
        var second = await StartAsync(store, roster, browser);

        Assert.Equal(first.Store!.Id, second.Store!.Id);
    }

    /// <summary>A reload must still find the roster the previous session seeded.</summary>
    [Fact]
    public async Task ReloadingTheTerminal_StillFindsTheRoster()
    {
        var (store, roster, browser) = NewTerminal();

        var first = await StartAsync(store, roster, browser);

        var seeded = await roster.GetEmployeesAsync(first.Store!.Id.ToString());

        Assert.NotEmpty(seeded);

        var second = await StartAsync(store, roster, browser);

        var found = await roster.GetEmployeesAsync(second.Store!.Id.ToString());

        Assert.Equal(seeded.Count, found.Count);
    }

    /// <summary>
    /// A reload must not restart the sale number sequence.
    /// </summary>
    /// <remarks>
    /// The number is printed on a customer's receipt. Reissuing one is not a reporting
    /// inconvenience; it is two different sales claiming the same identity.
    /// </remarks>
    [Fact]
    public async Task ReloadingTheTerminal_DoesNotRestartSaleNumbering()
    {
        var (store, roster, browser) = NewTerminal();

        var first = await StartAsync(store, roster, browser);

        var issued = await store.NextSaleSequenceAsync(first.Store!.Id.ToString(), TradingDay);

        var second = await StartAsync(store, roster, browser);

        var next = await store.NextSaleSequenceAsync(second.Store!.Id.ToString(), TradingDay);

        Assert.Equal(issued + 1, next);
    }

    /// <summary>A reload must still see the day's trading.</summary>
    [Fact]
    public async Task ReloadingTheTerminal_StillSeesTheDaysSales()
    {
        var (store, roster, browser) = NewTerminal();

        var first = await StartAsync(store, roster, browser);

        await RecordOneSaleAsync(store, first.Store!.Id.ToString());

        var second = await StartAsync(store, roster, browser);

        var sales = await store.GetSalesForDateAsync(second.Store!.Id.ToString(), TradingDay);

        Assert.Single(sales);
    }

    /// <summary>
    /// A reload must not re-seed the demo catalogue over a real one.
    /// </summary>
    /// <remarks>
    /// The seed inserts a fixed set of demonstration products. Running it again on every load would
    /// be harmless only while the ids happen to collide; the moment the store id is stable — which
    /// is the point of this fix — a second seed would double the catalogue.
    /// </remarks>
    [Fact]
    public async Task ReloadingTheTerminal_DoesNotDuplicateTheCatalogue()
    {
        var (store, roster, browser) = NewTerminal();

        var first = await StartAsync(store, roster, browser);

        var seeded = await store.GetProductsAsync(first.Store!.Id.ToString(), false, 500);

        var second = await StartAsync(store, roster, browser);

        var after = await store.GetProductsAsync(second.Store!.Id.ToString(), false, 500);

        Assert.Equal(seeded.Count, after.Count);
    }

    // ------------------------------------------------------------------ shift restore

    /// <summary>
    /// The session-storage key <see cref="OperatorSignInMarker"/> writes. Duplicated here so a
    /// rename breaks these tests rather than slipping past them.
    /// </summary>
    private const string SignInMarkerKey = "pos.signedInEmployee";

    /// <summary>Seeded supervisor, signed in by PIN the way the sign-in screen does.</summary>
    private const string SupervisorPin = "7395";

    /// <summary>
    /// A mid-shift refresh re-attaches the operator whose marker is on the tab.
    /// </summary>
    /// <remarks>
    /// The ordinary case the marker exists to protect: signed in, page refreshed, shift still
    /// open. Restoring anything less would orphan the drawer's takings from the person
    /// answerable for them.
    /// </remarks>
    [Fact]
    public async Task ARefresh_RestoresTheOperator_WhenTheirMarkerIsPresent()
    {
        var (store, roster, browser) = NewTerminal();
        var sessionStorage = new Dictionary<string, string?>();

        var first = await StartAsync(store, roster, browser, sessionStorage);

        var signedIn = await SignInAsync(store, roster, browser, first.Store!.Id.ToString());

        // The sign-in screen writes this when the PIN succeeds.
        sessionStorage[SignInMarkerKey] = signedIn.Employee.Id.ToString();

        var second = await StartAsync(store, roster, browser, sessionStorage);

        Assert.Equal("Sipho Ndlovu", second.Employee?.Name);
        Assert.NotNull(second.Shift);
        Assert.True(second.Shift!.IsOpen);
    }

    /// <summary>
    /// A refresh after an explicit sign-out restores nobody, and orphans nothing.
    /// </summary>
    /// <remarks>
    /// The sign-out cleared the marker, so the open shift is left alone rather than re-attached
    /// to the operator who just left. It stays open in the store for the next operator to join.
    /// </remarks>
    [Fact]
    public async Task ARefresh_RestoresNobody_AfterAnExplicitSignOut()
    {
        var (store, roster, browser) = NewTerminal();
        var sessionStorage = new Dictionary<string, string?>();

        var first = await StartAsync(store, roster, browser, sessionStorage);

        await SignInAsync(store, roster, browser, first.Store!.Id.ToString());

        // What every explicit sign-out path does.
        sessionStorage.Remove(SignInMarkerKey);

        var second = await StartAsync(store, roster, browser, sessionStorage);

        Assert.Null(second.Employee);
        Assert.Null(second.Shift);

        // The drawer itself is untouched — still open, waiting for the next operator.
        var identity = await LoadIdentityAsync(browser);

        Assert.NotNull(await roster.GetOpenShiftAsync(identity.TerminalId));
    }

    /// <summary>
    /// A marker naming somebody other than the shift's operator restores nobody.
    /// </summary>
    /// <remarks>
    /// Re-attaching on a mismatched marker would attribute the drawer's cash to a person who
    /// never opened it, which is worse than asking for a sign-in that was technically avoidable.
    /// </remarks>
    [Fact]
    public async Task ARefresh_RestoresNobody_WhenTheMarkerNamesADifferentOperator()
    {
        var (store, roster, browser) = NewTerminal();
        var sessionStorage = new Dictionary<string, string?>();

        var first = await StartAsync(store, roster, browser, sessionStorage);

        await SignInAsync(store, roster, browser, first.Store!.Id.ToString());

        var employees = await roster.GetEmployeesAsync(first.Store!.Id.ToString());
        var somebodyElse = employees.Single(e => e.Name == "Thandi Mokoena");

        sessionStorage[SignInMarkerKey] = somebodyElse.Id;

        var second = await StartAsync(store, roster, browser, sessionStorage);

        Assert.Null(second.Employee);
        Assert.Null(second.Shift);
    }

    /// <summary>Signs the seeded supervisor in through the same service the sign-in screen uses.</summary>
    private static async Task<SignInResult> SignInAsync(
        ILocalStore store,
        IShiftStore roster,
        Dictionary<string, string?> browser,
        string storeId)
    {
        var identity = await LoadIdentityAsync(browser);

        var employees = await roster.GetEmployeesAsync(storeId);
        var supervisor = employees.Single(e => e.Name == "Sipho Ndlovu");

        var shifts = new ShiftService(roster, store, identity);

        return await shifts.SignInAsync(supervisor.Id, SupervisorPin, openingFloat: 500m);
    }

    /// <summary>Reloads the persisted terminal identity, as a page refresh would.</summary>
    private static async Task<TerminalIdentity> LoadIdentityAsync(Dictionary<string, string?> browser)
    {
        var identity = new TerminalIdentity(new FakeJsRuntime(browser));

        await identity.InitialiseAsync();

        return identity;
    }

    /// <summary>
    /// An enrolled terminal trades on the hub's store id, not a private one of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes an inter-store transfer receivable. A transfer is addressed to a store id
    /// the hub issued, and the receiving store accepts it only when the operator's own store id
    /// matches. A till that kept a locally-minted id could send stock but never book any in.
    /// </para>
    /// <para>
    /// Compared as identifiers, not as text. The hub mints ids without dashes and the domain type
    /// renders them with, so asserting on the string would be asserting on a coincidence of
    /// formatting rather than on the store being the same store.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnEnrolledTerminal_UsesTheHubsStoreId()
    {
        var (store, roster, browser) = NewTerminal();

        var hubStoreId = Guid.CreateVersion7();

        var identity = new TerminalIdentity(new FakeJsRuntime(browser));
        await identity.InitialiseAsync();
        await identity.SaveEnrolmentAsync(
            hubStoreId.ToString("N"),
            "secret",
            "refresh-token",
            DateTimeOffset.UtcNow.AddDays(1),
            "https://hub.example.com/");

        var session = new CheckoutSession();

        var startup = new TerminalStartup(
            identity,
            store,
            roster,
            session,
            new OperatorSignInMarker(new FakeJsRuntime(browser)),
            NullLogger<TerminalStartup>.Instance);

        await startup.InitialiseAsync();

        Assert.Equal(hubStoreId, session.Store!.Id.Value);
    }

    /// <summary>
    /// Enrolment reports that it moved the terminal to a different store.
    /// </summary>
    /// <remarks>
    /// The caller needs to know, because everything already recorded belongs to a store these books
    /// are not, and is queued to be pushed. Silence here would let a demonstration terminal's
    /// takings be posted into a real shop's books on the first sync.
    /// </remarks>
    [Fact]
    public async Task EnrolmentIntoADifferentStore_ReportsThatItMoved()
    {
        var browser = new Dictionary<string, string?>();

        var identity = new TerminalIdentity(new FakeJsRuntime(browser));
        await identity.InitialiseAsync();

        var moved = await identity.SaveEnrolmentAsync(
            Guid.CreateVersion7().ToString("N"),
            "secret",
            "refresh-token",
            DateTimeOffset.UtcNow.AddDays(1),
            "https://hub.example.com/");

        Assert.True(moved);
    }

    /// <summary>Re-enrolling into the same store moves nothing, so nothing is erased.</summary>
    [Fact]
    public async Task ReEnrollingIntoTheSameStore_ReportsThatItDidNotMove()
    {
        var browser = new Dictionary<string, string?>();

        var identity = new TerminalIdentity(new FakeJsRuntime(browser));
        await identity.InitialiseAsync();

        var storeId = Guid.CreateVersion7().ToString("N");

        await identity.SaveEnrolmentAsync(
            storeId, "first", "refresh-one", DateTimeOffset.UtcNow.AddDays(1), "https://hub.example.com/");

        var moved = await identity.SaveEnrolmentAsync(
            storeId, "second", "refresh-two", DateTimeOffset.UtcNow.AddDays(1), "https://hub.example.com/");

        Assert.False(moved);
    }

    /// <summary>A terminal with its own local store, roster, and browser storage.</summary>
    private static (ILocalStore Store, IShiftStore Roster, Dictionary<string, string?> Browser)
        NewTerminal()
    {
        var store = new InMemoryLocalStore();

        return (store, new InMemoryShiftStore(store), []);
    }

    /// <summary>Records a single completed sale, so a later session has something to find.</summary>
    private static async Task RecordOneSaleAsync(ILocalStore store, string storeId)
    {
        await store.CommitSaleAsync(
            new StoredSale
            {
                Id = Guid.CreateVersion7().ToString("N"),
                StoreId = storeId,
                Number = "CT01-20260115-1",
                TerminalSeq = await store.ReserveTerminalSequenceAsync(1),
                TerminalId = "terminal-1",
                CompletedAt = "2026-01-15T09:00:00.0000000+00:00",
                BusinessDate = "2026-01-15",
                LocalHour = 9,
                Currency = "ZAR",
                TaxMode = "Inclusive",
                Status = "Completed",
                Lines = [],
                Tenders = [],
                Subtotal = 10m,
                TotalDiscount = 0m,
                TaxTotal = 1.3m,
                Total = 10m,
            },
            "{}",
            [],
            [],
            "terminal-1");
    }

    /// <summary>
    /// Starts a terminal the way the checkout screen does on first render.
    /// </summary>
    /// <param name="store">Shared local store, as IndexedDB is shared across a reload.</param>
    /// <param name="roster">Shared shift store.</param>
    /// <param name="browser">Local storage, which likewise survives a reload.</param>
    /// <param name="sessionStorage">
    /// Session storage, which also survives a reload in the same tab — it is where the sign-in
    /// marker lives. Shared here for the same reason the local storage dictionary is.
    /// </param>
    private static async Task<CheckoutSession> StartAsync(
        ILocalStore store,
        IShiftStore roster,
        Dictionary<string, string?> browser,
        Dictionary<string, string?>? sessionStorage = null)
    {
        var session = new CheckoutSession();

        var js = new FakeJsRuntime(browser, sessionStorage);

        var identity = new TerminalIdentity(js);

        await identity.InitialiseAsync();

        var startup = new TerminalStartup(
            identity,
            store,
            roster,
            session,
            new OperatorSignInMarker(js),
            NullLogger<TerminalStartup>.Instance);

        await startup.InitialiseAsync();

        return session;
    }
}
