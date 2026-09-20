// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Web.Pages;

namespace Pos.Web.Tests;

/// <summary>
/// Renders each screen of the terminal and checks it produces a page rather than an exception.
/// </summary>
/// <remarks>
/// <para>
/// This is the check that was missing. Every screen compiled, and three of them had been looked at in
/// a browser once; the rest were taken on trust. A Razor page can compile perfectly and still throw
/// the first time it is opened — a markup expression over a null, a loop over a sequence that was
/// never populated, a component parameter bound to something that cannot round-trip.
/// </para>
/// <para>
/// Each test names the screen rather than the assertion, because when one of these fails the useful
/// information is <em>which screen is broken</em>, and the exception that comes back already carries
/// the rest.
/// </para>
/// </remarks>
public sealed class PageRenderTests
{
    /// <summary>
    /// The checkout screen renders with nobody signed in, which is the state a shop opens in.
    /// </summary>
    /// <remarks>
    /// Asserted against the scan box rather than the page title: <c>PageTitle</c> is a head element
    /// that a real browser applies through interop, so a server-side render legitimately omits it.
    /// Asserting on it would be testing the renderer, not the page.
    /// </remarks>
    [Fact]
    public async Task The_till_renders()
    {
        await using var pages = await PageHarness.CreateAsync();

        var html = await pages.RenderAsync<Checkout>();

        Assert.Contains("Scan a barcode", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_till_renders_with_an_operator_signed_in()
    {
        await using var pages = await PageHarness.CreateAsync();

        await pages.SignInAsync();

        var html = await pages.RenderAsync<Checkout>();

        Assert.Contains("Anele", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_sign_in_screen_renders()
    {
        await using var pages = await PageHarness.CreateAsync();

        var html = await pages.RenderAsync<SignIn>();

        Assert.Contains("Sign in", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_catalogue_screen_renders_its_products()
    {
        await using var pages = await PageHarness.CreateAsync();

        // The startup seed is the catalogue, so a rendered catalogue should name a product from it.
        var html = await pages.RenderAsync<Catalog>();

        Assert.Contains("Cola 500ml", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_shift_screen_renders()
    {
        await using var pages = await PageHarness.CreateAsync();

        await pages.SignInAsync();

        var html = await pages.RenderAsync<Shift>();

        Assert.Contains("Anele", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_refunds_screen_renders()
    {
        await using var pages = await PageHarness.CreateAsync();

        var html = await pages.RenderAsync<Refunds>();

        Assert.False(string.IsNullOrWhiteSpace(html));
    }

    [Fact]
    public async Task The_reports_screen_renders()
    {
        await using var pages = await PageHarness.CreateAsync();

        var html = await pages.RenderAsync<Reports>();

        Assert.False(string.IsNullOrWhiteSpace(html));
    }

    [Fact]
    public async Task The_transfers_screen_renders()
    {
        await using var pages = await PageHarness.CreateAsync();

        await pages.SignInAsync();

        var html = await pages.RenderAsync<Transfers>();

        Assert.Contains("Send stock to another store", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_device_settings_screen_renders()
    {
        await using var pages = await PageHarness.CreateAsync();

        var html = await pages.RenderAsync<DeviceSettings>();

        Assert.Contains("Enrol this terminal", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_customer_display_renders()
    {
        await using var pages = await PageHarness.CreateAsync();

        var html = await pages.RenderAsync<CustomerDisplay>();

        Assert.False(string.IsNullOrWhiteSpace(html));
    }

    [Fact]
    public async Task The_not_found_page_renders()
    {
        await using var pages = await PageHarness.CreateAsync();

        var html = await pages.RenderAsync<NotFound>();

        Assert.Contains("Back to checkout", html, StringComparison.Ordinal);
    }
}
