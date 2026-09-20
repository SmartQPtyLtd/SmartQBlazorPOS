// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Text.Json;
using Microsoft.JSInterop;
using Pos.Core.Domain;

namespace Pos.Web.Terminal;

/// <summary>
/// The basket state shown on the customer-facing display.
/// </summary>
/// <param name="Lines">Line items, already formatted for display.</param>
/// <param name="Total">Amount due.</param>
/// <param name="ItemCount">Number of lines, for the summary.</param>
/// <param name="IsComplete">
/// True once the sale is settled, so the display can show a thank-you rather than a stale
/// basket.
/// </param>
/// <param name="Message">Optional message, e.g. the last item scanned.</param>
/// <param name="UpdatedAt">When this state was published, for the staleness indicator.</param>
public sealed record CustomerDisplayState(
    IReadOnlyList<CustomerDisplayLine> Lines,
    decimal Total,
    int ItemCount,
    bool IsComplete = false,
    string? Message = null,
    string? UpdatedAt = null)
{
    /// <summary>The idle state shown before anything is scanned.</summary>
    public static CustomerDisplayState Idle { get; } = new([], 0m, 0, Message: "Welcome");

    /// <summary>The state shown immediately after a sale completes.</summary>
    public static CustomerDisplayState ThankYou(decimal total) =>
        new([], total, 0, IsComplete: true, Message: "Thank you!");
}

/// <summary>One line as the customer sees it.</summary>
/// <param name="Name">Product name.</param>
/// <param name="Quantity">Units, formatted so whole numbers read as "2" and not "2.0".</param>
/// <param name="LineTotal">Amount for this line.</param>
public sealed record CustomerDisplayLine(string Name, string Quantity, decimal LineTotal);

/// <summary>
/// Publishes the basket to a customer-facing display.
/// </summary>
/// <remarks>
/// <para>
/// Uses <c>BroadcastChannel</c>, which is same-origin and needs no device API, so it works in
/// every browser and keeps working with no network. That matters: a customer display that
/// goes blank when the shop's internet drops looks like the till has failed.
/// </para>
/// <para>
/// Publishing is best-effort by design. A display that is closed, crashed, or blocked must
/// never affect a sale, so every failure here is swallowed.
/// </para>
/// </remarks>
public sealed class CustomerDisplayPublisher(IJSRuntime js, ILogger<CustomerDisplayPublisher> logger)
{
    private const string ModulePath = "./js/customer-display.js";

    /// <summary>
    /// How often the till tells the display it is still there.
    /// </summary>
    /// <remarks>
    /// Comfortably inside the display's own ten-second staleness window, so a missed beat or two
    /// does not make a working till look dead. Short enough that a display which really has lost the
    /// till is corrected quickly.
    /// </remarks>
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(4);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IJSRuntime _js = js ?? throw new ArgumentNullException(nameof(js));
    private readonly ILogger<CustomerDisplayPublisher> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private IJSObjectReference? _module;
    private bool _unavailable;

    private CancellationTokenSource? _heartbeat;
    private Task? _heartbeatLoop;

    /// <summary>True when the browser cannot carry the display channel at all.</summary>
    public bool IsUnavailable => _unavailable;

    /// <summary>Whether the browser supports the channel.</summary>
    public async Task<bool> IsSupportedAsync(CancellationToken ct = default)
    {
        if (_unavailable)
        {
            return false;
        }

        try
        {
            var module = await ModuleAsync(ct).ConfigureAwait(false);
            var supported = await module.InvokeAsync<bool>("isSupported", ct).ConfigureAwait(false);

            _unavailable = !supported;
            return supported;
        }
        catch (JSException)
        {
            _unavailable = true;
            return false;
        }
    }

