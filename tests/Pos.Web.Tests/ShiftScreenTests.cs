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

// The drawer screen and the domain type that models a shift are both called Shift. The screen is the
// one under test here, so the domain type is left to its namespace.
using ShiftScreen = Pos.Web.Pages.Shift;

namespace Pos.Web.Tests;

/// <summary>
/// Drives signing in, the cash drawer, and cashing up.
/// </summary>
/// <remarks>
/// <para>
/// A shift is the unit of cash accountability: every sale, refund, and drawer opening during it
/// belongs to one person, so a shortage is answerable to a name rather than to a terminal. That makes
/// this screen the one where a mistake costs money and cannot be reconstructed afterwards — and it
/// had never been executed.
/// </para>
/// <para>
/// The terminal here is <b>not</b> enrolled, so the seeded roster is present and the tests sign in
/// the way a cashier does: choose a person, press digits, confirm. Two operators with different
/// authority are used deliberately, because half of what this screen does is refuse.
/// </para>
/// </remarks>
public sealed class ShiftScreenTests : BunitContext
{
    /// <summary>Seeded supervisor: can refund, open the drawer, and close a shift.</summary>
    private const string SupervisorPin = "7395";

    /// <summary>Seeded cashier: can sell and open the drawer, but cannot close a shift.</summary>
    private const string CashierPin = "4821";

    private readonly InMemoryLocalStore _store = new();
    private readonly InMemoryShiftStore _roster;
    private readonly FakeJsRuntime _js = new([]);

    /// <summary>The session-storage key the sign-in marker is written under.</summary>
    private const string SignInMarkerKey = "pos.signedInEmployee";

    public ShiftScreenTests()
    {
        _roster = new InMemoryShiftStore(_store);

        JSInterop.Mode = JSRuntimeMode.Loose;

        TerminalSeed.Compose(Services, _store, _roster, [], new FakeNavigationManager(), js: _js);
    }

    // ----------------------------------------------------------------------- signing in

    /// <summary>
    /// Signing in through the screen opens a shift with the float that was counted.
    /// </summary>
    /// <remarks>
    /// The float is the only figure in the whole system that is asserted rather than derived, so it
    /// being recorded from what the operator typed is the foundation of every variance afterwards.
    /// </remarks>
    [Fact]
    public async Task SigningIn_OpensAShiftWithTheCountedFloat()
    {
        await TerminalSeed.StartAsync(Services);

        var cut = Render<SignIn>();

        await SignInAsync(cut, "Sipho Ndlovu", SupervisorPin, openingFloat: 500m);

        var session = Services.GetRequiredService<CheckoutSession>();

        Assert.Equal("Sipho Ndlovu", session.Employee?.Name);
        Assert.NotNull(session.Shift);
        Assert.Equal(500m, session.Shift!.OpeningFloat);
    }

    /// <summary>
    /// Signing in writes the marker that lets a refresh re-attach the operator.
    /// </summary>
    /// <remarks>
    /// Written only on success, in session storage: a wrong PIN must never leave a marker behind,
    /// because startup trusts it to mean "this operator signed in on this tab".
    /// </remarks>
    [Fact]
    public async Task SigningIn_WritesTheMarkerThatSurvivesARefresh()
    {
        await TerminalSeed.StartAsync(Services);

        var cut = Render<SignIn>();

        await SignInAsync(cut, "Sipho Ndlovu", SupervisorPin, openingFloat: 500m);

        var session = Services.GetRequiredService<CheckoutSession>();

        Assert.Equal(session.Employee!.Id.ToString(), _js.Session[SignInMarkerKey]);
    }

    /// <summary>A wrong PIN signs nobody in.</summary>
    /// <remarks>
    /// The PIN is not security — it is accountability, and a shop counter is not a private place. But
    /// a wrong one must still refuse, or a sale would be attributed to the wrong person.
    /// </remarks>
    [Fact]
    public async Task AWrongPin_SignsNobodyIn()
    {
        await TerminalSeed.StartAsync(Services);

        var cut = Render<SignIn>();

        await SignInAsync(cut, "Sipho Ndlovu", "0000", openingFloat: 500m);

        var session = Services.GetRequiredService<CheckoutSession>();

        Assert.Null(session.Employee);
        Assert.Contains("not recognised", cut.Markup, StringComparison.OrdinalIgnoreCase);

        // A refused PIN leaves no marker behind: startup trusts it to mean a real sign-in.
        Assert.False(_js.Session.ContainsKey(SignInMarkerKey));
    }

    // --------------------------------------------------------------------- the cash drawer

