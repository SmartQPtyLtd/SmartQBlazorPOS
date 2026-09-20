// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Pos.Core.Domain;
using Pos.Devices.EscPos;
using Pos.Devices.Zpl;
using Pos.Devices.Transport;
using Pos.Infrastructure.Storage;
using Pos.Infrastructure.Sync;
using Pos.Web.Terminal;

namespace Pos.Web.Pages;

/// <summary>
/// Device settings: pair a printer, enrol the terminal, inspect the environment.
/// </summary>
/// <remarks>
/// This screen is where the transport and sync work becomes reachable by a person setting up
/// a till. The two things it exists to do are pairing a printer and enrolling against head
/// office; everything else is diagnostics for when those fail.
/// </remarks>
public sealed partial class DeviceSettings : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();

    [Inject]
    private PrinterBindings Bindings { get; set; } = default!;

    /// <summary>
    /// The kitchen printer, distinct from <c>Printers</c> so the two cannot be confused.
    /// </summary>
    [Inject]
    private KitchenTicketPrinter Kitchen { get; set; } = default!;

    /// <summary>
    /// The label printer, again its own type. It sends ZPL, which is a different command language
    /// from the ESC/POS everything else here speaks.
    /// </summary>
    [Inject]
    private LabelPrinter Labels { get; set; } = default!;

    [Inject]
    private LabelSettings LabelStockSettings { get; set; } = default!;

    private string? _message;
    private bool _messageIsError;
    private bool _busy;
    private bool _persisted;
    private int _pending;

    private string _hubAddress = string.Empty;
    private string _enrollmentCode = string.Empty;
    private string _terminalLabel = "Front counter";

    private BrowserCapabilities? _capabilities;

    private bool IsPrinterReady => Printers.Current?.Transport.State.IsReady ?? false;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            await Identity.InitialiseAsync(_cts.Token);
            await RefreshKitchenPrinterAsync();
            await RefreshLabelPrinterAsync();
            await RefreshDisplayChannelAsync();
            await LocalStore.InitialiseAsync(_cts.Token);
            await Printers.RequireAsync(_cts.Token);

            // Pre-fill the hub with the origin the till was served from, which is the right
            // answer whenever the hub hosts the PWA — the normal deployment.
            _hubAddress = Identity.HubAddress ?? GetCurrentOrigin();

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Fail($"Device settings could not be loaded: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------------ printer

    /// <summary>
    /// Whether this browser can carry the customer display channel at all.
    /// </summary>
    /// <remarks>
    /// Null while it has not been asked. Reported here because a display that renders but never
    /// updates looks exactly like a display with an empty basket, and an operator has no other way
    /// to tell the difference. The publisher has exposed this probe since it was written; nothing
    /// ever called it, so the answer was never shown to anyone.
    /// </remarks>
    private bool? _displayChannel;

    private async Task RefreshDisplayChannelAsync()
    {
        try
        {
            _displayChannel = await Display.IsSupportedAsync(_cts.Token);
        }
        catch (JSException)
        {
            // A probe that throws is a browser that cannot carry the channel, from the operator's
            // point of view — and the answer is shown on the screen, so there is nothing to log that
            // the person reading it does not already know.
            _displayChannel = false;
        }
    }

    /// <summary>
    /// Pairs a WebUSB printer.
    /// </summary>
    /// <remarks>
    /// Reached from a click, which is essential: the browser only permits
    /// <c>requestDevice</c> while a user gesture is still live.
    /// </remarks>
    private async Task PairPrinterAsync()
    {
        await RunAsync(async () =>
        {
            var printer = await Printers.RequireAsync(_cts.Token);

            if (printer.Transport is not WebUsbTransport usb)
            {
                Fail($"The active transport is {printer.Transport.DisplayName}, not WebUSB. " +
                     "Use the serial pairing button instead.");
                return;
            }

            if (await usb.RequestDeviceAsync(_cts.Token))
            {
                Printers.Reset();
                await Printers.RequireAsync(_cts.Token);
                Succeed($"Paired {usb.State.DeviceLabel ?? "printer"} over WebUSB.");
            }
            else
            {
                Fail(DescribePairingFailure(usb.State));
            }
        });
    }

    // ------------------------------------------------------------ kitchen printer

    /// <summary>What is paired for the kitchen role, for the operator to read.</summary>
    private string KitchenPrinterStatus { get; set; } = "Not paired";

    private bool IsKitchenPrinterReady => Kitchen.IsAvailable;

    /// <summary>
    /// Pairs the kitchen printer and remembers which device it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The binding is written here and nowhere else. It is what stops the kitchen printer from
    /// taking the receipt printer's device: both roles resolve at startup, and an unbound
    /// transport takes whichever authorised device it can open first.
    /// </para>
    /// <para>
    /// This is also why the receipt printer is re-resolved afterwards. The two roles share a pool
    /// of authorised devices, and reconnecting the kitchen one can leave the receipt transport
    /// holding a stale handle.
    /// </para>
    /// </remarks>
    private async Task PairKitchenPrinterAsync()
    {
        await RunAsync(async () =>
        {
            var bridge = new JsDeviceBridge(JS);

            // Deliberately unbound for the moment of pairing: the operator is choosing a device
            // rather than confirming a known one, and a binding to a device that no longer exists
            // would refuse the very chooser that is meant to replace it.
            await using var usb = new WebUsbTransport(bridge, binding: PrinterBinding.Any);

            if (!await usb.IsAvailableAsync(_cts.Token))
            {
                Fail("This browser does not support WebUSB. A kitchen printer needs a direct " +
                     "connection, because printing a ticket through the browser dialog would " +
                     "block the till mid-sale.");
                return;
            }

            if (!await usb.RequestDeviceAsync(_cts.Token))
            {
                Fail(DescribePairingFailure(usb.State));
                return;
            }

            if (usb.ConnectionId is not { Length: > 0 } connectionId)
            {
                Fail("The printer was selected but no connection could be established.");
                return;
            }

            await Bindings.SaveAsync(
                PrinterRole.Kitchen,
                new PrinterBindingRecord(connectionId, usb.TransportId, usb.State.DeviceLabel),
                _cts.Token);

            // A fresh resolver so the new binding takes effect, and the receipt printer re-resolved
            // so it cannot be left holding the device the kitchen just took.
            Kitchen.Reset();
            Printers.Reset();

            await RefreshKitchenPrinterAsync();

            Succeed($"Paired {usb.State.DeviceLabel ?? "printer"} as the kitchen printer.");
        });
    }

    /// <summary>Forgets the kitchen printer, so no tickets are sent.</summary>
    private async Task UnpairKitchenPrinterAsync()
    {
        await RunAsync(async () =>
        {
            await Bindings.ClearAsync(PrinterRole.Kitchen, _cts.Token);

            Kitchen.Reset();
            await RefreshKitchenPrinterAsync();

            Succeed("The kitchen printer has been unpaired. No preparation tickets will be sent.");
        });
    }

    private async Task RefreshKitchenPrinterAsync()
    {
        KitchenPrinterStatus = await Bindings.DescribeAsync(PrinterRole.Kitchen, _cts.Token);

        // Resolving is what actually attempts to reach the device, so the status shown reflects
        // whether it is reachable rather than merely recorded.
        var printer = await Kitchen.ResolveAsync(_cts.Token);

        if (printer is not null)
        {
            var state = printer.Transport.State;

            KitchenPrinterStatus = state.Status switch
            {
                DeviceStatus.Ready => $"{state.DeviceLabel ?? "Printer"} — ready",
                DeviceStatus.Faulted => $"{state.DeviceLabel ?? "Printer"} — {state.Detail}",
                _ => $"{KitchenPrinterStatus} — {state.Detail ?? state.Status.ToString()}",
            };
        }
    }

    // ---------------------------------------------------------------- label printer

    /// <summary>What is paired for the label role, for the operator to read.</summary>
    private string LabelPrinterStatus { get; set; } = "Not paired";

    private bool IsLabelPrinterReady => Labels.IsAvailable;

    private decimal _labelWidthMm = (decimal)LabelStock.Default.WidthMm;

    private decimal _labelHeightMm = (decimal)LabelStock.Default.HeightMm;

    private int _labelDpi = LabelStock.Default.Dpi;

    /// <summary>
    /// Pairs the label printer and remembers which device it is.
    /// </summary>
    /// <remarks>
    /// The same shape as the kitchen printer, and for the same reason: a Zebra label printer and a
    /// receipt printer both appear as USB printer-class devices, so an unbound transport would let
    /// them take each other's — and ZPL sent to a receipt printer comes out as a page of literal
    /// command text.
    /// </remarks>
    private async Task PairLabelPrinterAsync()
    {
        await RunAsync(async () =>
        {
            var bridge = new JsDeviceBridge(JS);

            // Unbound for the moment of pairing: the operator is choosing a device, and a binding
            // to one that no longer exists would refuse the very chooser meant to replace it.
            await using var usb = new WebUsbTransport(bridge, binding: PrinterBinding.Any);

            if (!await usb.IsAvailableAsync(_cts.Token))
            {
                Fail("This browser does not support WebUSB, which a label printer needs. A label " +
                     "is a ZPL document, and the browser print dialog cannot carry one.");
                return;
            }

            if (!await usb.RequestDeviceAsync(_cts.Token))
            {
                Fail(DescribePairingFailure(usb.State));
                return;
            }

            if (usb.ConnectionId is not { Length: > 0 } connectionId)
            {
                Fail("The printer was selected but no connection could be established.");
                return;
            }

            await Bindings.SaveAsync(
                PrinterRole.Label,
                new PrinterBindingRecord(connectionId, usb.TransportId, usb.State.DeviceLabel),
                _cts.Token);

            Labels.Reset();
            await RefreshLabelPrinterAsync();

            Succeed($"Paired {usb.State.DeviceLabel ?? "printer"} as the label printer.");
        });
    }

    private async Task UnpairLabelPrinterAsync()
    {
        await RunAsync(async () =>
        {
            await Bindings.ClearAsync(PrinterRole.Label, _cts.Token);

            Labels.Reset();
            await RefreshLabelPrinterAsync();

            Succeed("The label printer has been unpaired. No labels will be printed.");
        });
    }

    /// <summary>
    /// Stores the label media size.
    /// </summary>
    /// <remarks>
    /// Geometry is validated before it is saved rather than at print time. A mis-sized label does
    /// not fail: it prints, it is the wrong size, and nobody notices until it will not fit the
    /// product it was printed for.
    /// </remarks>
    private async Task SaveLabelStockAsync()
    {
        await RunAsync(async () =>
        {
            var stock = new LabelStock((double)_labelWidthMm, (double)_labelHeightMm, _labelDpi);

            await LabelStockSettings.SaveAsync(stock, _cts.Token);

            Succeed($"Label size saved: {stock.Describe()}.");
        });
    }

    private async Task RefreshLabelPrinterAsync()
    {
        LabelPrinterStatus = await Bindings.DescribeAsync(PrinterRole.Label, _cts.Token);

        var printer = await Labels.ResolveAsync(_cts.Token);

        if (printer is not null)
        {
            var state = printer.Transport.State;

            LabelPrinterStatus = state.Status switch
            {
                DeviceStatus.Ready => $"{state.DeviceLabel ?? "Printer"} — ready",
                DeviceStatus.Faulted => $"{state.DeviceLabel ?? "Printer"} — {state.Detail}",
                _ => $"{LabelPrinterStatus} — {state.Detail ?? state.Status.ToString()}",
            };
        }

        var stock = await Labels.StockAsync(_cts.Token);

        _labelWidthMm = (decimal)stock.WidthMm;
        _labelHeightMm = (decimal)stock.HeightMm;
        _labelDpi = stock.Dpi;
    }

    /// <summary>Prints a label so the operator can confirm the size and alignment.</summary>
    private async Task PrintTestLabelAsync()
    {
        await RunAsync(async () =>
        {
            var printed = await Labels.PrintShelfLabelAsync(
                "Test label",
                "6001000000017",
                15.00m,
                sku: "TEST",
                copies: 1,
                _cts.Token);

            if (printed)
            {
                Succeed("Test label sent. Check the size matches the roll.");
            }
            else
            {
                Fail("The label did not print. Check the printer is connected and paired.");
            }
        });
    }

    private async Task PairSerialAsync()
    {
        await RunAsync(async () =>
        {
            // A serial printer may not be the currently chosen transport, so it is built and
            // used directly rather than routed through the resolver.
            var bridge = new JsDeviceBridge(JS);
            await using var serial = new WebSerialTransport(bridge);

            if (!await serial.IsAvailableAsync(_cts.Token))
            {
                Fail("This browser does not support Web Serial.");
                return;
            }
            if (await serial.RequestDeviceAsync(_cts.Token))
            {
                Succeed($"Paired {serial.State.DeviceLabel ?? "serial printer"}. " +
                        "Reload the till to use it.");
            }
            else
            {
                Fail(DescribePairingFailure(serial.State));
            }
        });
    }

    private async Task ReconnectAsync()
    {
        await RunAsync(async () =>
        {
            Printers.Reset();
            var printer = await Printers.RequireAsync(_cts.Token);

            await printer.Transport.TryReconnectAsync(_cts.Token);

            if (printer.Transport.State.IsReady)
            {
                Succeed($"Reconnected to {printer.Transport.State.DeviceLabel ?? printer.Transport.DisplayName}.");
            }
            else
            {
                Fail(printer.Transport.State.Detail ?? "No paired printer responded.");
            }
        });
    }

    private async Task TryPrintTestAsync()
    {
        await RunAsync(async () =>
        {
            var printer = await Printers.RequireAsync(_cts.Token);

            var test = BuildTestDocument();
            await printer.PrintReceiptAsync(test, openDrawer: false, _cts.Token);

            Succeed($"A test receipt was sent to {printer.Transport.DisplayName}.");
        });
    }

    /// <summary>
    /// Builds a minimal test sale for the printer check.
    /// </summary>
    /// <remarks>
    /// Exercises the full path — render, transfer, cut — rather than sending a bare string, so
    /// a successful test actually means receipts will work.
    /// </remarks>
    private ReceiptDocument BuildTestDocument()
    {
        var store = new Store
        {
            Id = StoreId.New(),
            Name = "PRINTER TEST",
            Code = "TEST",
            Currency = "ZAR",
            TaxMode = TaxMode.Inclusive,
            DefaultTaxRate = new TaxRate("VAT", 0.15m),
            AddressLines = ["Printer self-test"],
            ReceiptFooter = "If you can read this, the printer works.",
            ReceiptColumns = Printers.Current?.Transport.Capabilities.Columns ?? 48,
        };

        var cart = new Cart(store.Id, store.Currency, store.TaxMode);
        cart.Add(ProductId.New(), "TEST-1", "Printer test item", new TaxRate("VAT", 0.15m),
            new Money(1.00m, store.Currency));

        var sale = new Sale
        {
            Id = SaleId.New(),
            StoreId = store.Id,
            Number = new SaleNumber("TEST", DateOnly.FromDateTime(DateTime.Now), 1),
            CompletedAt = DateTimeOffset.Now,
            BusinessDate = DateOnly.FromDateTime(DateTime.Now),
            Currency = store.Currency,
            TaxMode = store.TaxMode,
            Lines =
            [
                new SaleLine(
                    ProductId.New(), "TEST-1", "Printer test item", 1m,
                    new Money(1.00m, store.Currency), new TaxRate("VAT", 0.15m),
                    0m, 1.00m, 0.13m),
            ],
            Tenders = [new Tender(TenderType.Cash, new Money(1.00m, store.Currency))],
            Tax = new TaxCalculation(TaxMode.Inclusive, 0.87m, 0.13m, 1.00m, []),
            Subtotal = 1.00m,
            TotalDiscount = 0m,
            Total = 1.00m,
        };

        return new ReceiptDocument(store, sale, PosDocumentType.SalesReceiptCopy, CopyIndex: 1);
    }

    // --------------------------------------------------------------------------- sync

    private async Task SyncNowAsync()
    {
        await RunAsync(async () =>
        {
            var result = await Sync.RunOnceAsync(_cts.Token);

            if (result.Error is { } error)
            {
                Fail($"Could not reach the hub ({error}). Sales stay queued and safe.");
            }
            else
            {
                Succeed($"Sent {result.Pushed}, received {result.Applied}, {result.Remaining} still queued.");
            }

            await RefreshAsync();
        });
    }

    private async Task EnrollAsync()
    {
        await RunAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(_hubAddress) || string.IsNullOrWhiteSpace(_enrollmentCode))
            {
                Fail("A hub address and an enrolment code are both required.");
                return;
            }

            if (!Uri.TryCreate(_hubAddress, UriKind.Absolute, out var hub))
            {
                Fail("The hub address is not a valid absolute URL.");
                return;
            }

            // The credential is generated here and never leaves the device except as this one
            // request. The hub stores only hashes. The refresh token is generated now too rather
            // than being issued by the hub, because a rotation has to be retryable: the till
            // already holds the pair it is asking the hub to adopt.
            var credential = DeviceCredentialGenerator.NewPair();

            var request = new
            {
                code = _enrollmentCode.Trim().ToUpperInvariant(),
                label = string.IsNullOrWhiteSpace(_terminalLabel) ? "Terminal" : _terminalLabel,
                deviceSecret = credential.Secret,
                deviceRefreshToken = credential.RefreshToken,
            };

            using var response = await Http.PostAsJsonAsync(
                new Uri(hub, "api/enrollment/enroll"), request, _cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(_cts.Token);
                Fail($"Enrolment failed ({(int)response.StatusCode}). {Summarise(body)}");
                return;
            }

            var result = await response.Content
                .ReadFromJsonAsync<EnrollResponse>(cancellationToken: _cts.Token);

            if (result is null)
            {
                Fail("The hub returned an empty enrolment response.");
                return;
            }

            var movedStore = await Identity.SaveEnrolmentAsync(
                result.StoreId,
                credential.Secret,
                credential.RefreshToken,
                result.RotateAfter,
                hub.ToString(),
                _cts.Token);

            _enrollmentCode = string.Empty;

            if (movedStore)
            {
                // Everything recorded before this moment is keyed to a store id these books are not.
                // Left in place it would be invisible to every report while still queued for push,
                // and the first sync would file one store's takings under another's name. The wipe
                // is done here, at the moment of the change, rather than at the next startup,
                // because "Sync now" is one click away and would drain the old queue in between.
                await LocalStore.WipeAsync(_cts.Token);

                // A forced load, not client-side navigation. The running session still holds the
                // previous store id, so a sale rung up now would be recorded locally under one store
                // and pushed to the hub under another. Only reloading the app rebuilds the session
                // around the store this terminal now belongs to.
                Nav.NavigateTo("/", forceLoad: true);
                return;
            }

            Succeed($"Enrolled as {result.DeviceId[..Math.Min(8, result.DeviceId.Length)]} for store {result.StoreCode}.");
        });
    }

    private async Task WipeAsync()
    {
        await RunAsync(async () =>
        {
            await LocalStore.WipeAsync(_cts.Token);
            await RefreshAsync();
            Succeed("Local data erased. Queued records are gone with it.");
        });
    }

    // -------------------------------------------------------------------- diagnostics

    private async Task RefreshAsync()
    {
        var summary = await LocalStore.GetOutboxSummaryAsync(_cts.Token);
        _pending = summary.Pending + summary.Dead;
        _persisted = await LocalStore.IsPersistedAsync(_cts.Token);

        try
        {
            var module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/device-bridge.js");
            _capabilities = await module.InvokeAsync<BrowserCapabilities>("capabilityReport");
            await module.DisposeAsync();
        }
        catch (JSException ex)
        {
            TerminalLog.CapabilityProbeFailed(Logger, ex);
        }
    }

    /// <summary>
    /// Turns a failed pairing into something an operator can act on.
    /// </summary>
    /// <remarks>
    /// "Unsupported" and "no device selected" need completely different responses from the
    /// person at the till, so they must not collapse into one generic message.
    /// </remarks>
    private static string DescribePairingFailure(DeviceState state) => state.Status switch
    {
        DeviceStatus.Unsupported =>
            "This browser does not support that connection. Use Chrome or Edge, or connect the printer another way.",
        DeviceStatus.NotConfigured when state.Detail?.Contains("cancel", StringComparison.OrdinalIgnoreCase) == true =>
            "Pairing was cancelled.",
        DeviceStatus.NotConfigured =>
            state.Detail ?? "No printer was selected.",
        _ => state.Detail ?? $"Pairing failed ({state.Status}).",
    };

    /// <summary>Extracts a useful message from an error response body.</summary>
    private static string Summarise(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("detail", out var detail))
            {
                return detail.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Fall through to the truncated raw body.
        }

        return body.Length > 200 ? body[..200] : body;
    }

    /// <summary>
    /// The origin the till was served from, offered as the hub default.
    /// </summary>
    /// <remarks>
    /// Taken from <see cref="NavigationManager"/> rather than a JavaScript <c>eval</c>, which a
    /// Content Security Policy — as any real deployment should have — blocks outright.
    /// </remarks>
    private string GetCurrentOrigin() =>
        Nav.BaseUri.TrimEnd('/');

    private void BackToCheckout() => Nav.NavigateTo("/");

    private static string YesNo(bool? value) => value switch
    {
        true => "yes",
        false => "no",
        null => "unknown",
    };

    private async Task RunAsync(Func<Task> action)
    {
        _busy = true;
        ClearMessage();

        try
        {
            await action();
        }
        catch (SyncAuthorisationException)
        {
            await Identity.ClearEnrolmentAsync(_cts.Token);
            Fail("The hub rejected this terminal's credential. It may have been revoked.");
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void Succeed(string message)
    {
        _message = message;
        _messageIsError = false;
    }

    private void Fail(string message)
    {
        _message = message;
        _messageIsError = true;
    }

    private void ClearMessage() => _message = null;

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();

        return ValueTask.CompletedTask;
    }

    /// <summary>Result of a successful enrolment.</summary>
    /// <remarks>
    /// A local copy of the hub's response shape rather than a reference to the server project, which the
    /// till must not depend on. <c>RotateAfter</c> is when this credential is due for replacement.
    /// </remarks>
    private sealed record EnrollResponse(
        string DeviceId,
        string StoreId,
        string StoreCode,
        string Currency,
        string TaxMode,
        DateTimeOffset RotateAfter);

    /// <summary>What the browser can do, for the diagnostics panel.</summary>
    private sealed record BrowserCapabilities(
        bool WebUsb,
        bool WebSerial,
        bool WebHid,
        bool Bluetooth,
        bool SecureContext,
        bool CrossOriginIsolated);
}
