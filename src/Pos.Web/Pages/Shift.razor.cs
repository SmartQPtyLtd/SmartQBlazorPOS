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
using Pos.Core.Domain;
using Pos.Core.Reporting;
using Pos.Devices.EscPos;
using Pos.Devices.Reports;
using Pos.Infrastructure.Checkout;
using Pos.Infrastructure.Storage;
using Pos.Web.Terminal;

namespace Pos.Web.Pages;

/// <summary>
/// The cash drawer: activity, manual movements, and the cash-up that closes a shift.
/// </summary>
/// <remarks>
/// <para>
/// This screen exists to make one number answerable: the difference between what should be in the
/// drawer and what is. Everything on it serves that, and two design choices are load-bearing.
/// </para>
/// <para>
/// <b>The expected figure is hidden until a count is entered.</b> A cashier who can see that the
/// drawer should hold 1 240.50 will count to 1 240.50, and the count stops being evidence of
/// anything. The rule is enforced in <see cref="ShiftReport"/>, which strips the expectation from
/// a mid-shift reading, so it holds for the printed slip as well as the screen.
/// </para>
/// <para>
/// <b>Non-cash tenders are shown but never counted.</b> Card money never entered the drawer;
/// adding it to the expected total would make every single shift look over by the card takings.
/// </para>
/// </remarks>
public sealed partial class Shift : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();

    private ShiftTotals? _totals;
    private List<DrawerEvent> _events = [];

    private string _activityKind = "nosale";
    private decimal _activityAmount;
    private string _activityReason = string.Empty;

    private decimal _closingCount;
    private string _endReason = nameof(ShiftEndReason.Handover);
    private string _closeNote = string.Empty;

    /// <summary>The cash-up, once the count has been submitted.</summary>
    private ShiftTotals? _closed;

    /// <summary>
    /// The shift that was just closed, kept because closing also signs the operator out —
    /// after <see cref="CloseAsync"/> the session holds nothing, but the summary on screen
    /// still belongs to this shift.
    /// </summary>
    private StoredShift? _closedShift;

    private string? _message;
    private bool _messageIsError;
    private bool _busy;

    /// <summary>True when the signed-in operator may open the drawer at all.</summary>
    private bool CanOpenDrawer =>
        Session.Employee?.Can(EmployeePermissions.OpenDrawer) == true;

    /// <summary>
    /// True when the signed-in operator may sign the drawer off.
    /// </summary>
    /// <remarks>
    /// Checked before the count form is shown, not after it is submitted. Closing is what converts a
    /// pile of cash into an agreed figure, so it belongs to whoever is accountable for it — and an
    /// operator who is going to be refused should be told before they spend five minutes counting,
    /// not after.
    /// </remarks>
    private bool CanCloseShift =>
        Session.Employee?.Can(EmployeePermissions.CloseShift) == true;

    /// <summary>The shift the screen is about: the open one, or the one just closed.</summary>
    private StoredShift? DisplayedShift => Session.Shift ?? _closedShift;

    private string ActivityReasonPlaceholder => _activityKind switch
    {
        "nosale" => "e.g. customer wanted change for the bus",
        "in" => "e.g. float top-up from the safe",
        _ => "e.g. banked to the safe",
    };

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (!Session.IsSignedIn || Session.Shift is not { } shift)
        {
            return;
        }

        _busy = true;

        try
        {
            _totals = await Shifts.CalculateTotalsAsync(shift.Id, _cts.Token);
            _events = await LoadEventsAsync(shift.Id);

            // A shift that is already closed shows its cash-up rather than an empty count box.
            if (!shift.IsOpen)
            {
                _closed = _totals;
            }

            ClearMessage();
        }
        catch (Exception ex)
        {
            TerminalLog.DrawerLoadFailed(Logger, ex);
            Fail("The drawer could not be read on this terminal.");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<List<DrawerEvent>> LoadEventsAsync(string shiftId)
    {
        var stored = await Roster.GetDrawerEventsAsync(shiftId, _cts.Token);

        return [.. stored.Select(e => e.ToDomain())];
    }

    private async Task RefreshAsync() => await LoadAsync();

    // ------------------------------------------------------------------- drawer activity

    private async Task RecordActivityAsync()
    {
        if (Session.Employee is not { } employee)
        {
            return;
        }

        _busy = true;

        try
        {
            if (_activityKind == "nosale")
            {
                await Shifts.RecordNoSaleAsync(employee, _activityReason, _cts.Token);
                Succeed("Drawer recorded as opened with no sale.");
            }
            else
            {
                var isCashIn = _activityKind == "in";

                await Shifts.RecordCashMovementAsync(
                    employee, _activityAmount, isCashIn, _activityReason, _cts.Token);

                Succeed(isCashIn
                    ? $"Recorded {Money(_activityAmount)} paid into the drawer."
                    : $"Recorded {Money(_activityAmount)} taken out of the drawer.");
            }

            _activityAmount = 0m;
            _activityReason = string.Empty;

            // The physical drawer is kicked only after the movement is durably recorded. The
            // other order loses the record if the tab dies between the two, and an unexplained
            // opening is exactly what the record exists to prevent.
            if (_activityKind == "nosale")
            {
                await KickDrawerAsync();
            }

            await LoadAsync();
        }
        catch (Exception ex)
        {
            // A policy refusal is an ordinary outcome — the wrong operator, or no reason given —
            // so it reads as a message rather than as a fault.
            Fail(ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task KickDrawerAsync()
    {
        try
        {
            var printer = await Printers.RequireAsync(_cts.Token);
            await printer.OpenDrawerAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            // The movement is already recorded, so a drawer that will not fire is a prompt to
            // open it by hand, not a reason to lose the record.
            TerminalLog.DrawerKickFailed(Logger, ex);
            Fail("Recorded, but the drawer did not open. Open it with the key.");
        }
    }

    // -------------------------------------------------------------------------- closing

    private async Task CloseAsync()
    {
        if (Session.Employee is not { } employee)
        {
            return;
        }

        _busy = true;

        try
        {
            if (!Enum.TryParse<ShiftEndReason>(_endReason, out var reason))
            {
                reason = ShiftEndReason.Handover;
            }

            var result = await Shifts.CloseShiftAsync(
                employee,
                _closingCount,
                reason,
                string.IsNullOrWhiteSpace(_closeNote) ? null : _closeNote,
                countedByEmployeeId: null,
                _cts.Token);

            _closed = result.Totals;
            _totals = result.Totals;
            _closedShift = result.Shift;

            // Closing the drawer ends the operator's session with it. The count has been
            // signed off, so nothing afterwards should be attributed to someone who has
            // cashed up and gone — and the next sign-in opens a fresh drawer rather than
            // joining a closed one.
            Session.SignOut();

            // Cleared at every explicit sign-out, so a refresh does not re-attach the operator
            // who just cashed up.
            await SignInMarker.ClearAsync(_cts.Token);

            Succeed(DescribeVariance(result.Totals));

            // Printing is best-effort and happens after the close is durable, so a printer fault
            // cannot leave the shift open with the cash already counted out of it.
            await PrintSlipCoreAsync(result.Totals, isFinal: true);
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

    private async Task PrintSlipAsync()
    {
        if (_closed is not { } totals)
        {
            return;
        }

        _busy = true;

        try
        {
            await PrintSlipCoreAsync(totals, isFinal: true);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ReadAsync()
    {
        if (_totals is not { } totals)
        {
            return;
        }

        _busy = true;

        try
        {
            // A mid-shift reading leaves the shift open, and the slip deliberately carries no
            // expected total. See ShiftReport.
            await PrintSlipCoreAsync(totals, isFinal: false);
            Succeed("X reading sent to the printer.");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task PrintSlipCoreAsync(ShiftTotals totals, bool isFinal)
    {
        try
        {
            var store = Session.Store;

            var report = ShiftReport.From(
                totals,
                store?.Name ?? "Store",
                DisplayedShift?.TerminalId ?? "till",
                isFinal,
                DateTimeOffset.UtcNow);

            var bytes = ReportRenderer.RenderShift(report, store?.ReceiptColumns ?? ReportRenderer.DefaultColumns);

            var printer = await Printers.RequireAsync(_cts.Token);

            // Sent through the transport rather than a document-level method, matching how the
            // trading-day report prints. A report is already an ESC/POS document, so wrapping it
            // in a receipt interface would add a layer that does nothing.
            await printer.Transport.WriteAsync(bytes, _cts.Token);
        }
        catch (Exception ex)
        {
            TerminalLog.ShiftSlipFailed(Logger, ex);
            Fail("The cash-up was saved, but the slip did not print.");
        }
    }

    // --------------------------------------------------------------------------- display

    private static string DescribeVariance(ShiftTotals totals) => totals.Variance switch
    {
        null => "Shift closed.",
        0m => "Shift closed. The drawer balances.",
        < 0m => $"Shift closed. The drawer is SHORT by {Money(Math.Abs(totals.Variance.Value))}.",
        _ => $"Shift closed. The drawer is OVER by {Money(totals.Variance.Value)}.",
    };

    private static string VarianceLabel(ShiftTotals totals) => totals.Variance switch
    {
        null => "Variance",
        0m => "Balanced",
        < 0m => "SHORT",
        _ => "OVER",
    };

    private static string VarianceClass(ShiftTotals totals) => totals.Variance switch
    {
        null or 0m => "sh-total",
        < 0m => "sh-short",
        _ => "sh-over",
    };

    private static string Describe(DrawerEvent drawerEvent) => drawerEvent.Type switch
    {
        DrawerEventType.ShiftOpened => "Shift opened",
        DrawerEventType.ShiftClosed => "Shift closed",
        DrawerEventType.NoSale => "No sale",
        DrawerEventType.CashIn => "Cash in",
        DrawerEventType.CashOut => "Cash out",
        DrawerEventType.CountPerformed => "Count performed",
        _ => drawerEvent.Type.ToString(),
    };

    private static string Money(decimal amount) =>
        amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string When(DateTimeOffset moment) =>
        moment.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture);

    private static string When(string? at) =>
        DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment)
            ? When(moment)
            : "—";

    private void BackToCheckout() => Nav.NavigateTo("/");

    private void GoToSignIn() => Nav.NavigateTo("/signin");

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
}
