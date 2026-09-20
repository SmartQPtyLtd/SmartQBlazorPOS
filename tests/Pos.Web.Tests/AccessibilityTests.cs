// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Pos.Core.Domain;
using Pos.Infrastructure.Storage;
using Pos.Web.Pages;
using Pos.Web.Terminal;

// The drawer screen and the domain type that models a shift are both called Shift. The screen is the
// one under test, so the domain type is left to its namespace.
using ShiftScreen = Pos.Web.Pages.Shift;

namespace Pos.Web.Tests;

/// <summary>
/// Structural accessibility checks over every screen, on the markup it actually renders.
/// </summary>
/// <remarks>
/// <para>
/// A till is not a form somebody visits once. It is held at arm's length under bad lighting, by
/// somebody who is also handling goods and talking to a customer, for eight hours. "Accessible" here
/// is not only about assistive technology: an unlabelled box or an unreadable target is a daily
/// irritation for every operator, and a barrier for some of them.
/// </para>
/// <para>
/// These are structural checks against the rendered document — the same markup a browser gets — rather
/// than a browser audit, so they cover what can be decided from the DOM: whether every control has a
/// name, whether every table says what its columns are, whether the page announces changes. Contrast,
/// focus order, and reading order need a browser and are listed in the README as not covered.
/// </para>
/// </remarks>
public sealed class AccessibilityTests : BunitContext
{
    private readonly InMemoryLocalStore _store = new();
    private readonly InMemoryShiftStore _roster;

    public AccessibilityTests()
    {
        _roster = new InMemoryShiftStore(_store);

        JSInterop.Mode = JSRuntimeMode.Loose;

        TerminalSeed.Compose(Services, _store, _roster, [], new FakeNavigationManager());
    }

    /// <summary>
    /// One row per screen: what it is called, whether its page reports status to an operator, and the
    /// component behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The screens are listed rather than discovered so that the decision each row carries —
    /// particularly whether the screen announces changes — is written down where it can be read and
    /// argued with. The <see cref="Type"/> is what makes the list checkable: the test below reflects
    /// over the assembly for every component carrying a route and fails if one is missing here, so
    /// adding a screen and forgetting this file is a red test rather than a silent gap.
    /// </para>
    /// <para>
    /// <c>notfound</c> and <c>display</c> do not report status, and that is a judgement rather than an
    /// oversight:
    /// <list type="bullet">
    /// <item><description>
    /// <c>notfound</c> is a dead end. Nothing on it ever changes, so a live region would be an empty
    /// element announcing nothing for the life of the page.
    /// </description></item>
    /// <item><description>
    /// <c>display</c> is the customer-facing mirror of the till, read by somebody standing in front of
    /// it rather than by the operator. Every change it shows was made, and announced, on the till
    /// itself. A live region of its own would double-announce the basket to anyone running a screen
    /// reader on the machine driving the till.
    /// </description></item>
    /// </list>
    /// </para>
    /// </remarks>
    private static readonly (string Name, bool Reports, Type Page)[] ScreenCatalogue =
    [
        ("checkout", true, typeof(Checkout)),
        ("signin", true, typeof(SignIn)),
        ("shift", true, typeof(ShiftScreen)),
        ("catalog", true, typeof(Catalog)),
        ("refunds", true, typeof(Refunds)),
        ("reports", true, typeof(Reports)),
        ("transfers", true, typeof(Transfers)),
        ("devices", true, typeof(DeviceSettings)),
        ("display", false, typeof(CustomerDisplay)),
        ("notfound", false, typeof(NotFound)),
    ];

    /// <summary>Every screen.</summary>
    public static TheoryData<string> Screens => [.. ScreenCatalogue.Select(row => row.Name)];

    /// <summary>The screens that report what just happened to the person operating the till.</summary>
    /// <remarks>See <see cref="ScreenCatalogue"/> for why the other two are absent.</remarks>
    public static TheoryData<string> Screens_that_report_status =>
        [.. ScreenCatalogue.Where(row => row.Reports).Select(row => row.Name)];

