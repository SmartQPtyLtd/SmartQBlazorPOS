// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Pos.Core.Domain;
using Pos.Devices.Transport;
using Pos.Infrastructure.Checkout;
using Pos.Infrastructure.Storage;
using Pos.Infrastructure.Sync;
using Pos.Web;
using Pos.Web.Terminal;

namespace Pos.Web.Tests;

/// <summary>
/// Builds a terminal against the real composition root, with the browser swapped out.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the two ways a screen is exercised here: rendered to HTML, and driven through its
/// controls. Both need the same thing underneath — the production service graph, an in-memory
/// database a test can seed, and a started terminal — so it is built once and used by both.
/// </para>
/// <para>
/// Only <see cref="ILocalStore"/> and <see cref="IShiftStore"/> are replaced. Everything else,
/// including the sync client, the printers, checkout, and reporting, is what a shop runs.
/// </para>
/// </remarks>
internal static class TerminalSeed
{
    /// <summary>Base address the harness pretends the terminal is served from.</summary>
    public static readonly Uri Origin = new("http://localhost:5043/");

    /// <summary>The PIN every seeded operator uses.</summary>
    public const string Pin = "9152";

    /// <summary>
    /// Registers the terminal and the browser stand-ins on a service collection.
    /// </summary>
    /// <param name="services">Collection to populate.</param>
    /// <param name="store">In-memory database.</param>
    /// <param name="roster">In-memory drawer and roster.</param>
    /// <param name="browser">Local storage, shared so an enrolment survives a rebuild.</param>
    /// <param name="navigation">
    /// Navigation manager to register. Supplied by the caller because the two harnesses provide
    /// different ones, and a page that navigates must not reach for a real browser.
    /// </param>
    /// <param name="hub">
    /// Answers the terminal's calls to the hub. Defaults to one that behaves like an enrolled till's
    /// hub with a second branch, because a terminal that knows no other branch cannot raise a
    /// transfer at all, and that is the emptier path rather than the interesting one.
    /// </param>
    /// <param name="bridge">
    /// Stands in for the browser's device APIs. Left null the terminal gets the real
    /// <c>JsDeviceBridge</c>, which finds no printers and prints nothing — so a test that wants to
    /// inspect what reached a printer supplies one that records.
    /// </param>
    public static void Compose(
        IServiceCollection services,
        InMemoryLocalStore store,
        InMemoryShiftStore roster,
        Dictionary<string, string?> browser,
        NavigationManager navigation,
        HttpMessageHandler? hub = null,
        IDeviceBridge? bridge = null,
        FakeJsRuntime? js = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddLogging();
        services.AddSingleton<IJSRuntime>(js ?? new FakeJsRuntime(browser));
        services.AddSingleton(navigation);

        // The Blazor host supplies an HttpClient whose handler is the browser's fetch. Registered
        // here because the device settings screen injects one directly to reach the enrolment
        // endpoint, and a test must never depend on a network.
        var transport = hub ?? new StubHubHandler();

        services.AddSingleton(_ => new HttpClient(transport) { BaseAddress = Origin });

        services.AddPosTerminal(Origin, transport);

        services.AddSingleton<ILocalStore>(store);
        services.AddSingleton<IShiftStore>(roster);

        // After AddPosTerminal, so this registration wins. Everything that resolves a printer does so
        // through a factory and picks this up rather than the browser-backed bridge.
        if (bridge is not null)
        {
            services.AddSingleton(bridge);
        }
    }

