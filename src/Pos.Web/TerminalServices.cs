// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Microsoft.Extensions.DependencyInjection;
using Pos.Core.Domain;
using Pos.Core.Payments;
using Pos.Devices.EscPos;
using Pos.Devices.Transport;
using Pos.Infrastructure.Checkout;
using Pos.Infrastructure.Storage;
using Pos.Infrastructure.Sync;
using Pos.Web.Terminal;

namespace Pos.Web;

/// <summary>
/// Wires the terminal's services together.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from the host's startup so the composition root can be exercised by a test. It was
/// previously top-level statements in <c>Program.cs</c>, which meant the only thing that ever
/// checked it was a browser loading the page — and a mistake there is not a wrong number on a
/// report, it is a till that does not start.
/// </para>
/// <para>
/// The risk is real rather than theoretical: services were added here most rounds, and nothing
/// resolved them until someone opened the app. A missing registration, a wrong keyed lookup, or a
/// singleton that depends on a scoped service all fail at first resolution — which in production is
/// the shop's first sale of the day.
/// </para>
/// <para>
/// Nothing here blocks. Blazor WebAssembly runs on a single thread, so a synchronous wait on async
/// work deadlocks the runtime outright; every factory that needs asynchronous data does its reading
/// inside an already-asynchronous factory or a component's startup path.
/// </para>
/// </remarks>
public static class TerminalServices
{
    /// <summary>
    /// Registers everything the terminal needs.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="baseAddress">
    /// Origin the terminal was served from, used as the sync base until the terminal is enrolled
    /// and knows its hub.
    /// </param>
    /// <param name="hubHandler">
    /// Transport for the terminal's calls to the hub. Null in production, where the browser's own
    /// fetch is the transport. A test supplies one so that a screen which reads the estate's branch
    /// list, or syncs, can be exercised without a network.
    /// </param>
    public static IServiceCollection AddPosTerminal(
        this IServiceCollection services,
        Uri baseAddress,
        HttpMessageHandler? hubHandler = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(baseAddress);

        // --- Terminal identity and local storage ---------------------------------------
        //
        // The terminal is the system of record while trading, so local storage is a singleton:
        // one database, one connection, one outbox sequence for this till.
        services.AddSingleton<TerminalIdentity>();
        services.AddSingleton<ITerminalIdentity>(sp => sp.GetRequiredService<TerminalIdentity>());
        services.AddSingleton<ILocalStore, JsLocalStore>();

        // Employees, shifts, and drawer events. A separate store from the sales path, because a
        // terminal whose roster has not synced can still sell — it simply cannot attribute the sale
        // to anyone, which is a degraded till rather than a stopped one.
        services.AddSingleton<IShiftStore, JsShiftStore>();

        // --- Per-session checkout state -------------------------------------------------
        services.AddScoped<CheckoutSession>();
        services.AddScoped<OperatorSignInMarker>();
        services.AddScoped<TerminalStartup>();
        services.AddSingleton<CustomerDisplayPublisher>();

        // --- Printing ------------------------------------------------------------------
        //
        // The bridge owns the browser's device APIs; the resolver picks the best transport this
        // browser can actually support, preferring WebUSB and degrading honestly elsewhere.
        services.AddSingleton<IDeviceBridge, JsDeviceBridge>();
        services.AddSingleton(_ => new ReceiptRenderer(new ReceiptOptions
        {
            PrintReturnQrCode = true,
            QrModuleSize = 6,
        }));

        services.AddSingleton<PrinterBindings>();

        // One resolver per printer role, because the roles differ in ways that matter:
        //
        //   * A second printer is bound to the device paired for it, so two roles cannot fight over
        //     one USB device. An unbound transport takes the first device it can open, which is how
        //     a kitchen ticket ends up on the customer-facing roll.
        //
        //   * Only the receipt role degrades. A receipt must always be printable — a sale the
        //     customer cannot prove is not a completed sale — so it falls back as far as the
        //     browser's print dialog. The other two send raw control languages a print dialog
        //     cannot carry, and their fallback would be actively harmful: a print dialog opening on
        //     the till screen mid-sale blocks the cashier to produce a document nobody asked for.
        services.AddSingleton(sp => BuildResolver(
            sp.GetRequiredService<IDeviceBridge>(), PrinterRole.Receipt, sp.GetRequiredService<PrinterBindings>()));

        services.AddKeyedSingleton(PrinterRole.Kitchen, (sp, _) => BuildResolver(
            sp.GetRequiredService<IDeviceBridge>(), PrinterRole.Kitchen, sp.GetRequiredService<PrinterBindings>()));

        services.AddKeyedSingleton(PrinterRole.Label, (sp, _) => BuildResolver(
            sp.GetRequiredService<IDeviceBridge>(), PrinterRole.Label, sp.GetRequiredService<PrinterBindings>()));

        // A printer is resolved asynchronously by the terminal on first use, never while the
        // container is being built. See the remarks above: blocking here deadlocks the runtime.
        services.AddSingleton(sp => new TerminalPrinterProvider(
            sp.GetRequiredService<PrinterResolver>(),
            sp.GetRequiredService<ReceiptRenderer>(),
            sp.GetRequiredService<ILogger<TerminalPrinterProvider>>(),
            PrinterRole.Receipt));

        services.AddKeyedSingleton(PrinterRole.Kitchen, (sp, _) => new TerminalPrinterProvider(
            sp.GetRequiredKeyedService<PrinterResolver>(PrinterRole.Kitchen),
            sp.GetRequiredService<ReceiptRenderer>(),
            sp.GetRequiredService<ILogger<TerminalPrinterProvider>>(),
            PrinterRole.Kitchen));

        services.AddKeyedSingleton(PrinterRole.Label, (sp, _) => new TerminalPrinterProvider(
            sp.GetRequiredKeyedService<PrinterResolver>(PrinterRole.Label),
            sp.GetRequiredService<ReceiptRenderer>(),
            sp.GetRequiredService<ILogger<TerminalPrinterProvider>>(),
            PrinterRole.Label));

        // Routes a completed sale to each station that has work. A distinct type from the receipt
        // provider, so a component cannot be injected with the wrong one by accident.
        services.AddSingleton(sp => new KitchenTicketPrinter(
            sp.GetRequiredKeyedService<TerminalPrinterProvider>(PrinterRole.Kitchen),
            sp.GetRequiredService<ILogger<KitchenTicketPrinter>>()));

        // Sends ZPL to the label printer. Its own service rather than a mode on the receipt
        // provider, because the two speak completely different command languages.
        services.AddSingleton<LabelSettings>();
        services.AddSingleton(sp => new LabelPrinter(
            sp.GetRequiredKeyedService<TerminalPrinterProvider>(PrinterRole.Label),
            sp.GetRequiredService<LabelSettings>(),
            sp.GetRequiredService<ILogger<LabelPrinter>>()));

        // --- Sync ----------------------------------------------------------------------
        services.AddSingleton<SyncAuthHandler>();

        // A stream carries several record types and each belongs to a different part of the replica,
        // so they are applied by separate classes and routed by entity type. One applier with a
        // growing switch over record types would put the catalogue's rules and the transfer's rules
        // in the same place.
        services.AddSingleton<CatalogChangeApplier>();
        services.AddSingleton<StockTransferChangeApplier>();
        services.AddSingleton<ISyncChangeApplier>(sp => new TerminalChangeApplier(
        [
            sp.GetRequiredService<CatalogChangeApplier>(),
            sp.GetRequiredService<StockTransferChangeApplier>(),
        ]));

        // The transport is built from the terminal's enrolled endpoint, which is only known once
        // identity has loaded. Resolving it lazily means an unenrolled terminal can still run and
        // sell; it simply has nothing to sync until it is enrolled.
        services.AddSingleton(sp =>
        {
            var identity = sp.GetRequiredService<TerminalIdentity>();
            var endpoint = identity.ToEndpoint();

            var http = new HttpClient(new SyncAuthHandler(identity)
            {
                // A test's transport, or the browser's fetch. The credential is attached by the
                // handler either way, so a test exercises the same authenticated path a shop does.
                InnerHandler = hubHandler ?? new HttpClientHandler(),
            })
            {
                BaseAddress = endpoint?.BaseAddress ?? baseAddress,
                Timeout = TimeSpan.FromSeconds(30),
            };

            // One client, one credential. The transport and the store directory must not drift
            // apart in how they authenticate, or a transfer would be addressed with a store id
            // that the hub never issued.
            return new SyncConnection(
                http,
                endpoint ?? new SyncEndpoint(http.BaseAddress!, string.Empty, string.Empty));
        });

        services.AddSingleton<ISyncTransport>(sp =>
        {
            var connection = sp.GetRequiredService<SyncConnection>();
            return new HttpSyncTransport(connection.Http, connection.Endpoint);
        });

        // Rotating the device credential, and the preflight that does it when it falls due. The
        // preflight sits before every sync pass rather than behind a button, so a till left running
        // for months replaces its credential without anybody remembering to ask it to.
        services.AddSingleton(sp =>
        {
            var connection = sp.GetRequiredService<SyncConnection>();
            return new DeviceRotationClient(connection.Http, connection.Endpoint);
        });

        services.AddSingleton<ISyncPreflight>(sp => new CredentialRotationPreflight(
            sp.GetRequiredService<TerminalIdentity>(),
            sp.GetRequiredService<DeviceRotationClient>(),
            sp.GetRequiredService<ILogger<CredentialRotationPreflight>>()));

        // The branch list a till may see. Device-authenticated and deliberately narrow, so a
        // shop can address a transfer without ever holding the head-office token.
        services.AddScoped(sp =>
        {
            var connection = sp.GetRequiredService<SyncConnection>();
            return new StoreDirectoryClient(connection.Http, connection.Endpoint);
        });

        services.AddScoped(sp => new SyncClient(
            sp.GetRequiredService<ILocalStore>(),
            sp.GetRequiredService<ISyncTransport>(),
            sp.GetRequiredService<ISyncChangeApplier>(),
            sp.GetRequiredService<ISyncPreflight>()));

        // --- Checkout ------------------------------------------------------------------
        services.AddScoped<ISaleNumberSource>(sp =>
            new LocalSaleNumberSource(sp.GetRequiredService<ILocalStore>()));

        services.AddScoped(sp => new CheckoutRecordingService(
            sp.GetRequiredService<ILocalStore>(),
            sp.GetRequiredService<ISaleNumberSource>(),
            sp.GetRequiredService<ITerminalIdentity>()));

        // Refunds are a separate service from checkout because they carry different risks, so the
        // call site always makes it obvious which operation is happening.
        services.AddScoped(sp => new ReturnRecordingService(
            sp.GetRequiredService<ILocalStore>(),
            sp.GetRequiredService<ITerminalIdentity>()));

        // --- Payments ------------------------------------------------------------------
        //
        // The till never learns which kind of payment it is taking. Today the shop has a standalone
        // card machine and the operator tells the POS the card went through; tomorrow the POS may
        // drive an integrated terminal. Checkout code that called either one directly would have to
        // change on the day the hardware did.
        //
        // Record-only is the honest default for a shop with a standalone terminal: it approves
        // everything because it has nothing to check, and CanDecline says so rather than implying a
        // verification that never happened.
        services.AddSingleton<IPaymentProvider, RecordOnlyPaymentProvider>();

        // Pays first, records second. A sale written against a payment that later declined is a
        // sale the shop cannot collect on, and the customer is already walking out with the goods.
        services.AddScoped(sp => new SaleCompletionService(
            sp.GetRequiredService<CheckoutRecordingService>(),
            sp.GetRequiredService<IPaymentProvider>()));

        // Catalogue and stock. Stock changes are appended to the ledger, never written as a level.
        services.AddScoped(sp => new StockService(
            sp.GetRequiredService<ILocalStore>(),
            sp.GetRequiredService<ITerminalIdentity>()));

        // Shifts and cash accountability. Drawer events draw their sequence from the same persisted
        // counter as sales, so a sale and a drawer opening can never claim one position.
        services.AddScoped(sp => new ShiftService(
            sp.GetRequiredService<IShiftStore>(),
            sp.GetRequiredService<ILocalStore>(),
            sp.GetRequiredService<ITerminalIdentity>()));

        // Inter-store stock transfers. Both events reserve a terminal sequence block sized to the
        // transfer's lines, so a dispatch and the movements it implies stay consecutive.
        services.AddScoped(sp => new StockTransferService(
            sp.GetRequiredService<ILocalStore>(),
            sp.GetRequiredService<IShiftStore>(),
            sp.GetRequiredService<ITerminalIdentity>()));

        return services;
    }

