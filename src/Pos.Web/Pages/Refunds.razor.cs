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
using Pos.Core.Domain;
using Pos.Devices.EscPos;
using Pos.Infrastructure.Checkout;
using Pos.Infrastructure.Storage;
using Pos.Web.Terminal;

namespace Pos.Web.Pages;

/// <summary>
/// Refunds: find the sale, choose what is coming back, refund it, print the slip.
/// </summary>
/// <remarks>
/// <para>
/// The flow follows what actually happens at a counter: the customer presents a receipt, the
/// operator types the number, the items are chosen, and the money goes back. A customer who has
/// lost the slip can be served from the day's list rather than being turned away.
/// </para>
/// <para>
/// The screen deliberately cannot bypass the refund policy. What may be returned comes from the
/// service, which recomputes it from storage, so this page can offer only what is genuinely
/// refundable.
/// </para>
/// </remarks>
public sealed partial class Refunds : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Quantities chosen for return, keyed by barcode.</summary>
    private readonly Dictionary<string, decimal> _quantities = new(StringComparer.Ordinal);

    private ElementReference _lookupInput;

    private string _lookupText = string.Empty;
    private string? _message;
    private bool _messageIsError;
    private bool _busy;

    private StoredSale? _sale;
    private RefundableState? _state;
    private IReadOnlyList<StoredSale> _recentSales = [];
    private decimal _alreadyRefunded;

    private ReturnReason _reason = ReturnReason.ChangedMind;
    private TenderType _refundMethod = TenderType.Cash;
    private string? _note;
    private string? _tenderWarning;

    private List<ReturnLine> _refundLines = [];

    private decimal _refundTotal => _refundLines.Sum(l => l.LineRefund);

    private decimal _refundTax => CartLine.Round(_refundLines.Sum(l => l.TaxAmount));

    protected override async Task OnInitializedAsync()
    {
        await LoadRecentAsync();
    }

    // -------------------------------------------------------------------------- lookup

    private async Task OnLookupKeyDown(KeyboardEventArgs args)
    {
        if (string.Equals(args.Key, "Enter", StringComparison.Ordinal))
        {
            await LookupAsync();
        }
    }

    private async Task LookupAsync()
    {
        await RunAsync(async () =>
        {
            var number = _lookupText.Trim();

            if (number.Length == 0)
            {
                Fail("Type a receipt number, or choose a sale from the list.");
                return;
            }

            var sale = await LocalStore.FindSaleByNumberAsync(number, _cts.Token);

            if (sale is null)
            {
                Fail($"No sale on this terminal has the receipt number '{number}'.");
                return;
            }

            await SelectSaleAsync(sale);
        });
    }

    private async Task SelectSaleAsync(StoredSale sale)
    {
        await RunAsync(async () =>
        {
            _sale = sale;
            _quantities.Clear();
            _refundLines = [];
            _tenderWarning = null;
            _lookupText = sale.Number;

            var prepared = await Returns.PrepareRefundAsync(sale.Id, _cts.Token);

            if (prepared is null)
            {
                Fail("That sale could not be loaded.");
                return;
            }

            _state = prepared.Value.State;
            _alreadyRefunded = prepared.Value.PreviousReturns.Sum(r => r.TotalRefund);

            // Default the refund method to how the customer actually paid, so the common case
            // needs no decision and a mismatch is a deliberate act.
            _refundMethod = sale.Tenders.Any(t =>
                string.Equals(t.Type, nameof(TenderType.ExternalCard), StringComparison.Ordinal))
                    ? TenderType.ExternalCard
                    : TenderType.Cash;

            if (prepared.Value.State.IsFullyReturned)
            {
                Succeed("Everything on this sale has already been refunded.");
            }
            else
            {
                Succeed($"Loaded {sale.Number}. Choose what is coming back.");
            }
        });
    }

    private async Task LoadRecentAsync()
    {
        var store = Session.Store;

        if (store is null)
        {
            return;
        }

        try
        {
            _recentSales = await LocalStore.FindRecentSalesAsync(
                store.Id.ToString(),
                DateOnly.FromDateTime(DateTime.Now),
                25,
                _cts.Token);
        }
        catch (Exception ex)
        {
            TerminalLog.RefundLookupFailed(Logger, ex);
        }
    }

    // ----------------------------------------------------------------------- selection

    private decimal ReturnQuantityFor(string barcode) =>
        _quantities.TryGetValue(barcode, out var quantity) ? quantity : 0m;

    private async Task OnQuantityChangedAsync(StoredSaleLine line, ChangeEventArgs args)
    {
        var raw = args.Value?.ToString();

        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var quantity) ||
            quantity <= 0m)
        {
            // Clearing the field means "not returning this line", which is not an error.
            _quantities.Remove(line.Barcode);
            await RecalculateAsync();
            return;
        }

        if (_state is null || _sale is null)
        {
            return;
        }

        var refundable = _state.RefundableQuantity(ToDomainLine(line, _sale.Currency));

        if (quantity > refundable)
        {
            // Clamped rather than accepted: offering a quantity the policy will reject would
            // waste the operator's time at the counter.
            Fail($"Only {Quantity(refundable)} of '{line.Name}' can be refunded.");
            _quantities[line.Barcode] = refundable;
        }
        else
        {
            _quantities[line.Barcode] = quantity;
            ClearMessage();
        }

        await RecalculateAsync();
    }

    /// <summary>
    /// Rebuilds the proposed refund and previews it, including any tender warning.
    /// </summary>
    /// <remarks>
    /// Previewed using the same policy call the service will make, so what the screen shows is
    /// what will be recorded — including the warning, which the operator sees before committing
    /// rather than after.
    /// </remarks>
    private async Task RecalculateAsync()
    {
        _refundLines = [];
        _tenderWarning = null;

        if (_sale is null || _state is null || _quantities.Count == 0)
        {
            return;
        }

        try
        {
            var sale = _state.Sale;

            var selections = _quantities
                .Where(kv => kv.Value > 0m)
                .Select(kv => (
                    Line: sale.Lines.FirstOrDefault(l =>
                        string.Equals(l.Barcode, kv.Key, StringComparison.Ordinal)),
                    Quantity: kv.Value))
                .Where(x => x.Line != default)
                .ToArray();

            _refundLines = RefundPolicy.BuildLines(sale, selections, sale.Currency);

            // A dry-run of the same validation the service performs, so an impossible refund is
            // caught before the operator commits to it.
            RefundPolicy.Validate(sale, _refundLines);

            var preview = new SalesReturn
            {
                Id = ReturnId.New(),
                StoreId = sale.StoreId,
                OriginalSaleId = sale.Id,
                OriginalSaleNumber = sale.Number,
                Number = "preview",
                CompletedAt = DateTimeOffset.UtcNow,
                BusinessDate = sale.BusinessDate,
                Currency = sale.Currency,
                Lines = _refundLines,
                Refunds = [new Tender(_refundMethod, new Money(_refundTotal, sale.Currency))],
                Reason = _reason,
            };

            var warnings = RefundPolicy.FindTenderMismatches(sale, preview);
            _tenderWarning = warnings.Count > 0 ? string.Join(" ", warnings) : null;

            ClearMessage();
        }
        catch (InvalidOperationException ex)
        {
            // A policy failure is shown while the operator is still choosing, not thrown at them
            // when they press the button.
            _refundLines = [];
            Fail(ex.Message);
        }

        await Task.CompletedTask;
    }

    // -------------------------------------------------------------------------- commit

    private async Task CompleteRefundAsync()
    {
        await RunAsync(async () =>
        {
            if (_sale is null || _quantities.Count == 0)
            {
                Fail("Choose at least one item to refund.");
                return;
            }

            var selections = _quantities
                .Where(kv => kv.Value > 0m)
                .Select(kv => (kv.Key, kv.Value))
                .ToArray();

            var completed = await Returns.RecordReturnAsync(
                _sale.Id,
                selections,
                _reason,
                _refundMethod,

                // Attributed for the same reason a sale is. A cash refund leaves the drawer, so a
                // shift that could not attribute it would report a false variance on whichever
                // operator happened to be signed in.
                employeeId: Session.Employee?.Id.ToString(),
                note: string.IsNullOrWhiteSpace(_note) ? null : _note,
                businessDate: DateOnly.FromDateTime(DateTime.Now),
                shiftId: Session.Shift?.Id,

                // The authority is checked, not merely recorded.
                authorisedBy: Session.Employee,
                ct: _cts.Token);

            await PrintSlipAsync(completed);

            // The warnings are surfaced after the commit as well as before, because the refund is
            // already recorded and the operator still needs to know.
            var summary =
                $"Refunded {Money(completed.Return.TotalRefund)} on {completed.Stored.Number}.";

            if (completed.TenderWarnings.Count > 0)
            {
                Fail($"{summary} Note: {string.Join(" ", completed.TenderWarnings)}");
            }
            else
            {
                Succeed(summary);
            }

            Reset();
            await LoadRecentAsync();
        });
    }

    private async Task PrintSlipAsync(CompletedReturn completed)
    {
        if (Session.Store is not { } store)
        {
            return;
        }

        try
        {
            var printer = await Printers.RequireAsync(_cts.Token);

            await printer.Transport.WriteAsync(
                ReceiptRenderer.RenderRefund(store, completed.Return),
                _cts.Token);
        }
        catch (Exception ex)
        {
            // The money is already recorded, so a printer fault must not read as a failed refund.
            TerminalLog.RefundSlipFailed(Logger, ex);
            Fail($"Refund recorded, but the slip did not print ({ex.Message}). The refund stands.");
        }
    }

    private void Reset()
    {
        _sale = null;
        _state = null;
        _quantities.Clear();
        _refundLines = [];
        _tenderWarning = null;
        _lookupText = string.Empty;
        _note = null;
        _reason = ReturnReason.ChangedMind;
    }

    // ------------------------------------------------------------------------- helpers

    /// <summary>
    /// Rebuilds a stored line as a domain line.
    /// </summary>
    /// <remarks>
    /// The policy matches on barcode and quantity, and takes the currency from the sale rather
    /// than from the line, so only those fields need to be faithful here. The currency passed in
    /// comes from the sale itself, never a hard-coded default — a store trading in another
    /// currency must still refund correctly.
    /// </remarks>
    private static SaleLine ToDomainLine(StoredSaleLine line, string currency) => new(
        ProductId: ProductId.New(),
        Barcode: line.Barcode,
        Name: line.Name,
        Quantity: line.Quantity,
        UnitPrice: new Money(line.UnitPrice, currency),
        TaxRate: new TaxRate(line.TaxName, line.TaxRate),
        DiscountAmount: line.DiscountAmount,
        TaxableAmount: line.TaxableAmount,
        TaxAmount: line.TaxAmount,
        Note: line.Note);

    /// <summary>The sale's currency, or a neutral fallback if the sale is not loaded.</summary>
    private string Currency => _sale?.Currency ?? "ZAR";

    private static string Describe(ReturnReason reason) => reason switch
    {
        ReturnReason.ChangedMind => "Changed mind",
        ReturnReason.Faulty => "Faulty",
        ReturnReason.WrongItem => "Wrong item",
        ReturnReason.NotAsDescribed => "Not as described",
        ReturnReason.Damaged => "Damaged",
        ReturnReason.Expired => "Out of date",
        ReturnReason.Goodwill => "Goodwill",
        _ => reason.ToString(),
    };

    private static string Money(decimal amount) =>
        amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Quantity(decimal quantity) =>
        quantity == decimal.Truncate(quantity)
            ? decimal.Truncate(quantity).ToString("0", CultureInfo.InvariantCulture)
            : quantity.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Time(string completedAt) =>
        DateTimeOffset.TryParse(completedAt, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                : completedAt;

    private void BackToCheckout() => Nav.NavigateTo("/");

    private async Task RunAsync(Func<Task> action)
    {
        _busy = true;

        try
        {
            await action();
        }
        catch (InvalidOperationException ex)
        {
            // Policy refusals are expected outcomes of an attempted refund, not faults.
            Fail(ex.Message);
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
}
