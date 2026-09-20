// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Pos.Core.Domain;
using Pos.Core.Payments;
using Pos.Devices.EscPos;
using Pos.Infrastructure.Checkout;
using Pos.Infrastructure.Storage;
using Pos.Infrastructure.Sync;
using Pos.Web.Terminal;

namespace Pos.Web.Pages;

/// <summary>
/// The till screen: scan, build a basket, take payment, print, and sync.
/// </summary>
/// <remarks>
/// <para>
/// Designed around a keyboard-wedge barcode scanner, which is what virtually all handheld
/// scanners present as. The scan box keeps focus so a scan types straight into it and the
/// trailing Enter submits it, with no device API involved and therefore no browser
/// restriction at all. WebUSB and WebHID add capabilities on top; they are never required
/// to ring up a sale.
/// </para>
/// <para>
/// The ordering in <see cref="CompleteSaleAsync"/> is the important part: the sale is
/// recorded durably <em>before</em> the printer is asked for anything. A printer that is
/// out of paper costs a reprint; a sale lost between the two would cost the money.
/// </para>
/// </remarks>
public sealed partial class Checkout : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();

    [Inject]
    private CustomerDisplayPublisher Display { get; set; } = default!;

    /// <summary>
    /// Sends preparation tickets to the kitchen and bar.
    /// </summary>
    /// <remarks>
    /// A distinct type from <c>Printers</c> so the two cannot be confused: injecting the receipt
    /// printer here and sending order tickets to it would print food orders on the customer's roll.
    /// </remarks>
    [Inject]
    private KitchenTicketPrinter Kitchen { get; set; } = default!;

    /// <summary>
    /// Takes payment and then records the sale.
    /// </summary>
    /// <remarks>
    /// Distinct from <c>Recorder</c>, which writes a sale down without asking anyone for money.
    /// Going through here is what makes a declined card stop the sale being recorded.
    /// </remarks>
    [Inject]
    private SaleCompletionService Completion { get; set; } = default!;

    private ElementReference _scanInput;
    private string _scanText = string.Empty;
    private decimal? _cashTendered;
    private string? _message;
    private bool _messageIsError;
    private bool _ready;
    private bool _persisted;
    private bool _printerReady;
    private string _printerName = "resolving…";
    private int _pendingSync;
    private List<StoredProduct> _matches = [];

    private Store? _store => Session.Store;

    private Cart? Cart => Session.Cart;

    private CartTotals? Totals => Cart is null || Cart.IsEmpty ? null : Cart.CalculateTotals();

    protected override async Task OnInitializedAsync()
    {
        try
        {
            await Startup.InitialiseAsync(_cts.Token);

            await RefreshStatusAsync();

            _ready = true;

            // Focus is taken in OnAfterRenderAsync instead: the input is not in the DOM
            // yet at this point, so calling it here would be a no-op.
            var printer = await Printers.RequireAsync(_cts.Token);

            _printerName = printer.Transport.DisplayName;
            _printerReady = printer.Transport.State.IsReady;

            // Tell the display what is on screen now, which also restarts the heartbeat that
            // DisposeAsync stopped. Without this, any trip to another page left the display
            // claiming the till had gone until the next basket edit happened to publish.
            await PublishDisplayAsync();
        }
        catch (Exception ex)
        {
            Fail($"The terminal could not start: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------------ scanning

    private async Task OnScanKeyDown(KeyboardEventArgs args)
    {
        if (!string.Equals(args.Key, "Enter", StringComparison.Ordinal))
        {
            await SearchAsync();
            return;
        }

        var code = _scanText.Trim();
        if (code.Length == 0)
        {
            return;
        }

        _scanText = string.Empty;
        _matches = [];

        await LookupAsync(code);
        await FocusScannerAsync();
    }

    private async Task LookupAsync(string code)
    {
        if (_store is null)
        {
            Fail("No store is loaded. Open the checkout screen first.");
            return;
        }

        try
        {
            var exact = await LocalStore.FindProductByBarcodeAsync(_store.Id.ToString(), code, _cts.Token);

            if (exact is not null)
            {
                // An exact barcode match goes straight into the basket. Presenting a
                // one-item list for the operator to confirm would double the work of every
                // scan at a busy till.
                await AddProductAsync(exact);
                return;
            }

            // No barcode match, so treat the input as a name query. This is the fallback
            // for a label that will not scan, which happens routinely in a real shop.
            await SearchAsync(code);
        }
        catch (Exception ex)
        {
            Fail($"Lookup failed: {ex.Message}");
        }
    }

    private async Task SearchAsync(string? term = null)
    {
        var query = (term ?? _scanText).Trim();

        if (query.Length < 2 || _store is null)
        {
            _matches = [];
            return;
        }

        try
        {
            // A scanned barcode resolves exactly; anything else falls through to a name
            // search, so an operator can still find an item whose label will not scan.
            //
            // Both are scoped to this store. A terminal holds the whole estate's catalogue, so an
            // unscoped lookup would offer another shop's products here — with their prices.
            var storeId = _store.Id.ToString();

            var exact = await LocalStore.FindProductByBarcodeAsync(storeId, query, _cts.Token);

            if (exact is not null)
            {
                _matches = [exact];
                return;
            }

            var found = await LocalStore.SearchProductsByNameAsync(storeId, query, 8, _cts.Token);
            _matches = [.. found];
        }
        catch (Exception ex)
        {
            Fail($"Search failed: {ex.Message}");
        }
    }

    private async Task AddProductAsync(StoredProduct product)
    {
        if (Cart is null)
        {
            return;
        }

        // Money has been taken against this basket, so its contents are fixed until the payments
        // are removed. Silently re-pricing a basket the customer has already part-paid changes
        // what they owe without telling anyone.
        if (IsBasketLocked)
        {
            RefuseBasketEdit();
            return;
        }

        try
        {
            // Projected through the shared mapper rather than hand-copied. The hand-written
            // version here dropped the station, so a scanned burger produced no kitchen ticket —
            // and nothing failed, the kitchen simply never heard about the order.
            Cart.AddProduct(product.ToDomain(Cart.Currency), product.IsSoldByWeight ? 0.5m : 1m);

            Session.NoteScan(product.Name);
            _matches = [];
            _scanText = string.Empty;
            ClearMessage();

            await PublishDisplayAsync($"Added {product.Name}");
        }
        catch (Exception ex)
        {
            Fail($"Could not add {product.Name}: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    private void OnQuantityChangedAsync(CartLine line, ChangeEventArgs args)
    {
        if (IsBasketLocked)
        {
            RefuseBasketEdit();
            return;
        }

        var raw = args.Value?.ToString();

        if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var quantity) &&
            quantity > 0m)
        {
            line.Quantity = quantity;
            ClearMessage();
            _ = PublishDisplayAsync();
        }
        else
        {
            // Rejecting a bad quantity silently would leave the screen showing one number
            // and charging another, so the operator is told.
            Fail("Quantity must be a number greater than zero.");
        }
    }

    private void RemoveLine(CartLine line)
    {
        if (IsBasketLocked)
        {
            RefuseBasketEdit();
            return;
        }

        Cart?.Remove(line);
        ClearMessage();
        _ = PublishDisplayAsync($"Removed {line.Name}");
    }

    private void ClearCart()
    {
        if (IsBasketLocked)
        {
            RefuseBasketEdit();
            return;
        }

        Cart?.Clear();
        _cashTendered = null;
        _voucherAmount = null;
        _tender = null;
        _matches = [];
        ClearMessage();
        _ = PublishDisplayAsync();
    }

    /// <summary>
    /// Pushes the current basket to the customer-facing display.
    /// </summary>
    /// <remarks>
    /// Best-effort and never awaited by the caller's critical path: the display is an output
    /// only, so a display that is closed or crashed must not slow down or break a sale.
    /// </remarks>
    private async Task PublishDisplayAsync(string? message = null)
    {
        try
        {
            if (Cart is null || Cart.IsEmpty)
            {
                await Display.PublishAsync(CustomerDisplayState.Idle, _cts.Token);
                return;
            }

            var state = CustomerDisplayPublisher.FromCart(Cart, Cart.CalculateTotals(), message);
            await Display.PublishAsync(state, _cts.Token);
        }
        catch (Exception ex)
        {
            TerminalLog.DisplayPublishFailed(Logger, ex);
        }
    }

    // ------------------------------------------------------------------------- payment

    private decimal ChangeDue(decimal total) =>
        _cashTendered is { } tendered && tendered > total ? tendered - total : 0m;

    // --------------------------------------------------------------------- tendering

    /// <summary>
    /// Payments applied to the basket so far, and what is still owed.
    /// </summary>
    /// <remarks>
    /// The session follows the basket while nothing has been paid, and is frozen the moment a
    /// payment is applied. That combination is what makes split tender safe: a session carries a
    /// total, so letting the basket change underneath payments already taken would have the till
    /// settle the wrong figure — and the customer would be owed money nobody noticed.
    /// </remarks>
    private TenderSession Tender
    {
        get
        {
            var total = Totals?.Total ?? 0m;

            if (_tender is null ||
                (_tender.IsEmpty && _tender.Total.Amount != total))
            {
                _tender = new TenderSession(new Money(total, Cart?.Currency ?? "ZAR"));
            }

            return _tender;
        }
    }

    private TenderSession? _tender;

    private decimal? _voucherAmount;

    /// <summary>
    /// True when the basket may not be edited because money has been taken against it.
    /// </summary>
    /// <remarks>
    /// Refusing the edit is the honest answer. The alternative — silently adjusting a basket the
    /// customer has already part-paid — changes what they owe without telling anyone.
    /// </remarks>
    private bool IsBasketLocked => _tender is { IsEmpty: false };

    /// <summary>Explains why the basket is locked, for an operator who just tried to edit it.</summary>
    private void RefuseBasketEdit()
    {
        Fail("Payments have been taken against this basket. Remove them before changing the items.");
    }

    private void AddCash()
    {
        try
        {
            // Whatever the operator typed, or exactly what is owed if they typed nothing.
            Tender.AddCash(_cashTendered ?? Tender.Outstanding);
            _cashTendered = null;
            ClearMessage();
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private void AddVoucher(TenderType type)
    {
        try
        {
            // Defaults to settling the balance, which is the common case: a gift card covering
            // whatever is left.
            Tender.AddExact(type, _voucherAmount ?? Tender.Outstanding);
            _voucherAmount = null;
            ClearMessage();
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private void RemoveTender(Tender tender)
    {
        Tender.Remove(tender);
        ClearMessage();
    }

    private void ClearTender()
    {
        Tender.Clear();
        _cashTendered = null;
        _voucherAmount = null;
        ClearMessage();
    }

    /// <summary>Describes a payment the way an operator would say it.</summary>
    private static string DescribeTender(Tender tender)
    {
        var label = tender.Type switch
        {
            TenderType.ExternalCard => "Card",
            TenderType.GiftCard => "Gift card",
            TenderType.Voucher => "Voucher",
            TenderType.LoyaltyPoints => "Loyalty points",
            _ => "Cash",
        };

        if (tender.Type == TenderType.Cash && tender.Tendered is { } handed &&
            handed.Amount > tender.Amount.Amount)
        {
            // The amount handed over is worth showing: it is what the drawer has to account for,
            // and it is how the operator checks they recorded the right note.
            return $"Cash ({Format(handed.Amount)} received)";
        }

        return string.IsNullOrWhiteSpace(tender.Reference)
            ? label
            : $"{label} ({tender.Reference})";
    }

    // ------------------------------------------------------------------- completion

    /// <summary>
    /// Settles the outstanding balance with one method and completes the sale.
    /// </summary>
    /// <remarks>
    /// The common path: the whole balance on cash or on a card. Split tender is built up with the
    /// fields above and then completed with no method, because the balance is already zero.
    /// </remarks>
    private async Task SettleWithAsync(TenderType type)
    {
        if (Tender.IsSettled)
        {
            // Already covered by earlier payments, so there is nothing to add.
            await CompleteSaleAsync(openDrawer: Tender.Applied.Any(t => t.Type == TenderType.Cash));
            return;
        }

        try
        {
            if (type == TenderType.Cash)
            {
                // Whatever the operator typed, or exactly what is owed if they typed nothing.
                Tender.AddCash(_cashTendered ?? Tender.Outstanding);
                _cashTendered = null;
            }
            else
            {
                Tender.AddExact(type, Tender.Outstanding);
            }
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            return;
        }

        await CompleteSaleAsync(openDrawer: Tender.Applied.Any(t => t.Type == TenderType.Cash));
    }

    /// <summary>
    /// Secures the payments and records the sale.
    /// </summary>
    /// <param name="openDrawer">
    /// Whether to pulse the drawer. True whenever any cash was taken, because cash that went in has
    /// to be able to come back out as change.
    /// </param>
    private async Task CompleteSaleAsync(bool openDrawer)
    {
        if (Cart is null || _store is null || Cart.IsEmpty)
        {
            return;
        }

        try
        {
            var session = Tender;

            // Pays first, records second. A sale written against a payment that later declined is
            // a sale the shop cannot collect on, and the customer is already walking out with the
            // goods. A PaymentDeclinedException from here leaves the basket and its payments
            // untouched, so the operator can simply take another method.
            //
            // The operator and the shift go on the sale together. The operator is who is
            // answerable for it; the shift is which drawer the cash landed in.
            var completed = await Completion.PayAndRecordAsync(
                Cart,
                session,
                _store,
                employeeId: Session.Employee?.Id.ToString(),
                businessDate: null,
                shiftId: Session.Shift?.Id,
                catalogVersion: null,
                ct: _cts.Token);

            Session.LastSale = completed;

            // A fresh cart for the next customer.
            Session.Cart = new Cart(_store.Id, _store.Currency, _store.TaxMode);
            _tender = null;
            _cashTendered = null;
            _voucherAmount = null;
            _scanText = string.Empty;
            _matches = [];

            await PrintAsync(completed, openDrawer);

            // The kitchen and bar are told after the receipt, never before. By this point the
            // order exists and has been paid for, so a station printer that is unplugged cannot
            // affect the sale — it only means somebody has to be told by hand.
            await Kitchen.PrintAsync(_store, completed, Session.Employee?.Name, _cts.Token);

            await RefreshStatusAsync();

            // The customer sees the total they just paid, not an empty basket, until the
            // next item is scanned.
            await Display.PublishAsync(CustomerDisplayState.ThankYou(completed.Sale.Total), _cts.Token);

            // A sale is worth pushing immediately; waiting for a timer would leave a day's
            // takings sitting on the terminal until someone noticed.
            await SyncQuietlyAsync();

            Succeed($"Sale {completed.Sale.Number} recorded — {Format(completed.Sale.Total)}");
            await FocusScannerAsync();
        }
        catch (PaymentDeclinedException ex)
        {
            // A refusal is an ordinary retail outcome, not a fault: the basket is intact and the
            // payments already applied are still there, so the operator takes another method.
            Fail($"Payment declined — {ex.Message}");
        }
        catch (Exception ex)
        {
            Fail($"The sale could not be completed: {ex.Message}");
        }
    }

    private async Task PrintAsync(CompletedSale completed, bool openDrawer)
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            var printer = await Printers.RequireAsync(_cts.Token);

            // The receipt names the cashier, so a customer holding one can be told who served
            // them, and a reprinted copy still names the original operator.
            await printer.PrintReceiptAsync(
                completed.ToDocument(_store, cashierName: Session.Employee?.Name),
                openDrawer,
                _cts.Token);

            _printerReady = printer.Transport.State.IsReady;
        }
        catch (Exception ex)
        {
            // Reported, not thrown: the money is safely recorded, so a printer fault must
            // not present itself as a failed sale.
            _printerReady = false;
            Fail($"Sale recorded, but the receipt did not print ({ex.Message}). Use Reprint.");
        }
    }

    private async Task ReprintLastAsync()
    {
        if (Session.LastSale is not { } last || _store is null)
        {
            return;
        }

        try
        {
            var printer = await Printers.RequireAsync(_cts.Token);

            // Marked as a copy: an unmarked duplicate could be presented twice for the
            // same refund.
            await printer.PrintReceiptAsync(
                last.ToDocument(_store, PosDocumentType.SalesReceiptCopy, copyIndex: 1),
                openDrawer: false,
                _cts.Token);

            Succeed($"Reprinted {last.Sale.Number} as a copy.");
        }
        catch (Exception ex)
        {
            Fail($"Reprint failed: {ex.Message}");
        }
    }

    private async Task OpenDrawerAsync()
    {
        try
        {
            var printer = await Printers.RequireAsync(_cts.Token);

            await printer.OpenDrawerAsync(_cts.Token);
            Succeed("Drawer opened.");
        }
        catch (Exception ex)
        {
            Fail($"The drawer could not be opened: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------------- sync

    private async Task SyncNowAsync()
    {
        try
        {
            var result = await Sync.RunOnceAsync(_cts.Token);

            if (result.Error is { } error)
            {
                Fail($"Could not reach the hub ({error}). Sales are queued and safe.");
            }
            else if (result.Pushed == 0 && result.Applied == 0)
            {
                Succeed("Already up to date.");
            }
            else
            {
                Succeed($"Sent {result.Pushed}, received {result.Applied}.");
            }

            await RefreshStatusAsync();
        }
        catch (SyncAuthorisationException)
        {
            // The credential is gone. Retrying forever would be pointless and would leave
            // the terminal believing it was syncing.
            await Identity.ClearEnrolmentAsync(_cts.Token);
            Fail("This terminal has been revoked. It must be enrolled again.");
        }
        catch (Exception ex)
        {
            Fail($"Sync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Syncs after a sale without interrupting the operator if the hub is unreachable.
    /// </summary>
    /// <remarks>
    /// An offline shop is the normal case. Reporting every failed push as an error would
    /// train cashiers to ignore the message area, which is where real faults appear.
    /// </remarks>
    private async Task SyncQuietlyAsync()
    {
        try
        {
            await Sync.RunOnceAsync(_cts.Token);
        }
        catch (SyncAuthorisationException)
        {
            await Identity.ClearEnrolmentAsync(_cts.Token);
        }
        catch
        {
            // Queued entries remain queued and are retried on the next pass.
        }
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            var summary = await LocalStore.GetOutboxSummaryAsync(_cts.Token);
            _pendingSync = summary.Pending + summary.Dead;

            _persisted = await LocalStore.IsPersistedAsync(_cts.Token);

            if (Printers.Current is { } printer)
            {
                _printerReady = printer.Transport.State.IsReady;
                _printerName = printer.Transport.DisplayName;
            }
        }
        catch (Exception ex)
        {
            Fail($"Status refresh failed: {ex.Message}");
        }
    }

    // --------------------------------------------------------------------------- chrome

    private async Task FocusScannerAsync()
    {
        try
        {
            await _scanInput.FocusAsync();
        }
        catch (JSException)
        {
            // Focus can be refused before the element is laid out, or in a background tab.
            // The operator can click the box instead, so this is not worth reporting.
        }
    }

    /// <summary>
    /// Puts focus back in the scan box once the component has finished rendering.
    /// </summary>
    /// <remarks>
    /// Focus cannot be taken during initialisation: the input does not exist in the DOM
    /// yet, so the call is silently dropped and the first scan of the day is typed into
    /// the page instead of the basket. Without this, every scan needs a manual click first.
    /// </remarks>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || !_ready)
        {
            return;
        }

        await FocusScannerAsync();
    }

    private void FocusScanner() => _ = FocusScannerAsync();

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

    private static string Format(decimal amount) =>
        amount.ToString("0.00", CultureInfo.InvariantCulture);

    private void OpenDeviceSettings() => Nav.NavigateTo("devices");

    private void OpenReports() => Nav.NavigateTo("reports");

    private void OpenRefunds() => Nav.NavigateTo("refunds");

    private void OpenCatalog() => Nav.NavigateTo("catalog");

    private void OpenTransfers() => Nav.NavigateTo("transfers");

    private void OpenSignIn() => Nav.NavigateTo("signin");

    private void OpenDrawerPage() => Nav.NavigateTo("shift");

    private bool _operatorMenuOpen;

    /// <summary>
    /// The operator pill. Signed in, it opens a small menu; signed out, it goes straight
    /// to the sign-in screen, because that is the only thing the pill can offer then.
    /// </summary>
    private void OnOperatorPill()
    {
        if (!Session.IsSignedIn)
        {
            OpenSignIn();
            return;
        }

        _operatorMenuOpen = !_operatorMenuOpen;
    }

    private void CloseOperatorMenu() => _operatorMenuOpen = false;

    private void OpenDrawerFromMenu()
    {
        _operatorMenuOpen = false;
        OpenDrawerPage();
    }

    /// <summary>
    /// Ends the operator's session while leaving the shift running for whoever takes over.
    /// </summary>
    /// <remarks>
    /// The till stays exactly where it is — the basket and the screen are untouched, only the
    /// attribution is gone. The shift record itself is untouched, so the next person to sign in
    /// joins the drawer that is already open rather than starting a new one.
    /// </remarks>
    private async Task SignOutFromMenu()
    {
        Session.SignOut();
        _operatorMenuOpen = false;

        // Cleared at every explicit sign-out, so a refresh does not re-attach the operator
        // who just left.
        await SignInMarker.ClearAsync(_cts.Token);

        Succeed("Signed out. The shift is still open — the next person to sign in joins it.");
    }

    /// <summary>Describes an operator's authority the way the shop would say it.</summary>
    private static string RoleOf(Employee employee)
    {
        ArgumentNullException.ThrowIfNull(employee);

        if (employee.Can(EmployeePermissions.ManageCatalog))
        {
            return "Manager";
        }

        return employee.Can(EmployeePermissions.Supervisor) ? "Supervisor" : "Cashier";
    }

    /// <summary>
    /// Opens the customer display in its own window.
    /// </summary>
    /// <remarks>
    /// A separate window rather than a route, because it is meant to sit on a second screen
    /// facing the customer while the till stays on this one. A blocked pop-up is common and is
    /// reported rather than failing the sale, since the till works without a display.
    /// </remarks>
    private async Task OpenCustomerDisplay()
    {
        try
        {
            await JS.InvokeVoidAsync(
                "open",
                "/display",
                "pos-customer-display",
                "width=1024,height=768");

            await Display.PublishAsync(CustomerDisplayState.Idle, _cts.Token);
        }
        catch (Exception ex)
        {
            TerminalLog.DisplayPublishFailed(Logger, ex);
            Fail("The customer display window could not be opened. Check the pop-up blocker.");
        }
    }

    public ValueTask DisposeAsync()
    {
        // Told to the display, not merely stopped on this side. This screen is a singleton in
        // practice, but a heartbeat that outlived the till would keep telling a customer-facing
        // display that a closed till was still running — which is worse than saying nothing.
        Display.StopHeartbeat();

        _cts.Cancel();
        _cts.Dispose();

        return ValueTask.CompletedTask;
    }
}