    private static PrinterResolver BuildResolver(
        IDeviceBridge bridge,
        PrinterRole role,
        PrinterBindings bindings) =>
        new PrinterResolver()
            // WebUSB first: direct ESC/POS with full capability on Chromium and Edge.
            .Add(1, "WebUSB", async ct => new WebUsbTransport(
                bridge, binding: await BindingForAsync(role, bindings, ct).ConfigureAwait(false)))

            // Web Serial next, and it is not merely a lesser WebUSB: on Windows a printer is often
            // held by the system usbprint driver, which blocks claiming the USB interface while
            // leaving the serial port free. This transport is frequently the one that actually
            // works on real hardware.
            .Add(2, "Web Serial", async ct => new WebSerialTransport(
                bridge,
                baudRate: WebSerialTransport.DefaultBaudRate,
                binding: await BindingForAsync(role, bindings, ct).ConfigureAwait(false)))

            // Browser printing is the only transport that works in every browser, including Safari
            // and Firefox. It cannot cut or open a drawer, and the capability report says so, but
            // it means a sale can always be completed.
            .Add(3, "Browser print", _ => ValueTask.FromResult<IDeviceTransport?>(
                role == PrinterRole.Receipt ? new BrowserPrintTransport(bridge) : null))

            // The simulated printer is always ready, so a developer machine, a CI run, or a browser
            // with no device APIs still has a working till.
            .Add(100, "Simulated printer", _ => ValueTask.FromResult<IDeviceTransport?>(
                role == PrinterRole.Receipt ? new SimulatedTransport() : null));

    private static Task<PrinterBinding> BindingForAsync(
        PrinterRole role,
        PrinterBindings bindings,
        CancellationToken ct) =>
        // The receipt printer is deliberately unbound: it is the machine that must always work, so
        // it takes whatever device it can open rather than refusing because a specific one is
        // missing. The binding is read inside the factory, which is already asynchronous.
        role == PrinterRole.Receipt
            ? Task.FromResult(PrinterBinding.Any)
            : bindings.ForTransportAsync(role, ct);
}