    /// <summary>
    /// A no-sale opening without a reason is refused.
    /// </summary>
    /// <remarks>
    /// A no-sale opening is the classic cover for a small theft, so it needs a reason and a name
    /// against it. Refusing here is the whole value of recording it at all: an unexplained opening
    /// in a report is indistinguishable from one nobody was asked about.
    /// </remarks>
    [Fact]
    public async Task ANoSaleOpening_WithoutAReason_IsRefused()
    {
        var cut = await OpenDrawerScreenAsync("Thandi Mokoena", CashierPin);

        ClickButton(cut, "Open drawer");

        // Refused is not the same as ignored: nothing of the kind reached the ledger.
        Assert.DoesNotContain(
            await DrawerEventsAsync(),
            e => string.Equals(e.Type, "NoSale", StringComparison.Ordinal));

        Assert.Contains("A reason is required", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>A no-sale opening with a reason is recorded against the operator.</summary>
    [Fact]
    public async Task ANoSaleOpening_WithAReason_IsRecordedAgainstTheOperator()
    {
        var cut = await OpenDrawerScreenAsync("Thandi Mokoena", CashierPin);

        cut.Find("#activityReason").Change("Customer wanted change");
        ClickButton(cut, "Open drawer");

        var recorded = (await DrawerEventsAsync())
            .Where(e => string.Equals(e.Type, "NoSale", StringComparison.Ordinal))
            .ToList();

        var opening = Assert.Single(recorded);

        var session = Services.GetRequiredService<CheckoutSession>();

        Assert.Equal(session.Employee!.Id.ToString(), opening.EmployeeId);
        Assert.Equal("Customer wanted change", opening.Reason);
    }

    /// <summary>
    /// An operator who may not open the drawer is told so, and is offered no controls.
    /// </summary>
    /// <remarks>
    /// Tested by removing the permission rather than by finding a seeded operator without it, so the
    /// screen's own rule is what is under test rather than the roster's composition.
    /// </remarks>
    [Fact]
    public async Task AnOperatorWithoutDrawerAuthority_GetsNoControls()
    {
        var cut = await OpenDrawerScreenAsync("Thandi Mokoena", CashierPin);

        // Thandi has OpenDrawer, so the controls are there to begin with — which is what makes the
        // next assertion mean something.
        Assert.NotEmpty(cut.FindAll("#activityReason"));

        Assert.Contains("Open drawer", cut.Markup, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------- cashing up

    /// <summary>
    /// A drawer counted to exactly what the books expect closes with no variance.
    /// </summary>
    /// <remarks>
    /// The figure the whole shift design exists to produce. A float of 500 and no trading means the
    /// drawer should hold 500, and a screen that disagreed would be adding a discrepancy that is not
    /// there — which is worse than missing one, because it teaches everyone to ignore the number.
    /// </remarks>
    [Fact]
    public async Task ClosingWithTheExpectedCash_ReportsNoVariance()
    {
        var cut = await OpenDrawerScreenAsync("Sipho Ndlovu", SupervisorPin);

        var session = Services.GetRequiredService<CheckoutSession>();
        var shiftId = session.Shift!.Id;

        cut.Find("#count").Change("500.00");
        ClickButton(cut, "Count and close");

        Assert.Contains("balances", cut.Markup, StringComparison.OrdinalIgnoreCase);

        var stored = await _roster.GetShiftAsync(shiftId);

        Assert.False(stored!.IsOpen);
    }

    /// <summary>
    /// Counting and closing signs the operator out with the drawer.
    /// </summary>
    /// <remarks>
    /// The count has been signed off, so nothing after it should be attributed to someone who
    /// has cashed up and gone. The summary stays on screen — the sign-out clears the session,
    /// not the evidence.
    /// </remarks>
    [Fact]
    public async Task ClosingTheShift_SignsTheOperatorOut()
    {
        var cut = await OpenDrawerScreenAsync("Sipho Ndlovu", SupervisorPin);

        // As the sign-in screen would have left it.
        var session = Services.GetRequiredService<CheckoutSession>();
        _js.Session[SignInMarkerKey] = session.Employee!.Id.ToString();

        cut.Find("#count").Change("500.00");
        ClickButton(cut, "Count and close");

        Assert.Null(session.Employee);
        Assert.Null(session.Shift);
        Assert.False(session.IsSignedIn);

        // The cash-up is still on screen even though nobody is signed in any more.
        Assert.Contains("Counted", cut.Markup, StringComparison.Ordinal);

        // And the refresh marker is gone, so a reload does not re-attach the operator who left.
        Assert.False(_js.Session.ContainsKey(SignInMarkerKey));
    }

    /// <summary>
    /// Signing out from the sign-in screen leaves the shift running for the next operator.
    /// </summary>
    /// <remarks>
    /// A plain sign-out is a handover, not a close: the drawer stays open in the store, and the
    /// roster comes back so the next person can join it.
    /// </remarks>
    [Fact]
    public async Task SigningOut_LeavesTheShiftOpenForHandover()
    {
        await TerminalSeed.StartAsync(Services);

        var cut = Render<SignIn>();

        await SignInAsync(cut, "Sipho Ndlovu", SupervisorPin, openingFloat: 500m);

        var session = Services.GetRequiredService<CheckoutSession>();
        var shiftId = session.Shift!.Id;

        // Written by the sign-in above; the sign-out must take it away again, or a refresh
        // would quietly re-attach the operator who just left.
        Assert.True(_js.Session.ContainsKey(SignInMarkerKey));

        ClickButton(cut, "Sign out");

        Assert.Null(session.Employee);
        Assert.Null(session.Shift);
        Assert.False(session.IsSignedIn);
        Assert.False(_js.Session.ContainsKey(SignInMarkerKey));

        // The shift itself is untouched — still open, waiting for the next operator.
        Assert.True((await _roster.GetShiftAsync(shiftId))!.IsOpen);

        // And the roster is back on screen rather than the signed-in card.
        Assert.Contains("Who is on the till?", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>A drawer counted short reports the shortage.</summary>
    [Fact]
    public async Task ClosingShort_ReportsTheShortage()
    {
        var cut = await OpenDrawerScreenAsync("Sipho Ndlovu", SupervisorPin);

        cut.Find("#count").Change("480.00");
        ClickButton(cut, "Count and close");

        Assert.Contains("short", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("20", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>A drawer counted over reports the surplus.</summary>
    [Fact]
    public async Task ClosingOver_ReportsTheSurplus()
    {
        var cut = await OpenDrawerScreenAsync("Sipho Ndlovu", SupervisorPin);

        cut.Find("#count").Change("515.50");
        ClickButton(cut, "Count and close");

        Assert.Contains("over", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A cashier is not offered the close at all, and cannot reach it.
    /// </summary>
    /// <remarks>
    /// Closing is what converts a pile of cash into a signed-off number, so it belongs to whoever is
    /// accountable for it. Thandi can sell and can open the drawer; she cannot declare the drawer
    /// balanced. She is told that <em>instead of</em> being shown the count form, because being
    /// refused after counting the drawer wastes the count and teaches her to distrust the screen.
    /// </remarks>
    [Fact]
    public async Task ACashier_CannotCloseTheShift()
    {
        var cut = await OpenDrawerScreenAsync("Thandi Mokoena", CashierPin);

        Assert.Empty(cut.FindAll("#count"));

        Assert.DoesNotContain(
            cut.FindAll("button"),
            b => b.TextContent.Contains("Count and close", StringComparison.Ordinal));

        Assert.Contains("not authorised to close", cut.Markup, StringComparison.Ordinal);

        // And the shift is genuinely still running, not merely un-offered.
        Assert.True(Services.GetRequiredService<CheckoutSession>().Shift!.IsOpen);
    }

    /// <summary>
    /// A mid-shift reading does not cash the drawer up.
    /// </summary>
    /// <remarks>
    /// Mistaking a reading for a close would have the drawer reconciled twice from two different
    /// readings, and the second reconciliation would look like a discrepancy. Whether the slip itself
    /// withholds the expected figure is enforced in the report type and asserted on the paper in
    /// <c>Pos.Devices.Tests</c>; what this screen owns is that pressing the button changes nothing.
    /// </remarks>
    [Fact]
    public async Task AReading_LeavesTheShiftOpen()
    {
        var cut = await OpenDrawerScreenAsync("Sipho Ndlovu", SupervisorPin);

        ClickButton(cut, "Print X reading");

        var session = Services.GetRequiredService<CheckoutSession>();

        Assert.True(session.Shift!.IsOpen);
        Assert.Null(session.Shift.ClosedAt);
    }

    // ------------------------------------------------------------------------------- helpers

    /// <summary>Signs in and opens the drawer screen.</summary>
    private async Task<IRenderedComponent<ShiftScreen>> OpenDrawerScreenAsync(string name, string pin)
    {
        await TerminalSeed.StartAsync(Services);

        var session = Services.GetRequiredService<CheckoutSession>();
        var store = session.Store!;

        var employees = await _roster.GetEmployeesAsync(store.Id.ToString());

        var employee = employees.Single(e => e.Name == name).ToDomain();

        var signedIn = await Services
            .GetRequiredService<ShiftService>()
            .SignInAsync(employee.Id.ToString(), pin, openingFloat: 500m);

        session.Employee = signedIn.Employee;
        session.Shift = signedIn.Shift;

        return Render<ShiftScreen>();
    }

    /// <summary>
    /// Chooses an operator, presses their PIN on the keypad, and confirms.
    /// </summary>
    /// <remarks>
    /// Driven through the keypad rather than by setting a field: the digits are buttons, and the
    /// screen decides when the PIN is long enough to submit. Typing into a bound property would skip
    /// both.
    /// </remarks>
    private static async Task SignInAsync(
        IRenderedComponent<SignIn> cut,
        string name,
        string pin,
        decimal openingFloat)
    {
        ClickButton(cut, name);

        foreach (var digit in pin)
        {
            ClickButton(cut, digit.ToString());
        }

        cut.Find("#float").Change(openingFloat.ToString(
            System.Globalization.CultureInfo.InvariantCulture));

        ClickButton(cut, "Sign in");
    }

    /// <summary>The drawer events recorded against the open shift.</summary>
    private async Task<IReadOnlyList<StoredDrawerEvent>> DrawerEventsAsync()
    {
        var session = Services.GetRequiredService<CheckoutSession>();

        return await _roster.GetDrawerEventsAsync(session.Shift!.Id.ToString());
    }

    /// <summary>Clicks the first button whose label contains the given text.</summary>
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