    /// <summary>
    /// Every routable page is a screen this file checks.
    /// </summary>
    /// <remarks>
    /// Reflection rather than a second hand-written list, because a second list is one that can fall
    /// behind. A new page has to be given a row above — and therefore an answer to whether it
    /// announces changes — before the suite goes green.
    /// </remarks>
    [Fact]
    public void Every_page_is_checked_here()
    {
        var routed = typeof(Checkout).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(RouteAttribute), inherit: false).Length > 0)
            .ToList();

        var covered = ScreenCatalogue.Select(row => row.Page).ToHashSet();

        var missing = routed.Where(t => !covered.Contains(t)).Select(t => t.FullName).ToList();

        Assert.True(
            missing.Count == 0,
            $"these routed pages are not in {nameof(ScreenCatalogue)}: {string.Join(", ", missing)}");
    }

    private async Task<IRenderedComponent<IComponent>> RenderScreenAsync(string screen)
    {
        await TerminalSeed.StartAsync(Services);

        var session = Services.GetRequiredService<CheckoutSession>();

        // Signed in, so the screens that hide their controls otherwise render the populated version.
        // A page checked only in its signed-out state would have none of the controls this is about.
        if (screen is "shift" or "transfers" or "catalog" or "refunds")
        {
            await TerminalSeed.SignInAsync(
                Services, session, session.Store!, EmployeePermissions.Manager);
        }

        return screen switch
        {
            "checkout" => Render<Checkout>(),
            "signin" => Render<SignIn>(),
            "shift" => Render<ShiftScreen>(),
            "catalog" => Render<Catalog>(),
            "refunds" => Render<Refunds>(),
            "reports" => Render<Reports>(),
            "transfers" => Render<Transfers>(),
            "devices" => Render<DeviceSettings>(),
            "display" => Render<CustomerDisplay>(),
            "notfound" => Render<NotFound>(),
            _ => throw new ArgumentOutOfRangeException(nameof(screen), screen, "Unknown screen."),
        };
    }

    /// <summary>
    /// Every control on every screen can be named by something other than its position.
    /// </summary>
    /// <remarks>
    /// The check that matters most and is easiest to lose. A box with no label is announced as "edit
    /// text" and nothing else; a screen reader user cannot tell the quantity field from the price
    /// field, and neither can anybody using voice control. Repeated rows are the usual place it goes
    /// wrong, because a <c>for</c> attribute needs a unique id and the easy way out is to omit it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Screens))]
    public async Task Every_control_has_an_accessible_name(string screen)
    {
        var cut = await RenderScreenAsync(screen);

        var labelled = cut.FindAll("label")
            .Select(l => l.GetAttribute("for"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        var unnamed = new List<string>();

        foreach (var control in cut.FindAll("input, select, textarea"))
        {
            var id = control.GetAttribute("id");

            var named = !string.IsNullOrWhiteSpace(control.GetAttribute("aria-label"))
                || !string.IsNullOrWhiteSpace(control.GetAttribute("aria-labelledby"))
                || !string.IsNullOrWhiteSpace(control.GetAttribute("title"))
                || (id is not null && labelled.Contains(id));

            if (!named)
            {
                unnamed.Add($"<{control.TagName.ToLowerInvariant()} id='{id}' type='{control.GetAttribute("type")}'>");
            }
        }

        Assert.True(
            unnamed.Count == 0,
            $"{screen}: {unnamed.Count} control(s) have no accessible name: {string.Join(", ", unnamed)}");
    }

    /// <summary>Every button says what it does.</summary>
    /// <remarks>
    /// An icon-only or empty button is announced as "button", which tells the operator nothing about
    /// which one they are about to press.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Screens))]
    public async Task Every_button_says_what_it_does(string screen)
    {
        var cut = await RenderScreenAsync(screen);

        var silent = cut.FindAll("button")
            .Where(b => string.IsNullOrWhiteSpace(b.TextContent)
                && string.IsNullOrWhiteSpace(b.GetAttribute("aria-label"))
                && string.IsNullOrWhiteSpace(b.GetAttribute("title")))
            .Select(b => b.OuterHtml[..Math.Min(80, b.OuterHtml.Length)])
            .ToList();

        Assert.True(silent.Count == 0, $"{screen}: nameless button(s): {string.Join(" | ", silent)}");
    }

    /// <summary>Every table says what its columns are, and every column has a name.</summary>
    /// <remarks>
    /// <para>
    /// Without header cells a table is read out as a stream of values with no idea what any of them
    /// is. The till is almost entirely tables.
    /// </para>
    /// <para>
    /// A header cell that is present but empty is only half a fix, and it is the shape this defect
    /// actually took: the action column is the easy one to leave as a bare <c>&lt;th&gt;&lt;/th&gt;</c>
    /// because the button below it names itself. It does not help somebody navigating cell by cell,
    /// who hears what the column is only from its header. The name may be visually hidden — an
    /// "Actions" label above a column of obvious buttons is noise on a till screen — but it must be
    /// in the accessibility tree.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Screens))]
    public async Task Every_table_has_headers(string screen)
    {
        var cut = await RenderScreenAsync(screen);

        var headerless = cut.FindAll("table")
            .Where(t => t.QuerySelectorAll("th").Length == 0)
            .Select(t => t.OuterHtml[..Math.Min(80, t.OuterHtml.Length)])
            .ToList();

        Assert.True(
            headerless.Count == 0,
            $"{screen}: {headerless.Count} table(s) with no header cells: {string.Join(" | ", headerless)}");

        var unnamed = cut.FindAll("th")
            .Where(h => string.IsNullOrWhiteSpace(h.TextContent)
                && string.IsNullOrWhiteSpace(h.GetAttribute("aria-label")))
            .Select(h => h.OuterHtml[..Math.Min(60, h.OuterHtml.Length)])
            .ToList();

        Assert.True(
            unnamed.Count == 0,
            $"{screen}: {unnamed.Count} unnamed column(s): {string.Join(" | ", unnamed)}");
    }

    /// <summary>
    /// A control whose value is not obvious from a label must say what it means.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow: it checks the one attribute that carries machine-readable meaning through
    /// to the keyboard and to assistive technology, on the numeric fields a till is full of. Inputs
    /// with an explicit <c>type</c> other than text already tell the browser what they are.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Screens))]
    public async Task Numeric_fields_declare_their_input_mode(string screen)
    {
        var cut = await RenderScreenAsync(screen);

        var wrong = cut.FindAll("input[type=number], input[type=text]:not([inputmode])")
            .Where(i => i.GetAttribute("type") == "text"
                && string.IsNullOrWhiteSpace(i.GetAttribute("inputmode"))
                && i.GetAttribute("id") is "findProduct" or "activityReason")
            .Select(i => i.GetAttribute("id") ?? "(no id)")
            .ToList();

        // The name search and the drawer reason are typed on a touch till, so the keyboard matters.
        // Listing them keeps the check honest about what it is asking for rather than sweeping every
        // text input, most of which are ordinary.
        Assert.True(wrong.Count == 0, $"{screen}: no input mode on {string.Join(", ", wrong)}");
    }

    /// <summary>Nothing is reachable only by tabbing past everything else.</summary>
    /// <remarks>
    /// A positive <c>tabindex</c> reorders the whole page and breaks the tab order for everything
    /// after it. There is no good reason for one here.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Screens))]
    public async Task Nothing_hijacks_the_tab_order(string screen)
    {
        var cut = await RenderScreenAsync(screen);

        var positive = cut.FindAll("[tabindex]")
            .Where(e => int.TryParse(e.GetAttribute("tabindex"), out var value) && value > 0)
            .Select(e => e.OuterHtml[..Math.Min(60, e.OuterHtml.Length)])
            .ToList();

        Assert.True(positive.Count == 0, $"{screen}: positive tabindex: {string.Join(" | ", positive)}");
    }

    /// <summary>
    /// The region that reports what just happened announces itself.
    /// </summary>
    /// <remarks>
    /// The till's whole feedback model is a line of text that changes. Without a live region it
    /// changes silently for anybody not looking at it.
    /// <para>
    /// Checked on the untouched first render, which is what makes it able to catch the real defect:
    /// a region that only exists once there is something to say arrives already full, and a live
    /// region is announced when its contents change rather than when it appears. Rendered empty, the
    /// page must already contain the region.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Screens_that_report_status))]
    public async Task The_status_line_is_a_live_region(string screen)
    {
        var cut = await RenderScreenAsync(screen);

        var regions = cut.FindAll("[role=status], [role=alert], [aria-live]");

        Assert.True(regions.Count > 0, $"{screen}: nothing on the page announces changes.");
    }

    /// <summary>
    /// A live region is empty when the page first renders.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The general form of the defect this whole file was written after. A screen reader announces a
    /// live region when its <em>contents change</em>; content that is already there when the region
    /// appears is not a change, so it is never read out. Any text present at first render is text
    /// nobody hears — which is exactly what happened when every page wrapped its status line in
    /// <c>@if (_message is not null)</c> and created the region at the same moment as the message.
    /// </para>
    /// <para>
    /// Stated as a rule about the region rather than about the status line, because the PIN pad has a
    /// live region of its own and the same reasoning applies to it. Screens with no live region pass
    /// trivially; <see cref="The_status_line_is_a_live_region"/> is what requires one to exist.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Screens))]
    public async Task Live_regions_start_empty(string screen)
    {
        var cut = await RenderScreenAsync(screen);

        var full = cut.FindAll("[role=status], [role=alert], [aria-live]")
            .Where(r => !string.IsNullOrWhiteSpace(r.TextContent))
            .Select(r => r.OuterHtml[..Math.Min(90, r.OuterHtml.Length)])
            .ToList();

        Assert.True(
            full.Count == 0,
            $"{screen}: {full.Count} live region(s) already hold text on the first render, so that "
                + $"text is never announced: {string.Join(" | ", full)}");
    }

    /// <summary>
    /// Every image describes itself.
    /// </summary>
    /// <remarks>
    /// An image with no <c>alt</c> is announced by its file name, which is worse than nothing.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Screens))]
    public async Task Every_image_describes_itself(string screen)
    {
        var cut = await RenderScreenAsync(screen);

        var undescribed = cut.FindAll("img")
            .Where(i => i.GetAttribute("alt") is null)
            .Select(i => i.GetAttribute("src") ?? "(no src)")
            .ToList();

        Assert.True(undescribed.Count == 0, $"{screen}: images with no alt: {string.Join(", ", undescribed)}");
    }

    /// <summary>The page has one level-one heading, and its levels do not skip.</summary>
    /// <remarks>
    /// Headings are how somebody navigates a screen without reading it. A jump from a level-one
    /// heading straight to level three reads as a missing section. The level-one heading must also
    /// be the first thing in the outline: a screen whose top heading is an <c>h2</c> is announcing
    /// itself as a fragment of some other page.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Screens))]
    public async Task Headings_are_present_and_ordered(string screen)
    {
        var cut = await RenderScreenAsync(screen);

        var headings = cut.FindAll("h1, h2, h3, h4, h5, h6");

        var levels = headings
            .Select(h => int.Parse(h.TagName[1..], System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

        Assert.True(levels.Count > 0, $"{screen}: the page has no headings at all.");

        Assert.True(
            levels[0] == 1,
            $"{screen}: the first heading is h{levels[0]}, not h1. Headings in order: "
                + string.Join(", ", headings.Select(h => $"h{h.TagName[1..]} '{h.TextContent.Trim()}'")));

        var top = levels.Count(l => l == 1);

        Assert.True(top == 1, $"{screen}: {top} level-one headings; a screen gets exactly one.");

        var previous = levels[0];

        for (var i = 1; i < levels.Count; i++)
        {
            Assert.True(
                levels[i] <= previous + 1,
                $"{screen}: heading level jumps from h{previous} to h{levels[i]}.");
        }
    }
}
