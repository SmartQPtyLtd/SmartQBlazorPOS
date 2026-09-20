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
using Pos.Infrastructure.Sync;
using Pos.Web.Terminal;

namespace Pos.Web.Pages;

/// <summary>
/// Inter-store stock transfers: raise one, dispatch it, and book one in.
/// </summary>
/// <remarks>
/// <para>
/// The screen is organised around the two events rather than around the document, because the two
/// events happen in different shops. Raising and dispatching are the sender's job and happen here;
/// receiving is the destination's job and happens here too, but only on the till that the hub has
/// relayed the transfer to.
/// </para>
/// <para>
/// In-transit stock is in neither store's books. That is the point of the design, and it is also the
/// thing most likely to alarm someone looking at a stock level, so the screen says it outright
/// instead of leaving an operator to discover that dispatched goods have vanished.
/// </para>
/// <para>
/// The destination list comes from the hub's store directory, which is device-authenticated and
/// carries nothing but codes and names. A till must never hold the head-office token, so a raise is
/// impossible while the hub is unreachable — deliberately, and the screen says why rather than
/// offering a free-text store id that the hub would reject later.
/// </para>
/// </remarks>
public sealed partial class Transfers : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();

    [Inject]
    private StockTransferService Transfers_Service { get; set; } = default!;

    [Inject]
    private StockService Stock { get; set; } = default!;

    [Inject]
    private StoreDirectoryClient Directory { get; set; } = default!;

    [Inject]
    private NavigationManager Nav { get; set; } = default!;

    /// <summary>Outgoing transfers this store raised.</summary>
    private List<StoredStockTransfer> _outgoing = [];

    /// <summary>Transfers other stores have sent here.</summary>
    private List<StoredStockTransfer> _incoming = [];

    /// <summary>
    /// Every store the hub knows, this one included.
    /// </summary>
    /// <remarks>
    /// Kept whole so a store id on a document can be turned back into a name. Only the destinations
    /// are filtered for the picker, but a received transfer names its sender, and "01a0ac8e…" tells
    /// an operator nothing about which shop the goods came from.
    /// </remarks>
    private List<SyncStoreSummary> _directory = [];

    /// <summary>Stores stock may be sent to: the estate, less this store and any that have closed.</summary>
    private List<SyncStoreSummary> _targets = [];

    /// <summary>Catalogue, for the product picker on a new transfer.</summary>
    private List<CatalogueItem> _products = [];

    private string? _message;
    private bool _messageIsError;
    private bool _busy;

    // --- raising -----------------------------------------------------------------------

    private string _destinationStoreId = string.Empty;
    private string _raiseSearch = string.Empty;
    private string _raiseNote = string.Empty;

    /// <summary>Lines being assembled for a new transfer, keyed by product id.</summary>
    private readonly Dictionary<string, TransferDraftLine> _draft = [];

    /// <summary>
    /// Whether raising a transfer also sends it.
    /// </summary>
    /// <remarks>
    /// Sending immediately is the default because the ordinary case is a member of staff handing a box
    /// to a driver who is already waiting. A draft exists for goods being staged for a later
    /// collection, so it is offered rather than assumed.
    /// </remarks>
    private bool _dispatchImmediately = true;

    // --- receiving ---------------------------------------------------------------------

    /// <summary>The incoming transfer currently being booked in, if any.</summary>
    private StoredStockTransfer? _receiving;

    /// <summary>
    /// Counted quantity per product id, as typed.
    /// </summary>
    /// <remarks>
    /// Held as text rather than a number so a half-typed value does not become zero. An empty box
    /// means "arrived in full", which is the common case and must not require retyping every line.
    /// </remarks>
    private readonly Dictionary<string, string> _counted = [];

    private string _receiveNote = string.Empty;

    /// <summary>Products matching the raise search box.</summary>
    private IEnumerable<CatalogueItem> RaiseMatches =>
        string.IsNullOrWhiteSpace(_raiseSearch)
            ? []
            : _products
                .Where(p => Matches(p, _raiseSearch))
                .Take(8);

    /// <summary>True when the signed-in operator may move stock between stores.</summary>
    private bool CanTransfer => Session.Employee?.Can(EmployeePermissions.TransferStock) == true;

    /// <summary>True when the hub has given this terminal somewhere to send stock.</summary>
    private bool HasDestinations => _targets.Count > 0;

    /// <summary>How many lines the draft holds.</summary>
    private int DraftLineCount => _draft.Count;

    /// <summary>Total units across the draft.</summary>
    private decimal DraftQuantity => _draft.Values.Sum(l => l.Quantity);

    /// <summary>Incoming transfers still in transit, which are the only ones that can be received.</summary>
    private IEnumerable<StoredStockTransfer> AwaitingReceipt =>
        _incoming.Where(t => t.IsInTransit);

    /// <summary>Incoming transfers already booked in.</summary>
    private IEnumerable<StoredStockTransfer> ReceivedHere => _incoming.Where(t => !t.IsInTransit);

    protected override async Task OnInitializedAsync()
    {
        await ReloadAsync();
    }

    /// <summary>Re-reads the branch list and the transfers.</summary>
    private async Task ReloadAsync()
    {
        await RefreshDirectoryAsync();
        await LoadAsync();
    }

    /// <summary>
    /// Reads the estate's branch list from the hub.
    /// </summary>
    /// <remarks>
    /// Attempted on every load and every refresh rather than cached at startup: a branch opened this
    /// morning should be transferable to this afternoon, and a till that cached the list at enrolment
    /// would never learn about it.
    /// </remarks>
    private async Task RefreshDirectoryAsync()
    {
        try
        {
            var directory = await Directory.FetchAsync(_cts.Token);

            _directory = [.. directory.Stores];
            _targets = [.. directory.TransferTargets];

            var chosen = _targets.FirstOrDefault(t => t.Id == _destinationStoreId);

            if (chosen is null)
            {
                _destinationStoreId = _targets.Count > 0 ? _targets[0].Id : string.Empty;
            }
        }
        catch (Exception ex)
        {
            // FetchAsync is documented not to throw, but a page load must not die on a nicety.
            Fail($"The store list could not be read: {ex.Message}");
        }
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
            var storeId = store.Id.ToString();

            _outgoing = [.. await Transfers_Service.ListAsync(
                storeId, TransferDirection.Outgoing, 100, _cts.Token)];

            _incoming = [.. await Transfers_Service.ListAsync(
                storeId, TransferDirection.Incoming, 100, _cts.Token)];

            _products = [.. await Stock.GetCatalogueAsync(storeId, null, false, _cts.Token)];

            ClearMessage();
        }
        catch (Exception ex)
        {
            Fail($"Transfers could not be loaded: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    // --- raising -----------------------------------------------------------------------

    private void AddDraftLine(CatalogueItem item)
    {
        var id = item.Product.Id.ToString();

        if (_draft.TryGetValue(id, out var existing))
        {
            _draft[id] = existing with { Quantity = existing.Quantity + 1m };
        }
        else
        {
            _draft[id] = new TransferDraftLine(
                id,
                item.Product.Barcode,
                item.Product.Name,
                1m);
        }

        _raiseSearch = string.Empty;
        ClearMessage();
    }

    /// <summary>
    /// Changes a draft line's quantity, or drops it when the box is cleared to zero.
    /// </summary>
    /// <param name="productId">Line to change.</param>
    /// <param name="quantity">New quantity, or null when what was typed could not be read.</param>
    private void SetDraftQuantity(string productId, decimal? quantity)
    {
        if (quantity is not { } value || !_draft.TryGetValue(productId, out var line))
        {
            return;
        }

        if (value <= 0)
        {
            _draft.Remove(productId);
            return;
        }

        _draft[productId] = line with { Quantity = value };
    }

    private void RemoveDraftLine(string productId)
    {
        _draft.Remove(productId);
        ClearMessage();
    }

    private void ClearDraft()
    {
        _draft.Clear();
        _raiseNote = string.Empty;
        ClearMessage();
    }

    /// <summary>Raises the draft, and sends it when asked.</summary>
    private async Task RaiseAsync()
    {
        var store = Session.Store;
        var employee = Session.Employee;

        if (store is null || employee is null)
        {
            Fail("Sign in before moving stock.");
            return;
        }

        if (!CanTransfer)
        {
            Fail($"{employee.Name} does not have permission to transfer stock.");
            return;
        }

        if (_draft.Count == 0)
        {
            Fail("Add at least one product before raising a transfer.");
            return;
        }

        var destination = _targets.FirstOrDefault(t => t.Id == _destinationStoreId);

        if (destination is null)
        {
            Fail("Choose a destination store.");
            return;
        }

        // The hub issues store ids as strings; the domain carries them as typed ids. Parsing here
        // rather than trusting the string is what stops a directory that changed shape — or a store
        // id the hub never issued — from addressing a transfer at a store that does not exist.
        if (!Guid.TryParse(destination.Id, out var destinationId))
        {
            Fail(
                $"The hub calls the destination store '{destination.Id}', which is not a store id "
                + "this terminal can address. Refresh, and if it persists the store directory is "
                + "not in the shape this build expects.");
            return;
        }

        _busy = true;

        try
        {
            var lines = _draft.Values
                .Select(l => new StockTransferLine(l.ProductId, l.Barcode, l.Name, l.Quantity))
                .ToList();

            var transfer = await Transfers_Service.RaiseAsync(
                employee,
                store.Id,
                new StoreId(destinationId),
                store.Code,
                destination.Code,
                lines,
                _raiseNote,
                _cts.Token);

            if (!_dispatchImmediately)
            {
                // Saved, not merely built. Raising a transfer only assembles the document — the
                // dispatch is what writes it to the ledger — so reporting a draft as raised without
                // storing it would lose the work the moment the operator left the screen.
                var saved = await Transfers_Service.SaveDraftAsync(transfer, employee, _cts.Token);

                Succeed(
                    $"Saved {saved.Reference} as a draft. It moves no stock, and the destination is "
                    + "not told about it until it is sent.");
            }
            else
            {
                var dispatched = await Transfers_Service.DispatchAsync(transfer, employee, _cts.Token);

                Succeed(
                    $"Dispatched {dispatched.Reference} to {destination.Name}. "
                    + $"{Format(DraftQuantity)} units have left this store's stock and are counted in "
                    + "neither store until they are booked in.");
            }

            ClearDraft();
            await LoadAsync();
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

    /// <summary>
    /// Dispatches a transfer that was previously raised as a draft.
    /// </summary>
    private async Task DispatchAsync(StoredStockTransfer stored)
    {
        var employee = Session.Employee;

        if (employee is null)
        {
            Fail("Sign in before dispatching stock.");
            return;
        }

        _busy = true;

        try
        {
            // Rebuilt from the stored document rather than reconstructed field by field, so the
            // dispatch acts on exactly what was raised — including any counted quantities.
            var transfer = stored.ToDomain();

            var dispatched = await Transfers_Service.DispatchAsync(transfer, employee, _cts.Token);

            Succeed($"Dispatched {dispatched.Reference}.");
            await LoadAsync();
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

    // --- receiving ---------------------------------------------------------------------

    private void BeginReceiving(StoredStockTransfer stored)
    {
        _receiving = stored;
        _counted.Clear();
        _receiveNote = string.Empty;
        ClearMessage();
    }

    private void CancelReceiving()
    {
        _receiving = null;
        _counted.Clear();
        _receiveNote = string.Empty;
    }

    /// <summary>
    /// Books the transfer in.
    /// </summary>
    /// <remarks>
    /// Only lines that were actually short are sent as counted. A line left blank is taken as
    /// arrived in full, so an operator who finds everything present — the ordinary case — confirms
    /// with one click, and the only people who type numbers are the ones with a discrepancy to
    /// explain. The note is what makes a shortage actionable later.
    /// </remarks>
    private async Task ReceiveAsync()
    {
        var employee = Session.Employee;
        var stored = _receiving;

        if (employee is null || stored is null)
        {
            Fail("Sign in before booking stock in.");
            return;
        }

        if (!CanTransfer)
        {
            Fail($"{employee.Name} does not have permission to transfer stock.");
            return;
        }

        var counted = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var invalid = (string?)null;

        foreach (var line in stored.Lines)
        {
            if (!_counted.TryGetValue(line.ProductId, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (!decimal.TryParse(
                    raw,
                    NumberStyles.Number,
                    CultureInfo.CurrentCulture,
                    out var value))
            {
                invalid = $"'{raw}' is not a number, so {line.Name} was not counted.";
                break;
            }

            if (value < 0)
            {
                invalid = $"{line.Name} cannot have arrived a negative number of times.";
                break;
            }

            counted[line.ProductId] = value;
        }

        if (invalid is not null)
        {
            Fail(invalid);
            return;
        }

        _busy = true;

        try
        {
            var transfer = stored.ToDomain();

            var received = await Transfers_Service.ReceiveAsync(
                transfer,
                employee,
                counted.Count > 0 ? counted : null,
                _receiveNote,
                _cts.Token);

            // Read back through the domain object rather than off the stored record: the shortage is
            // the document's own arithmetic, and recomputing it here would be a second opinion that
            // could disagree with what was written.
            var bookedIn = received.ToDomain();

            if (bookedIn.HasDiscrepancy)
            {
                // Stated as a fact rather than a warning. The whole reason the discrepancy is
                // recorded is so that it can be looked into; dressing it up as an error would
                // imply the booking-in failed, and it did not.
                Succeed(
                    $"{bookedIn.Reference} booked in with a discrepancy of "
                    + $"{Format(bookedIn.TotalDiscrepancy)} units. The count is recorded against "
                    + $"{employee.Name}, and this store has been credited with what actually "
                    + "arrived.");
            }
            else
            {
                Succeed(
                    $"{bookedIn.Reference} booked in. "
                    + $"{Format(bookedIn.TotalCounted)} units added to this store's stock.");
            }

            CancelReceiving();
            await LoadAsync();
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

    // --- formatting --------------------------------------------------------------------

    /// <summary>Human name for a transfer's state.</summary>
    private static string StatusLabel(string status) => status switch
    {
        "Draft" => "Draft — not sent",
        "Dispatched" => "In transit",
        "Received" => "Received",
        "Cancelled" => "Cancelled",
        _ => status,
    };

    /// <summary>CSS modifier for a transfer's state.</summary>
    private static string StatusClass(string status) => status switch
    {
        "Draft" => "transfer-state--draft",
        "Dispatched" => "transfer-state--transit",
        "Received" => "transfer-state--received",
        _ => "transfer-state--cancelled",
    };

    /// <summary>
    /// Names a store from an id written on a document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Compared by identity rather than by text, and that is not fastidiousness. The hub's directory
    /// spells store ids without dashes; a transfer authored on a till carries them with, because
    /// that is how the domain type renders a GUID. Matching on the string would leave every received
    /// transfer labelled with a bare identifier instead of the shop it came from.
    /// </para>
    /// <para>
    /// Falls back to the id itself rather than to a guess. A store the hub no longer lists is worth
    /// showing as an id: it is evidence of a branch that has been removed, which is exactly the kind
    /// of thing someone looking at an old transfer needs to see.
    /// </para>
    /// </remarks>
    private string StoreLabel(string storeId)
    {
        var match = _directory.FirstOrDefault(s => StoreIdFormat.Same(s.Id, storeId));

        if (match is not null)
        {
            return $"{match.Code} — {match.Name}";
        }

        return storeId.Length > 8 ? $"{storeId[..8]}…" : storeId;
    }

    /// <summary>Units that left but did not arrive, or zero while a transfer is still in transit.</summary>
    private static decimal Shortfall(StoredStockTransfer transfer) =>
        transfer.Lines.Sum(l => l.QuantitySent - (l.QuantityCounted ?? l.QuantitySent));

    /// <summary>
    /// Reads a typed count, or null when the box was left alone.
    /// </summary>
    /// <remarks>
    /// Blank means "arrived in full" and is the ordinary case, so it must not read as zero — that
    /// would record every line of every transfer as a total loss.
    /// </remarks>
    private static decimal? ParseCount(string? raw) =>
        decimal.TryParse(
            raw,
            NumberStyles.Number,
            CultureInfo.CurrentCulture,
            out var parsed)
                ? parsed
                : null;

    private static string Format(decimal value) =>
        value.ToString("0.##", CultureInfo.CurrentCulture);

    /// <summary>
    /// Reads a number out of a bound input.
    /// </summary>
    /// <remarks>
    /// Blazor hands back whatever was typed, so an emptied box arrives as text. Returning zero for
    /// that would delete the line the moment someone cleared the box to retype it, so an unreadable
    /// value returns null and the quantity is left exactly as it was.
    /// </remarks>
    private static decimal? ToDecimal(object? value) => ParseCount(value?.ToString());

    /// <summary>When a transfer was last touched, for the list.</summary>
    private static string When(StoredStockTransfer stored)
    {
        var raw = stored.ReceivedAt ?? stored.DispatchedAt ?? stored.CreatedAt;

        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture)
            : raw;
    }

    private static bool Matches(CatalogueItem item, string search) =>
        item.Product.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || item.Product.Barcode.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (item.Product.Sku?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);

    // --- message helpers ---------------------------------------------------------------

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

    private void ClearMessage()
    {
        _message = null;
        _messageIsError = false;
    }

    private void BackToCheckout() => Nav.NavigateTo("/");

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();

        return ValueTask.CompletedTask;
    }

    /// <summary>One line being assembled for a transfer that has not been raised yet.</summary>
    /// <param name="ProductId">Product being moved.</param>
    /// <param name="Barcode">Barcode, carried onto the document for the receiving store.</param>
    /// <param name="Name">Name, carried onto the document.</param>
    /// <param name="Quantity">Units to send.</param>
    private sealed record TransferDraftLine(
        string ProductId,
        string Barcode,
        string Name,
        decimal Quantity);
}
