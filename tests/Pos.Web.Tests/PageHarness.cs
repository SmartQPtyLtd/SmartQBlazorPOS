// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Pos.Core.Domain;
using Pos.Infrastructure.Checkout;
using Pos.Infrastructure.Storage;
using Pos.Web;
using Pos.Web.Terminal;

namespace Pos.Web.Tests;

/// <summary>
/// Renders the terminal's pages in-process and hands back the HTML.
/// </summary>
/// <remarks>
/// <para>
/// Every screen in this app was, until now, verified by compiling it. That is a weak check for a
/// Razor page: markup that dereferences a null, indexes a sequence it assumed was populated, binds a
/// value that cannot round-trip, or recurses through a property that calls a service will compile
/// cleanly and throw the first time a shop opens it. The pages that had been looked at in a browser
/// were looked at once, by hand, and three of them never were.
/// </para>
/// <para>
/// This uses <see cref="HtmlRenderer"/>, which is the framework's own server-side renderer and needs
/// no browser and no extra package. It runs the real component lifecycle — injection, initialisation,
/// and the asynchronous work those kick off — against the terminal's <b>real</b> service
/// registration, with only the browser-backed stores swapped for in-memory ones so a test can seed
/// what it wants to see.
/// </para>
/// <para>
/// A page is rendered <b>populated</b> wherever possible. An empty screen exercises the empty state
/// and nothing else; almost every null dereference lives in a loop over rows.
/// </para>
/// </remarks>
internal sealed class PageHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;
    private readonly HtmlRenderer _renderer;

    private PageHarness(ServiceProvider provider, AsyncServiceScope scope, InMemoryLocalStore store, CheckoutSession session)
    {
        _provider = provider;
        _scope = scope;
        Store = store;
        Session = session;

        // Rendered against a scope rather than the root, which is what the browser does: a Blazor
        // WebAssembly app has a single scope for the life of the page, so its "scoped" services —
        // the checkout session, the terminal session, the drawer — are shared by every screen.
        _renderer = new HtmlRenderer(scope.ServiceProvider, NullLoggerFactory.Instance);
    }

    /// <summary>The local store behind the pages, for seeding and asserting.</summary>
    public InMemoryLocalStore Store { get; }

    /// <summary>The terminal session, so a test can sign someone in.</summary>
    public CheckoutSession Session { get; }

    /// <summary>Where the terminal's store ends up, once startup has run.</summary>
    public Store TerminalStore => Session.Store!;

    /// <summary>
    /// Builds a started terminal with a seeded catalogue, roster, and trading day.
    /// </summary>
    /// <param name="seed">Extra state to set up before the pages are rendered.</param>
    public static async Task<PageHarness> CreateAsync(Func<PageHarness, Task>? seed = null)
    {
        var browser = new Dictionary<string, string?>();
        var store = new InMemoryLocalStore();
        var roster = new InMemoryShiftStore(store);

        var services = new ServiceCollection();

        TerminalSeed.Compose(services, store, roster, browser, new FakeNavigationManager());

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        var scope = provider.CreateAsyncScope();
        var scoped = scope.ServiceProvider;

        var session = scoped.GetRequiredService<CheckoutSession>();

        var harness = new PageHarness(provider, scope, store, session);

        await TerminalSeed.StartAsync(scoped);

        if (seed is not null)
        {
            await seed(harness);
        }

        return harness;
    }

    /// <summary>
    /// Renders a page and returns its markup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole render — including producing the HTML — has to happen on the renderer's dispatcher.
    /// The component tree settles during the await, and both the state updates along the way and the
    /// final serialisation assert they are on the right thread; doing either from the test's thread
    /// throws rather than producing markup.
    /// </para>
    /// <para>
    /// Because the task completes only once asynchronous initialisation has finished, a page that
    /// throws in <c>OnInitializedAsync</c> fails here rather than yielding a plausible-looking
    /// fragment.
    /// </para>
    /// </remarks>
    public Task<string> RenderAsync<TComponent>(params (string Name, object? Value)[] parameters)
        where TComponent : IComponent
    {
        var view = ParameterView.FromDictionary(
            parameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal));

        return _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await _renderer.RenderComponentAsync<TComponent>(view);

            return root.ToHtmlString();
        });
    }

    /// <summary>Resolves a service, for a test that needs to arrange or assert directly.</summary>
    public T GetRequiredService<T>() where T : notnull =>
        _scope.ServiceProvider.GetRequiredService<T>();

    /// <summary>
    /// Signs an operator in and opens a shift.
    /// </summary>
    /// <remarks>
    /// Several pages behave differently depending on whether anyone is signed in — the transfers
    /// screen refuses to move stock, the drawer screen has nothing to count — so a test that wants
    /// the populated path goes through the same door a shop does, PIN included.
    /// </remarks>
    public Task SignInAsync(EmployeePermissions permissions = EmployeePermissions.Manager) =>
        TerminalSeed.SignInAsync(_scope.ServiceProvider, Session, TerminalStore, permissions);

    public async ValueTask DisposeAsync()
    {
        await _renderer.DisposeAsync();
        await _scope.DisposeAsync();
        await _provider.DisposeAsync();
    }
}
