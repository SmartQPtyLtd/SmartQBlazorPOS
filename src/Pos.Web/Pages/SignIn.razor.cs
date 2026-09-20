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
using Pos.Infrastructure.Checkout;
using Pos.Infrastructure.Storage;
using Pos.Web.Terminal;

namespace Pos.Web.Pages;

/// <summary>
/// Operator sign-in: choose a person, enter a PIN, open the drawer.
/// </summary>
/// <remarks>
/// <para>
/// This is where cash accountability starts. A sale recorded with no operator is a sale nobody
/// is answerable for, so signing in is what makes the rest of the day auditable.
/// </para>
/// <para>
/// The PIN is <b>accountability, not security</b>. It exists so a refund or a void has a name
/// against it, not to keep an attacker out — anyone holding the till has the device. That is why
/// the entry is an on-screen keypad that shows the digits rather than a masked field, and why a
/// wrong PIN is refused with the same message whether or not the account exists.
/// </para>
/// <para>
/// The keypad is an explicit grid of buttons rather than a keyboard listener. A keyboard-wedge
/// barcode scanner types into whatever is focused, so a page that captured raw keystrokes would
/// have a scan land in the PIN box — and, worse, could sign someone in from across the counter.
/// </para>
/// </remarks>
public sealed partial class SignIn : IAsyncDisposable
{
    /// <summary>Shortest PIN the keypad will submit.</summary>
    private const int MinPinLength = 4;

    /// <summary>Longest accepted before the keypad stops adding digits.</summary>
    private const int MaxPinLength = 8;

    private static readonly char[] Digits = ['1', '2', '3', '4', '5', '6', '7', '8', '9', '0'];

    private readonly CancellationTokenSource _cts = new();

    private List<StoredEmployee> _employees = [];

    private StoredEmployee? _chosen;
    private string _pin = string.Empty;
    private decimal _openingFloat;
    private bool _shiftAlreadyOpen;
    private bool _showDemoPins;

    private string? _message;
    private bool _messageIsError;
    private bool _busy;

    /// <summary>True when signing in will open a new drawer and therefore needs a float.</summary>
    private bool NeedsOpeningFloat => !_shiftAlreadyOpen;

    private bool _needsSupervision;

    protected override async Task OnInitializedAsync()
    {
        // The store is loaded by whichever screen got here first. Arriving at sign-in directly —
        // by bookmark, or straight after an install — would otherwise show an empty roster.
        if (Session.Store is null)
        {
            try
            {
                await Startup.InitialiseAsync(_cts.Token);
            }
            catch (Exception ex)
            {
                TerminalLog.SignInLoadFailed(Logger, ex);
                Fail("This terminal could not be prepared. Reload the page.");
                return;
            }
        }

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var store = Session.Store;

        if (store is null)
        {
            Fail("No store is loaded. Open the checkout screen first.");
            return;
        }

        _busy = true;

        try
        {
            _employees = [.. await Roster.GetEmployeesAsync(store.Id.ToString(), _cts.Token)];

            // Whether this sign-in opens a drawer or joins a running one is the difference between
            // being asked for a float and not, so it is read before the pad is drawn.
            _shiftAlreadyOpen = await Shifts.GetOpenShiftAsync(_cts.Token) is not null;

            _showDemoPins = _employees.Any(e =>
                e.Name is "Thandi Mokoena" or "Sipho Ndlovu" or "Anele Botha");

            ClearMessage();
        }
        catch (Exception ex)
        {
            TerminalLog.SignInLoadFailed(Logger, ex);
            Fail("The roster could not be read on this terminal.");
        }
        finally
        {
            _busy = false;
        }
    }

    private bool IsChosen(StoredEmployee employee) =>
        _chosen is not null && string.Equals(_chosen.Id, employee.Id, StringComparison.Ordinal);

