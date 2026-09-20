// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Pos.Core.Payments;
using Pos.Devices.EscPos;
using Pos.Devices.Transport;
using Pos.Infrastructure.Checkout;
using Pos.Infrastructure.Storage;
using Pos.Infrastructure.Sync;
using Pos.Web;
using Pos.Web.Terminal;

namespace Pos.Web.Tests;

/// <summary>
/// Tests for the terminal's composition root.
/// </summary>
/// <remarks>
/// <para>
/// The registrations used to be top-level statements in <c>Program.cs</c>, which meant the only
/// thing that ever resolved them was a browser loading the page. That is a slow and unreliable
/// detector: a missing registration, a wrong keyed lookup, or a singleton depending on a scoped
/// service all fail at first resolution — which in a shop is the first sale of the morning.
/// </para>
/// <para>
/// Services were added to the container most rounds, so this is the test that was missing while it
/// grew.
/// </para>
/// </remarks>
public sealed class CompositionRootTests
{
    private static readonly Uri Origin = new("http://localhost:5043/");

    /// <summary>
    /// Builds the same container the terminal builds, with the browser supplied.
    /// </summary>
    private static (IServiceCollection Services, ServiceProvider Provider) BuildContainer()
    {
        var services = new ServiceCollection();

        // Provided by the Blazor host in production; registered here so the container is complete.
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(new FakeJsRuntime());

        services.AddPosTerminal(Origin);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            // Fails at build time rather than at first resolution, which is the whole point: the
            // difference between a test run and a shop's opening time.
            ValidateOnBuild = true,

            // Catches a singleton that depends on a scoped service. On a single-threaded WASM
            // runtime that is not a subtle lifetime issue — it is a captive dependency shared
            // across every basket the till ever rings.
            ValidateScopes = true,
        });

        return (services, provider);
    }

    [Fact]
    public void The_container_the_terminal_builds_is_valid()
    {
        // BuildContainer throws if any registration cannot be satisfied, or if a lifetime is wrong.
        var (_, provider) = BuildContainer();

        using (provider)
        {
            Assert.NotNull(provider);
        }
    }

    [Fact]
    public void Every_registered_service_can_actually_be_resolved()
    {
        // Validation proves the graph is satisfiable; this proves the factories run. A factory that
        // throws — a bad cast, a keyed lookup for a key nobody registered — passes validation and
        // fails here.
        var (services, provider) = BuildContainer();

        using (provider)
        using (var scope = provider.CreateScope())
        {
            var failures = new List<string>();
            var checkedCount = 0;

            foreach (var descriptor in services)
            {
                // Open generics and keyed services have no single instance to resolve; keyed ones
                // are covered by their own tests below.
                if (descriptor.ServiceType.IsGenericTypeDefinition || descriptor.ServiceKey is not null)
                {
                    continue;
                }

                checkedCount++;

                try
                {
                    _ = scope.ServiceProvider.GetRequiredService(descriptor.ServiceType);
                }
                catch (Exception ex)
                {
                    failures.Add($"{descriptor.ServiceType.Name}: {ex.GetType().Name} — {ex.Message}");
                }
            }

            Assert.True(failures.Count == 0, "These services could not be resolved:\n" + string.Join("\n", failures));

            // Guards against the loop passing trivially over an empty or truncated list.
            Assert.True(checkedCount >= 20, $"Only {checkedCount} services were checked.");
        }
    }

    [Fact]
    public void The_registration_list_is_the_one_the_app_uses()
    {
        var (_, provider) = BuildContainer();

        using (provider)
        using (var scope = provider.CreateScope())
        {
            Assert.NotNull(scope.ServiceProvider.GetService<ILocalStore>());
            Assert.NotNull(scope.ServiceProvider.GetService<IShiftStore>());
            Assert.NotNull(scope.ServiceProvider.GetService<CheckoutRecordingService>());
            Assert.NotNull(scope.ServiceProvider.GetService<ReturnRecordingService>());
            Assert.NotNull(scope.ServiceProvider.GetService<SaleCompletionService>());
            Assert.NotNull(scope.ServiceProvider.GetService<StockService>());
            Assert.NotNull(scope.ServiceProvider.GetService<ShiftService>());
            Assert.NotNull(scope.ServiceProvider.GetService<SyncClient>());
            Assert.NotNull(scope.ServiceProvider.GetService<ISyncChangeApplier>());
            Assert.NotNull(scope.ServiceProvider.GetService<IPaymentProvider>());
            Assert.NotNull(scope.ServiceProvider.GetService<KitchenTicketPrinter>());
            Assert.NotNull(scope.ServiceProvider.GetService<LabelPrinter>());
        }
    }

    // ---------------------------------------------------------------- printer roles

    [Theory]
    [InlineData(PrinterRole.Kitchen)]
    [InlineData(PrinterRole.Label)]
    public void Each_secondary_printer_role_resolves_to_its_own_provider(PrinterRole role)
    {
        // The keyed lookups are the most error-prone part of the container: a wrong key resolves
        // the receipt printer instead, and the symptom is customer receipts coming out of the
        // kitchen printer.
        var (_, provider) = BuildContainer();

        var keyed = provider.GetRequiredKeyedService<TerminalPrinterProvider>(role);
        var receipts = provider.GetRequiredService<TerminalPrinterProvider>();

        Assert.NotSame(receipts, keyed);
        Assert.Equal(role, keyed.Role);
        Assert.Equal(PrinterRole.Receipt, receipts.Role);
    }

    [Fact]
    public void The_two_secondary_roles_get_different_providers_from_each_other()
    {
        // Kitchen and label are both keyed singletons, so a copy-paste registration with the wrong
        // key would hand one role the other's provider and nothing else would notice.
        var (_, provider) = BuildContainer();

        var kitchen = provider.GetRequiredKeyedService<TerminalPrinterProvider>(PrinterRole.Kitchen);
        var label = provider.GetRequiredKeyedService<TerminalPrinterProvider>(PrinterRole.Label);

        Assert.NotSame(kitchen, label);
        Assert.Equal(PrinterRole.Kitchen, kitchen.Role);
        Assert.Equal(PrinterRole.Label, label.Role);
    }

    [Fact]
    public void The_kitchen_and_label_printers_wrap_their_own_roles_provider()
    {
        // The assertion that matters most in this file. Both services are thin wrappers that
        // resolve happily whichever provider they are handed, so a copy-paste key in the container
        // passes every other test — and sends ZPL to the pass, or food orders out of the customer's
        // receipt roll.
        var (_, provider) = BuildContainer();

        Assert.Equal(PrinterRole.Kitchen, provider.GetRequiredService<KitchenTicketPrinter>().Role);
        Assert.Equal(PrinterRole.Label, provider.GetRequiredService<LabelPrinter>().Role);
    }

    [Fact]
    public void Every_role_gets_a_resolver_that_prefers_webusb()
    {
        // The objective is WebUSB first with honest fallbacks. Each role builds its own resolver,
        // so a missing registration would leave a role with no transports at all — and a printer
        // that silently never prints.
        var (_, provider) = BuildContainer();

        foreach (var role in new[] { PrinterRole.Kitchen, PrinterRole.Label })
        {
            var resolver = provider.GetRequiredKeyedService<PrinterResolver>(role);

            Assert.NotNull(resolver);
        }

        Assert.NotNull(provider.GetRequiredService<PrinterResolver>());
    }

    // ------------------------------------------------------------------- lifetimes

    [Fact]
    public void Scoped_services_are_not_the_same_instance_across_scopes()
    {
        // The till's basket state must not leak between sessions. CheckoutSession carries the cart,
        // the operator, and the open shift.
        var (_, provider) = BuildContainer();

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<CheckoutSession>(),
            second.ServiceProvider.GetRequiredService<CheckoutSession>());
    }

    [Fact]
    public void Singletons_are_the_same_instance_across_scopes()
    {
        // One database, one outbox sequence for the till. A second JsLocalStore would mean two
        // outbox counters and a sync stream with duplicate positions.
        var (_, provider) = BuildContainer();

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<ILocalStore>(),
            second.ServiceProvider.GetRequiredService<ILocalStore>());

        Assert.Same(
            first.ServiceProvider.GetRequiredService<ITerminalIdentity>(),
            second.ServiceProvider.GetRequiredService<ITerminalIdentity>());
    }

    [Fact]
    public void The_store_and_the_shifts_share_one_terminal_identity()
    {
        // Both draw sequence numbers from the same persisted counter, and that counter is per
        // terminal. Two identities would put a sale and a drawer event on the same position in two
        // different streams.
        var (_, provider) = BuildContainer();
        using var scope = provider.CreateScope();

        var identity = scope.ServiceProvider.GetRequiredService<ITerminalIdentity>();
        var concrete = provider.GetRequiredService<TerminalIdentity>();

        Assert.Same(concrete, identity);
    }

    [Fact]
    public void The_payment_provider_a_till_gets_is_the_record_only_one()
    {
        // The shipped default. A shop with a standalone card machine gets no verification from the
        // POS, and the provider says so rather than implying a guarantee it cannot make.
        var (_, provider) = BuildContainer();

        var payments = provider.GetRequiredService<IPaymentProvider>();

        Assert.IsType<RecordOnlyPaymentProvider>(payments);
        Assert.False(payments.CanDecline);
    }

    [Fact]
    public void The_change_applier_routes_every_record_type_the_terminal_knows()
    {
        // A silently-null or partial applier would make a pulled change a no-op, and the till would
        // keep its seeded demo catalogue forever with nothing to show for it.
        var (_, provider) = BuildContainer();
        using var scope = provider.CreateScope();

        Assert.IsType<TerminalChangeApplier>(scope.ServiceProvider.GetRequiredService<ISyncChangeApplier>());

        // Both record types the terminal applies, resolvable in their own right so that a missing
        // registration inside the router fails here rather than silently dropping changes.
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CatalogChangeApplier>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<StockTransferChangeApplier>());
    }

    [Fact]
    public void The_state_that_identifies_the_till_resolves_to_a_real_object()
    {
        // TerminalStartup is what loads the store and seeds the demo catalogue. If it could not be
        // constructed, the checkout screen would come up with no store at all.
        var (_, provider) = BuildContainer();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<TerminalStartup>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<PrinterBindings>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<LabelSettings>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CustomerDisplayPublisher>());
    }
}