    /// <summary>
    /// Pairs a printer with a role, the way an operator does in device settings.
    /// </summary>
    /// <remarks>
    /// Only the receipt printer is unbound by design — it takes whatever device it can open, because
    /// a sale that cannot print is a sale the customer cannot prove. The kitchen and label printers
    /// are paired to a named connection, and the binding is what stops one role from stealing
    /// another's device.
    /// </remarks>
    /// <param name="services">Terminal services.</param>
    /// <param name="role">Which printer is being paired.</param>
    /// <param name="connectionId">Connection the bridge reports for that device.</param>
    /// <param name="label">Friendly name, as the browser would report it.</param>
    public static Task PairPrinterAsync(
        IServiceProvider services,
        PrinterRole role,
        string connectionId,
        string label)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.GetRequiredService<PrinterBindings>().SaveAsync(
            role,
            new PrinterBindingRecord(connectionId, "WebUSB", label));
    }

    /// <summary>
    /// Enrols the terminal against the harness hub without starting it.
    /// </summary>
    /// <remarks>
    /// Enrolment is what gives the till a hub, and therefore a store directory. It is not a
    /// formality: an unenrolled terminal has no hub to ask, so it knows of no other branch and cannot
    /// raise a transfer at all. A test that skipped this would exercise the empty path and miss the
    /// one with the logic in it.
    /// </remarks>
    /// <param name="services">Terminal services.</param>
    /// <param name="storeId">Store id the hub issued, or null to mint one.</param>
    public static Task EnrolAsync(IServiceProvider services, string? storeId = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.GetRequiredService<TerminalIdentity>()
            .SaveEnrolmentAsync(
                storeId ?? Guid.CreateVersion7().ToString("N"),
                TestSecret,
                TestRefreshToken,

                // Not due, so an ordinary test never trips the rotation preflight by accident. A test
                // that is about rotation passes its own date.
                DateTimeOffset.UtcNow.Add(SyncCredentialPolicy.RotationInterval),
                Origin.ToString());
    }

    /// <summary>The secret a test terminal is enrolled with.</summary>
    public const string TestSecret = "test-device-secret";

    /// <summary>The refresh token a test terminal is enrolled with.</summary>
    public const string TestRefreshToken = "test-device-refresh-token";

    /// <summary>
    /// Runs the terminal's startup: loads the store, seeds the demo catalogue and roster, and
    /// restores any shift left open.
    /// </summary>
    /// <remarks>
    /// Running it is what makes a rendered page meaningful. Without it the session has no store, and
    /// every screen takes its "nothing is loaded" path — which is a real path, but not the one that
    /// hides null dereferences.
    /// </remarks>
    public static Task StartAsync(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.GetRequiredService<TerminalStartup>().InitialiseAsync();
    }

    /// <summary>
    /// Starts the terminal and enrols it against the harness hub.
    /// </summary>
    /// <remarks>
    /// The order matters and mirrors enrolment in the real world only loosely: a shop enrols a till
    /// once and then trades under the store id the hub issued it. Here the enrolment is written
    /// before startup so that startup builds the session around that same id, which is the state a
    /// working till is in.
    /// </remarks>
    public static async Task StartEnrolledAsync(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The hub's own id for the store, adopted as this terminal's store id.
        var storeId = services.GetRequiredService<TerminalIdentity>();

        await storeId.SaveEnrolmentAsync(
            Guid.CreateVersion7().ToString("N"),
            TestSecret,
            TestRefreshToken,
            DateTimeOffset.UtcNow.Add(SyncCredentialPolicy.RotationInterval),
            Origin.ToString());

        await StartAsync(services);
    }

    /// <summary>
    /// Signs an operator in and opens a shift, through the same door a shop uses.
    /// </summary>
    /// <param name="services">Terminal services.</param>
    /// <param name="session">Session to populate.</param>
    /// <param name="store">Store the operator belongs to.</param>
    /// <param name="permissions">What the operator is allowed to do.</param>
    /// <param name="name">Name, so a test can tell two operators apart on screen.</param>
    public static async Task<Employee> SignInAsync(
        IServiceProvider services,
        CheckoutSession session,
        Store store,
        EmployeePermissions permissions,
        string name = "Anele Botha")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(store);

        var id = EmployeeId.New();

        var employee = new Employee
        {
            Id = id,
            StoreId = store.Id,
            Name = name,
            Initials = "AB",
            PinHash = Pos.Infrastructure.Security.PinHasher.Hash(id, Pin),
            Permissions = permissions,
        };

        await services.GetRequiredService<IShiftStore>().UpsertEmployeesAsync(
        [
            new StoredEmployee
            {
                Id = employee.Id.ToString(),
                StoreId = store.Id.ToString(),
                Name = employee.Name,
                Initials = employee.Initials,
                PinHash = employee.PinHash,
                Permissions = employee.Permissions.ToString(),
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow.ToString(
                    "O", System.Globalization.CultureInfo.InvariantCulture),
            },
        ]);

        var signedIn = await services
            .GetRequiredService<ShiftService>()
            .SignInAsync(employee.Id.ToString(), Pin, openingFloat: 500m);

        session.Employee = signedIn.Employee;
        session.Shift = signedIn.Shift;

        return signedIn.Employee;
    }
}