    private void Choose(StoredEmployee employee)
    {
        ArgumentNullException.ThrowIfNull(employee);

        // Changing who is signing in starts the PIN again. Carrying digits across would let a
        // half-typed PIN from one person complete another person's sign-in.
        _chosen = employee;
        _pin = string.Empty;
        _needsSupervision = employee.RequiresSupervision;

        ClearMessage();
    }

    private void Press(char digit)
    {
        if (_pin.Length >= MaxPinLength)
        {
            return;
        }

        _pin += digit;
        ClearMessage();
    }

    private void Backspace()
    {
        if (_pin.Length > 0)
        {
            _pin = _pin[..^1];
        }
    }

    private void Clear()
    {
        _pin = string.Empty;
        _needsSupervision = false;

        ClearMessage();
    }

    private async Task SignInAsync()
    {
        if (_chosen is not { } employee)
        {
            return;
        }

        _busy = true;

        try
        {
            var result = await Shifts.SignInAsync(
                employee.Id, _pin, _openingFloat, _cts.Token);

            Session.Employee = result.Employee;
            Session.Shift = result.Shift;

            // Written only now that the sign-in succeeded: the marker is what lets a refresh
            // re-attach this operator, so it must never name someone who failed the PIN.
            await SignInMarker.MarkAsync(result.Employee.Id.ToString(), _cts.Token);

            _pin = string.Empty;

            Succeed(result.OpenedNewShift
                ? $"Signed in as {result.Employee.Name}. Drawer opened with a float of {Money(_openingFloat)}."
                : $"Signed in as {result.Employee.Name}. Joined the shift already running on this terminal.");
        }
        catch (Exception ex)
        {
            // A wrong PIN is an ordinary outcome, not a fault, so it reads as a message rather
            // than an error page.
            _pin = string.Empty;
            Fail(ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void GoToDrawer() => Nav.NavigateTo("/shift");

    /// <summary>
    /// Ends the operator's session and shows the roster again.
    /// </summary>
    /// <remarks>
    /// The shift keeps running: a plain sign-out is a handover, and the next person to sign
    /// in joins the drawer that is already open. Closing the drawer is a separate act, done
    /// on the cash drawer screen.
    /// </remarks>
    private async Task SignOut()
    {
        Session.SignOut();

        // Cleared here and at every other explicit sign-out, so a refresh does not re-attach
        // the operator who just left.
        await SignInMarker.ClearAsync(_cts.Token);

        Succeed("Signed out. The shift is still running — the next operator to sign in joins it.");
    }

    private void BackToCheckout() => Nav.NavigateTo("/");

    private static string Initials(StoredEmployee employee) =>
        !string.IsNullOrWhiteSpace(employee.Initials)
            ? employee.Initials
            : string.Concat(
                employee.Name
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Take(2)
                    .Select(part => char.ToUpperInvariant(part[0])));

    /// <summary>Describes an operator's authority in the words the shop would use.</summary>
    private static string Role(StoredEmployee employee)
    {
        if (!Enum.TryParse<EmployeePermissions>(employee.Permissions, out var permissions))
        {
            return "No permissions";
        }

        if (permissions.HasFlag(EmployeePermissions.ManageCatalog))
        {
            return "Manager";
        }

        if (permissions.HasFlag(EmployeePermissions.Supervisor))
        {
            return "Supervisor";
        }

        return "Cashier";
    }

    private static string Money(decimal amount) =>
        amount.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// How far along the PIN is, for the PIN pad's live region.
    /// </summary>
    /// <remarks>
    /// Deliberately counts digits rather than echoing them. The digits are on screen for the operator
    /// standing there; reading them aloud would put the PIN in the room and in the browser's
    /// accessibility tree, which is the one thing masking the dots was designed to avoid.
    /// <para>
    /// Empty at nought, so the region starts empty and every change is a change. Writing "0 of 4"
    /// into it at first render would be text nobody ever hears.
    /// </para>
    /// </remarks>
    private string PinProgress() => _pin.Length == 0
        ? string.Empty
        : $"{_pin.Length} of 4 digits entered";

    private static string When(string? at) =>
        DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment)
            ? moment.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture)
            : "just now";

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
