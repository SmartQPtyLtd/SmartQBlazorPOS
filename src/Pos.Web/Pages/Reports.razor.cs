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
using Pos.Core.Reporting;
using Pos.Devices.Reports;
using Pos.Infrastructure.Reporting;
using Pos.Infrastructure.Storage;
using Pos.Web.Terminal;

namespace Pos.Web.Pages;

/// <summary>
/// Trading reports: X-readings, Z-readings, and the day's figures.
/// </summary>
/// <remarks>
/// <para>
/// The report is rebuilt from the recorded sales every time rather than read from a running
/// total, so what is shown here is always reconcilable against the individual sales behind it.
/// </para>
/// <para>
/// Printing uses the same receipt printer as the till, because that is the only printer a shop
/// has at the counter.
/// </para>
/// </remarks>
public sealed partial class Reports : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();

    /// <summary>The roster, so a report can name the operator rather than print an identifier.</summary>
    [Inject]
    private IShiftStore Roster { get; set; } = default!;

    private TradingDayReport? _report;
    private DateOnly _date = DateOnly.FromDateTime(DateTime.Now);
    private int _topProductCount = 10;
    private string? _message;
    private bool _messageIsError;
    private bool _busy;

    /// <summary>
    /// Employee names by id, loaded with the report.
    /// </summary>
    /// <remarks>
    /// A sale records the operator's id, which is what survives a rename and what sync carries. Turning
    /// it back into a name for the person reading the report is presentation, so it happens here rather
    /// than in the report itself — and an id with no matching employee is shown as such rather than
    /// being dropped, because a sale rung on an unsigned terminal has to appear somewhere.
    /// </remarks>
    private Dictionary<string, string> _operatorNames =
        new(StringComparer.Ordinal);

    /// <summary>The name behind an operator id, or a marker when the roster does not know it.</summary>
    private string OperatorName(string? employeeId)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
        {
            return "No operator signed in";
        }

        return _operatorNames.TryGetValue(employeeId, out var name)
            ? name
            : $"Unknown operator ({employeeId[..Math.Min(8, employeeId.Length)]})";
    }

    /// <summary>Which view the screen is showing: one day, or a range.</summary>
    private bool _periodMode;

    private DateOnly _periodFrom = DateOnly.FromDateTime(DateTime.Now).AddDays(-6);
    private DateOnly _periodTo = DateOnly.FromDateTime(DateTime.Now);
    private PeriodReport? _period;

    /// <summary>Preset period lengths, since these are what a shopkeeper actually asks for.</summary>
    private static readonly (string Label, int Days)[] PeriodPresets =
    [
        ("Last 7 days", 7),
        ("Last 14 days", 14),
        ("Last 30 days", 30),
        ("Last 90 days", 90),
    ];

    protected override async Task OnInitializedAsync()
    {
        await LoadOperatorNamesAsync();
        await RefreshAsync();
    }

    /// <summary>
    /// Reads the roster so a report can name operators.
    /// </summary>
    /// <remarks>
    /// Failure is not fatal: a report that cannot resolve names still shows the takings, and refusing to
    /// produce a Z-report because the roster did not load would be a till that cannot close its day.
    /// </remarks>
    private async Task LoadOperatorNamesAsync()
    {
        var store = Session.Store;

        if (store is null)
        {
            return;
        }

        try
        {
            var employees = await Roster.GetEmployeesAsync(store.Id.ToString(), _cts.Token);

            _operatorNames = employees
                .GroupBy(e => e.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            TerminalLog.ReportOperatorNamesFailed(Logger, ex);
        }
    }

    // ------------------------------------------------------------------- period view

    /// <summary>Applies a preset range and rebuilds.</summary>
    /// <param name="days">How many days back from today, inclusive of today.</param>
    private async Task UsePresetAsync(int days)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);

        _periodTo = today;
        _periodFrom = today.AddDays(-(Math.Max(1, days) - 1));
        _periodMode = true;

        await RefreshPeriodAsync();
    }

    private async Task RefreshPeriodAsync()
    {
        _busy = true;
        ClearMessage();

        try
        {
            var store = Session.Store;

            if (store is null)
            {
                Fail("No store is loaded. Open the checkout screen first.");
                return;
            }

            if (_periodTo < _periodFrom)
            {
                // Refused rather than shown as an empty report: a mistyped date looks exactly like
                // a period in which the shop took nothing.
                Fail("The period ends before it starts. Check the dates.");
                _period = null;
                return;
            }

            // Bounded so a mistyped year does not read every sale the terminal has ever recorded
            // into memory, on a device that is also trying to serve customers.
            var length = _periodTo.DayNumber - _periodFrom.DayNumber + 1;

            if (length > 366)
            {
                Fail("A period may cover at most 366 days. Narrow the dates, or read two reports.");
                _period = null;
                return;
            }

            var builder = new TradingReportBuilder(LocalStore);

            _period = await builder.BuildPeriodAsync(
                store.Id.ToString(),
                store.Name,
                _periodFrom,
                _periodTo,
                topProductCount: Math.Clamp(_topProductCount, 1, 50),
                compareWithPrevious: true,
                _cts.Token);
        }
        catch (Exception ex)
        {
            Fail($"The period report could not be built: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task OnPeriodFromChanged(ChangeEventArgs args)
    {
        var raw = args.Value?.ToString();

        if (DateOnly.TryParse(raw, CultureInfo.InvariantCulture, out var parsed))
        {
            _periodFrom = parsed;
            await RefreshPeriodAsync();
        }
        else
        {
            Fail("That date could not be read.");
        }
    }

    private async Task OnPeriodToChanged(ChangeEventArgs args)
    {
        var raw = args.Value?.ToString();

        if (DateOnly.TryParse(raw, CultureInfo.InvariantCulture, out var parsed))
        {
            _periodTo = parsed;
            await RefreshPeriodAsync();
        }
        else
        {
            Fail("That date could not be read.");
        }
    }

    /// <summary>Formats a change against the previous period for a reader.</summary>
    private static string DescribeChange(double? percent) => percent switch
    {
        // Not "0%": there is nothing to compare against, and a zero would read as "no change" —
        // which is a completely different claim.
        null => "nothing to compare against",
        > 0.05 => $"up {percent.Value:0.#}%",
        < -0.05 => $"down {Math.Abs(percent.Value):0.#}%",
        _ => "level",
    };

    private static string ChangeClass(double? percent) => percent switch
    {
        null => "rep-change--none",
        > 0.05 => "rep-change--up",
        < -0.05 => "rep-change--down",
        _ => "rep-change--flat",
    };

    private static string Day(DateTime date) =>
        date.ToString("ddd dd MMM", CultureInfo.InvariantCulture);

    /// <summary>Formats a business date for the per-day table.</summary>
    private static string DayOf(TradingDaySummary day) =>
        day.BusinessDate.ToDateTime(TimeOnly.MinValue)
            .ToString("ddd dd MMM", CultureInfo.InvariantCulture);

    private async Task OnDateChanged(ChangeEventArgs args)
    {
        var raw = args.Value?.ToString();

        if (DateOnly.TryParse(raw, CultureInfo.InvariantCulture, out var parsed))
        {
            _date = parsed;
            await RefreshAsync();
        }
        else
        {
            Fail("That date could not be read.");
        }
    }

    /// <summary>
    /// Rebuilds the report from the recorded sales for the selected day.
    /// </summary>
    private async Task RefreshAsync()
    {
        _busy = true;
        ClearMessage();

        try
        {
            var store = Session.Store;

            if (store is null)
            {
                Fail("No store is loaded. Open the checkout screen first.");
                return;
            }

            var builder = new TradingReportBuilder(LocalStore);

            _report = await builder.BuildAsync(
                store.Id.ToString(),
                store.Name,
                _date,
                isFinal: false,
                topProductCount: Math.Clamp(_topProductCount, 1, 50),
                _cts.Token);
        }
        catch (Exception ex)
        {
            Fail($"The report could not be built: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Prints the report as an X or Z reading.
    /// </summary>
    /// <param name="isFinal">
    /// True to close the day. The distinction is printed on the document, because an X-report
    /// mistaken for a Z-report would mean the day is reconciled twice from two readings.
    /// </param>
    private async Task PrintAsync(bool isFinal)
    {
        _busy = true;
        ClearMessage();

        try
        {
            var store = Session.Store;

            if (store is null)
            {
                Fail("No store is loaded.");
                return;
            }

            // Rebuilt with the requested finality so the printed document is correct even if the
            // screen was left on the mid-shift view.
            var builder = new TradingReportBuilder(LocalStore);

            var report = await builder.BuildAsync(
                store.Id.ToString(),
                store.Name,
                _date,
                isFinal,
                Math.Clamp(_topProductCount, 1, 50),
                _cts.Token);

            var printer = await Printers.RequireAsync(_cts.Token);
            var columns = printer.Transport.Capabilities.Columns;

            await printer.Transport.WriteAsync(ReportRenderer.Render(report, columns), _cts.Token);

            Succeed($"{(isFinal ? "Z-report" : "X-report")} printed for {_date:yyyy-MM-dd}.");
        }
        catch (Exception ex)
        {
            Fail($"The report did not print: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private void BackToCheckout() => Nav.NavigateTo("/");

    private static string Money(decimal amount) =>
        amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Quantity(decimal quantity) =>
        quantity == decimal.Truncate(quantity)
            ? decimal.Truncate(quantity).ToString("0", CultureInfo.InvariantCulture)
            : quantity.ToString("0.###", CultureInfo.InvariantCulture);

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
