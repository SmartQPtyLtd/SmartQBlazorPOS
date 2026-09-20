// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Pos.Infrastructure.Storage;
using Pos.Web.Pages;
using Pos.Web.Terminal;

namespace Pos.Web.Tests;

/// <summary>
/// Drives the customer-facing display from the till.
/// </summary>
/// <remarks>
/// <para>
/// The display is a second window on the same origin, fed over <c>BroadcastChannel</c>. Nothing had
/// ever checked that the till actually publishes, or what it publishes — the projection was tested in
/// isolation and the screen was rendered, but the wire between them was never inspected.
/// </para>
/// <para>
/// These tests read the payloads the till hands to the display module, which is the same JSON the
/// display window deserialises. A basket published with the wrong total, or not published at all, is
/// invisible to every other kind of test here.
/// </para>
/// </remarks>
public sealed class CustomerDisplayTests : BunitContext
{
    private const string ColaBarcode = "6001000000017";
    private const string BreadBarcode = "6001000000031";

    private readonly InMemoryLocalStore _store = new();
    private readonly InMemoryShiftStore _roster;
    private readonly FakeJsRuntime _browser = new();

    public CustomerDisplayTests()
    {
        _roster = new InMemoryShiftStore(_store);

        JSInterop.Mode = JSRuntimeMode.Loose;

        TerminalSeed.Compose(
            Services, _store, _roster, [], new FakeNavigationManager(), js: _browser);
    }