    /// <summary>
    /// Publishes the current basket.
    /// </summary>
    /// <remarks>
    /// Called after every basket edit rather than on a timer, so the customer sees a line
    /// appear the moment it is scanned. A timer would introduce a visible lag that reads as a
    /// fault at the till.
    /// </remarks>
    public async Task PublishAsync(CustomerDisplayState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (_unavailable)
        {
            return;
        }

        try
        {
            var module = await ModuleAsync(ct).ConfigureAwait(false);
            var payload = JsonSerializer.Serialize(state, Json);

            await module.InvokeVoidAsync("publish", ct, payload).ConfigureAwait(false);

            // The first thing the till says to a display also starts the heartbeat. Before this
            // existed, a display went stale — and claimed the till had gone — after ten seconds of a
            // shop being a shop: a customer browsing, an operator bagging. The display has handled a
            // heartbeat message from the beginning; nothing ever sent one.
            StartHeartbeat();
        }
        catch (JSException ex)
        {
            // Best-effort: the display is an output only, so a failure here must never
            // interrupt a sale.
            TerminalLog.DisplayPublishFailed(_logger, ex);
        }
    }

    /// <summary>
    /// Tells the display that the till is still there.
    /// </summary>
    /// <remarks>
    /// Sent on a timer rather than only alongside a basket, because the display's liveness indicator
    /// answers "is the till still running", and an idle till is running perfectly well. A status
    /// light that is wrong most of the time is one everybody learns to ignore.
    /// </remarks>
    public async Task SendHeartbeatAsync(CancellationToken ct = default)
    {
        if (_unavailable)
        {
            return;
        }

        try
        {
            var module = await ModuleAsync(ct).ConfigureAwait(false);

            await module.InvokeVoidAsync(
                "sendHeartbeat",
                ct,
                DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            TerminalLog.DisplayPublishFailed(_logger, ex);
        }
    }

    /// <summary>
    /// Starts the heartbeat, or leaves the running one alone.
    /// </summary>
    /// <param name="interval">How often to beat. Defaults to <see cref="DefaultHeartbeatInterval"/>.</param>
    /// <remarks>
    /// Idempotent so that a till which publishes on every scan does not accumulate one timer per
    /// scan. Stopped again by <see cref="StopHeartbeat"/>, which the checkout screen calls when it
    /// is torn down — a heartbeat that outlived the till would keep telling a display that a closed
    /// till was still running.
    /// </remarks>
    public void StartHeartbeat(TimeSpan? interval = null)
    {
        if (_heartbeat is not null)
        {
            return;
        }

        var period = interval ?? DefaultHeartbeatInterval;

        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "The heartbeat interval must be positive.");
        }

        var cancellation = new CancellationTokenSource();

        _heartbeat = cancellation;

        _heartbeatLoop = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(period);

                while (await timer.WaitForNextTickAsync(cancellation.Token).ConfigureAwait(false))
                {
                    await SendHeartbeatAsync(cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected: this is how the loop ends.
            }
            catch (Exception ex)
            {
                // The display must never be able to affect a sale, so nothing here is allowed to
                // escape onto an unobserved task.
                TerminalLog.DisplayPublishFailed(_logger, ex);
            }
        });
    }

    /// <summary>True while the till is telling the display it is still there.</summary>
    public bool IsHeartbeatRunning => _heartbeat is not null;

    /// <summary>Stops the heartbeat, if one is running.</summary>
    public void StopHeartbeat()
    {
        var cancellation = _heartbeat;

        if (cancellation is null)
        {
            return;
        }

        _heartbeat = null;
        _heartbeatLoop = null;

        cancellation.Cancel();
        cancellation.Dispose();
    }

    /// <summary>Builds the display state from a cart.</summary>
    public static CustomerDisplayState FromCart(Cart cart, CartTotals totals, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(cart);

        var lines = cart.Lines
            .Select(line => new CustomerDisplayLine(
                Name: line.Name,
                Quantity: FormatQuantity(line.Quantity),
                LineTotal: line.NetAmount))
            .ToArray();

        return new CustomerDisplayState(
            Lines: lines,
            Total: totals.Total,
            ItemCount: lines.Length,
            Message: message,
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O"));
    }

    private static string FormatQuantity(decimal quantity) =>
        quantity == decimal.Truncate(quantity)
            ? decimal.Truncate(quantity).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : quantity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private async ValueTask<IJSObjectReference> ModuleAsync(CancellationToken ct) =>
        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", ct, ModulePath).ConfigureAwait(false);
}