    /// <summary>
    /// Every message the till sent to the display, in the order it sent them.
    /// </summary>
    /// <remarks>
    /// Both kinds count, and the order matters: a basket goes out through <c>publish</c> and a
    /// heartbeat through <c>sendHeartbeat</c>, and the display receives them on one channel. Reading
    /// the two lists separately would make "the last thing the customer saw" ambiguous.
    /// </remarks>
    private IReadOnlyList<JsonElement> Published =>
    [
        .. _browser.Module.Invocations
            .Where(i => i.Identifier is "publish" or "sendHeartbeat")
            .Select(i => i.Args is { Length: > 0 } ? i.Args[^1]?.ToString() : null)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => JsonDocument.Parse(p!).RootElement.Clone()),
    ];

    /// <summary>The most recent basket the display was given.</summary>
    private JsonElement LatestBasket
    {
        get
        {
            var published = Published;

            for (var i = published.Count - 1; i >= 0; i--)
            {
                if (!published[i].TryGetProperty("heartbeat", out _))
                {
                    return published[i];
                }
            }

            throw new InvalidOperationException("Nothing was published to the display.");
        }
    }

    // ------------------------------------------------------------------- publishing the basket

    /// <summary>
    /// Scanning an item tells the display about it.
    /// </summary>
    /// <remarks>
    /// Called after every basket edit rather than on a timer, so the customer sees the line appear
    /// the moment it is scanned. A timer would introduce a visible lag that reads as a fault.
    /// </remarks>
    [Fact]
    public async Task Scanning_an_item_publishes_it_to_the_display()
    {
        var cut = await OpenTillAsync();

        await ScanAsync(cut, ColaBarcode);

        var basket = LatestBasket;

        var lines = basket.GetProperty("lines");

        Assert.Equal(1, lines.GetArrayLength());
        Assert.Equal("Cola 500ml", lines[0].GetProperty("name").GetString());
        Assert.Equal(15.00m, lines[0].GetProperty("lineTotal").GetDecimal());
        Assert.Equal(15.00m, basket.GetProperty("total").GetDecimal());
    }

    /// <summary>
    /// The lines the display is given add up to the total it is given.
    /// </summary>
    /// <remarks>
    /// The one invariant a customer can check for themselves while standing at the counter, and the
    /// only number they are in a position to argue about. Asserted here on what actually crossed the
    /// channel rather than on the projection in isolation.
    /// </remarks>
    [Fact]
    public async Task The_published_lines_add_up_to_the_published_total()
    {
        var cut = await OpenTillAsync();

        await ScanAsync(cut, ColaBarcode);
        await ScanAsync(cut, BreadBarcode);

        var basket = LatestBasket;

        var sum = 0m;

        foreach (var line in basket.GetProperty("lines").EnumerateArray())
        {
            sum += line.GetProperty("lineTotal").GetDecimal();
        }

        Assert.Equal(basket.GetProperty("total").GetDecimal(), sum);
    }

    /// <summary>Scanning the same barcode twice shows a quantity of two, not two lines.</summary>
    [Fact]
    public async Task Scanning_twice_shows_a_quantity_of_two()
    {
        var cut = await OpenTillAsync();

        await ScanAsync(cut, ColaBarcode);
        await ScanAsync(cut, ColaBarcode);

        var lines = LatestBasket.GetProperty("lines");

        Assert.Equal(1, lines.GetArrayLength());
        Assert.Equal("2", lines[0].GetProperty("quantity").GetString());
        Assert.Equal(30.00m, lines[0].GetProperty("lineTotal").GetDecimal());
    }

    /// <summary>A removed item disappears from the display.</summary>
    /// <remarks>
    /// A customer watching the screen sees a mistake corrected. Leaving the line up would have them
    /// querying a total they can see is wrong.
    /// </remarks>
    [Fact]
    public async Task Removing_an_item_removes_it_from_the_display()
    {
        var cut = await OpenTillAsync();

        await ScanAsync(cut, ColaBarcode);
        await ScanAsync(cut, BreadBarcode);

        ClickButton(cut, "Void", rowIndex: 1);

        var basket = LatestBasket;

        Assert.Equal(1, basket.GetProperty("lines").GetArrayLength());
        Assert.Equal(15.00m, basket.GetProperty("total").GetDecimal());
    }

    /// <summary>
    /// Completing a sale shows the customer what they paid, not an empty basket.
    /// </summary>
    /// <remarks>
    /// The last thing a customer sees before walking away. An empty basket with a zero total reads as
    /// though the sale did not happen.
    /// </remarks>
    [Fact]
    public async Task Completing_a_sale_shows_the_thanks_state_with_what_was_paid()
    {
        var cut = await OpenTillAsync();

        await ScanAsync(cut, ColaBarcode);

        ClickButton(cut, "Pay the whole balance by card");

        var last = Published[^1];

        Assert.True(last.GetProperty("isComplete").GetBoolean(), "The display was not told the sale finished.");
        Assert.Equal(15.00m, last.GetProperty("total").GetDecimal());
        Assert.Equal("Thank you!", last.GetProperty("message").GetString());
    }

    // -------------------------------------------------------------------------- the heartbeat

    /// <summary>
    /// Once the till has said anything to the display, it keeps saying it is there.
    /// </summary>
    /// <remarks>
    /// The first basket edit is what starts the heartbeat. Nothing sent one before this round, so an
    /// idle till — a customer browsing, an operator bagging — made the display claim the till had
    /// gone after ten seconds. A status light that is wrong most of the time is one everybody learns
    /// to ignore, which is worse than not having one.
    /// </remarks>
    [Fact]
    public async Task Publishing_starts_the_heartbeat()
    {
        var publisher = Services.GetRequiredService<CustomerDisplayPublisher>();

        Assert.False(publisher.IsHeartbeatRunning);

        await publisher.PublishAsync(CustomerDisplayState.Idle);

        Assert.True(
            publisher.IsHeartbeatRunning,
            "The display was spoken to but the till never said it was still there.");

        publisher.StopHeartbeat();
    }

    /// <summary>The heartbeat carries a timestamp the display can read.</summary>
    /// <remarks>
    /// The wrapping into the shape the display recognises happens in the display module, which is
    /// verified where it lives; this asserts what the till's side of that boundary sends.
    /// </remarks>
    [Fact]
    public async Task The_heartbeat_carries_a_timestamp()
    {
        var publisher = Services.GetRequiredService<CustomerDisplayPublisher>();

        await publisher.SendHeartbeatAsync();

        var sent = _browser.Module.PayloadsFor("sendHeartbeat");

        var stamp = Assert.Single(sent);

        Assert.True(
            DateTimeOffset.TryParse(
                stamp,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out _),
            $"The heartbeat payload was not a timestamp: '{stamp}'.");
    }

    /// <summary>A heartbeat is published periodically.</summary>
    /// <remarks>
    /// The loop itself, at an interval short enough to observe. Started explicitly because starting
    /// it twice is deliberately a no-op, so a test cannot shorten the interval after a publish has
    /// already begun it.
    /// </remarks>
    [Fact]
    public async Task Heartbeats_repeat_while_the_till_is_open()
    {
        var publisher = Services.GetRequiredService<CustomerDisplayPublisher>();

        publisher.StartHeartbeat(TimeSpan.FromMilliseconds(20));

        try
        {
            await Task.Delay(500);

            // Read once: the heartbeat thread is still writing, so a count captured for the
            // assertion and another for the message can disagree — which is exactly the race
            // that made this test flaky.
            var heartbeats = _browser.Module.PayloadsFor("sendHeartbeat").Count;

            Assert.True(
                heartbeats >= 3,
                $"Expected repeated heartbeats, saw {heartbeats}.");
        }
        finally
        {
            publisher.StopHeartbeat();
        }
    }

    /// <summary>Heartbeats stop when the till goes away, rather than running on.</summary>
    [Fact]
    public async Task Heartbeats_stop_when_asked()
    {
        var publisher = Services.GetRequiredService<CustomerDisplayPublisher>();

        publisher.StartHeartbeat(TimeSpan.FromMilliseconds(20));

        await Task.Delay(120);

        publisher.StopHeartbeat();

        Assert.False(publisher.IsHeartbeatRunning);

        var after = _browser.Module.PayloadsFor("sendHeartbeat").Count;

        await Task.Delay(120);

        Assert.Equal(after, _browser.Module.PayloadsFor("sendHeartbeat").Count);
    }

    // ------------------------------------------------------------------------------ helpers

    private async Task<IRenderedComponent<Checkout>> OpenTillAsync()
    {
        await TerminalSeed.StartAsync(Services);

        return Render<Checkout>();
    }

    private static async Task ScanAsync(IRenderedComponent<Checkout> cut, string barcode)
    {
        var box = cut.Find(".pos-scan__input");

        box.Input(barcode);

        await box.TriggerEventAsync("onkeydown", new KeyboardEventArgs { Key = "Enter" });
    }

    /// <summary>Clicks a button by label, optionally the nth match.</summary>
    private static void ClickButton<TComponent>(
        IRenderedComponent<TComponent> cut,
        string label,
        int rowIndex = 0)
        where TComponent : Microsoft.AspNetCore.Components.IComponent
    {
        var matches = cut.FindAll("button")
            .Where(b => b.TextContent.Contains(label, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            rowIndex < matches.Length,
            $"No button #{rowIndex} labelled '{label}'.");

        matches[rowIndex].Click();
    }
}
