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
using Microsoft.AspNetCore.Components.Forms;
using Pos.Core.Domain;
using Pos.Devices.Zpl;
using Pos.Infrastructure.Checkout;
using Pos.Infrastructure.Storage;
using Pos.Web.Terminal;

namespace Pos.Web.Pages;

/// <summary>
/// Catalogue management and the stock ledger.
/// </summary>
/// <remarks>
/// <para>
/// The screen shows the derived stock level <em>and</em> how many movements produced it. A level
/// derived from two movements and one derived from two hundred look identical as a number, but they
/// mean very different things to someone deciding whether to trust it — and a product that has
/// never moved is shown as "not tracked" rather than as zero, because those are different states.
/// </para>
/// <para>
/// A physical count records the <b>difference</b> between what was counted and the derived level.
/// Recording the count itself would only produce a correct level if the previous level were already
/// right, which is the thing a count exists to question.
/// </para>
/// </remarks>
public sealed partial class Catalog : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// The label printer. Its own type, because it sends ZPL rather than ESC/POS and the two
    /// languages are not interchangeable.
    /// </summary>
    [Inject]
    private LabelPrinter Labels { get; set; } = default!;

    private List<CatalogueItem> _items = [];
    private List<StockMovement> _history = [];

    private string? _message;
    private bool _messageIsError;
    private bool _busy;
    private string _search = string.Empty;
    private bool _includeInactive;

    /// <summary>Items at or below their reorder point, worst shortfall first.</summary>
    private IReadOnlyList<CatalogueItem> _reorder = [];

    /// <summary>What the last import did, or null when none has run.</summary>
    private string? _importSummary;

    /// <summary>
    /// The catalogue as CSV, rebuilt when the catalogue is loaded.
    /// </summary>
    /// <remarks>
    /// Held rather than generated on demand because the export link is rendered, and building the file
    /// during a render would do it on every keystroke in the search box.
    /// </remarks>
    private string _exportCsv = string.Empty;

    private CatalogueItem? _selected;
    private string _editName = string.Empty;
    private decimal _editPrice;
    private bool _editActive = true;

    /// <summary>
    /// Station that prepares this product, or blank when it is handed over at the till.
    /// </summary>
    /// <remarks>
    /// Blank is the ordinary case for anything off a shelf. Assigning a station to a bottled drink
    /// would send work to a kitchen printer that has nothing to do, which is how a kitchen learns
    /// to ignore tickets.
    /// </remarks>
    private string _editStation = string.Empty;

    /// <summary>Internal stock code, blank when the shop does not use one.</summary>
    private string _editSku = string.Empty;

    /// <summary>Grouping for reporting, blank when the product is not grouped.</summary>
    private string _editCategory = string.Empty;

    /// <summary>Whether this shop counts the item's stock.</summary>
    private bool _editTracksStock = true;

    /// <summary>
    /// Reorder point, or null when nobody has set one.
    /// </summary>
    /// <remarks>
    /// Held as a nullable decimal rather than a box plus a flag, because the difference between "no
    /// reorder point" and "reorder at zero" is a decision somebody made, and the form has to be able to
    /// express both.
    /// </remarks>
    private decimal? _editReorderLevel;

    /// <summary>Stations this store prepares orders at, for the picker.</summary>
    private IReadOnlyList<PreparationStation> Stations => Session.Store?.Stations ?? [];

    /// <summary>How many shelf labels to print at once, for a row of the same product.</summary>
    private int _labelCopies = 1;

    /// <summary>True when a label printer is paired and reachable.</summary>
    private bool CanPrintLabels => Labels.IsAvailable;

    /// <summary>The label media currently loaded, for the operator to check against.</summary>
    private string _labelStockDescription = LabelStock.Default.Describe();

    private string _movementKind = "count";
    private decimal _movementQuantity;
    private string _movementReason = string.Empty;

    private string ReasonPlaceholder => _movementKind switch
    {
        "count" => "e.g. stocktake",
        "receipt" => "e.g. supplier invoice number",
        _ => "e.g. damaged in transit",
    };

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        await RefreshLabelStockAsync();
    }

    /// <summary>
    /// Prints shelf-edge labels for the selected product.
    /// </summary>
    /// <remarks>
    /// Printed on demand rather than as part of a sale. A customer buying a tin does not need the
    /// shelf edge reprinted, and a till that printed labels during checkout would slow the queue
    /// for work nobody asked for.
    /// </remarks>
    private async Task PrintShelfLabelAsync()
    {
        if (_selected is not { } item)
        {
            return;
        }

        _busy = true;

        try
        {
            var copies = Math.Max(1, _labelCopies);

            var printed = await Labels.PrintShelfLabelAsync(
                item.Product.Name,
                item.Product.Barcode,
                item.Product.UnitPrice,
                item.Product.Sku,
                copies,
                _cts.Token);

            if (printed)
            {
                Succeed(copies == 1
                    ? $"Printed a shelf label for {item.Product.Name}."
                    : $"Printed {copies} shelf labels for {item.Product.Name}.");
            }
            else
            {
                // Not an error state: a shop with no label printer is an ordinary shop, and the
                // shelf keeps its old label until someone tries again.
                Fail("The label did not print. Check a label printer is paired in device settings.");
            }

            await RefreshLabelStockAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RefreshLabelStockAsync()
    {
        var stock = await Labels.StockAsync(_cts.Token);

        _labelStockDescription = stock.Describe();
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
            _items = [.. await Stock.GetCatalogueAsync(
                store.Id.ToString(), _search, _includeInactive, _cts.Token)];

            await ReloadReorderAsync();

            // The whole catalogue, not the filtered view: an export is a backup and a template, and one
            // that silently omitted everything not matching the current search would be neither.
            _exportCsv = await Stock.ExportCsvAsync(store.Id.ToString(), _cts.Token);

            ClearMessage();
        }
        catch (Exception ex)
        {
            Fail($"The catalogue could not be loaded: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Reloads the reorder list on its own.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="LoadAsync"/> so recording a delivery can refresh what is due without
    /// re-running the search the operator typed. The list is unfiltered on purpose: a reorder point is
    /// about the shelf, not about what somebody is currently looking at.
    /// </remarks>
    private async Task ReloadReorderAsync()
    {
        var store = Session.Store;

        if (store is null)
        {
            _reorder = [];
            return;
        }

        try
        {
            _reorder = [.. await Stock.GetReorderListAsync(store.Id.ToString(), _cts.Token)];
        }
        catch (Exception ex)
        {
            TerminalLog.CatalogHistoryFailed(Logger, ex);
            _reorder = [];
        }
    }

    /// <summary>
    /// Exports the catalogue as a data URI, so the browser saves it as a file.
    /// </summary>
    /// <remarks>
    /// A data URI on an anchor with <c>download</c> rather than a JavaScript download helper: it needs
    /// no interop, works in every browser this app supports, and cannot fail at the point of clicking
    /// because there is nothing to run.
    /// </remarks>
    private string ExportDataUri
    {
        get
        {
            if (_exportCsv.Length == 0)
            {
                return "data:text/csv;charset=utf-8,";
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(_exportCsv);

            return "data:text/csv;charset=utf-8;base64," + Convert.ToBase64String(bytes);
        }
    }

    private string ExportFileName =>
        $"{Session.Store?.Code ?? "catalogue"}-catalogue.csv";

    /// <summary>
    /// Reads a chosen CSV file into the catalogue.
    /// </summary>
    /// <remarks>
    /// The file is read whole and handed to the service, which owns the matching and validation rules.
    /// Keeping the parse out of the component means it is tested without a browser, which is the only
    /// way the quoting and header rules get covered at all.
    /// </remarks>
    private async Task ImportAsync(InputFileChangeEventArgs args)
    {
        var store = Session.Store;

        if (store is null)
        {
            Fail("No store is loaded. Open the checkout screen first.");
            return;
        }

        _busy = true;
        _importSummary = null;

        try
        {
            // Bounded: a file that is not a catalogue is far more likely to be a mistake than a
            // deliberate four-megabyte import, and reading it would freeze the till.
            using var stream = args.File.OpenReadStream(maxAllowedSize: 4 * 1024 * 1024);
            using var reader = new StreamReader(stream);

            var text = await reader.ReadToEndAsync(_cts.Token);

            var outcome = await Stock.ImportCsvAsync(store.Id.ToString(), text, _cts.Token);

            _importSummary = Describe(outcome);

            await LoadAsync();

            if (outcome.Skipped > 0)
            {
                Fail($"{outcome.Skipped} row(s) were skipped. The first was: {outcome.Errors[0].Reason}");
            }
            else
            {
                Succeed($"Imported {outcome.Created + outcome.Updated} product(s).");
            }
        }
        catch (Exception ex)
        {
            Fail($"The file could not be imported: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>One line describing what an import did, including why rows were skipped.</summary>
    private static string Describe(CatalogImportOutcome outcome)
    {
        var summary =
            $"Imported: {outcome.Created} new, {outcome.Updated} updated, {outcome.Skipped} skipped.";

        if (outcome.Errors.Count == 0)
        {
            return summary;
        }

        // Line numbers, because the alternative is somebody scrolling a spreadsheet looking for what
        // the importer objected to.
        var reasons = outcome.Errors
            .Take(3)
            .Select(e => $"line {e.LineNumber}: {e.Reason}");

        return summary + " " + string.Join("; ", reasons)
            + (outcome.Errors.Count > 3 ? $"; and {outcome.Errors.Count - 3} more." : ".");
    }

    private async Task SetIncludeInactiveAsync(bool include)
    {
        _includeInactive = include;
        await LoadAsync();
    }

    private async Task SelectAsync(CatalogueItem item)
    {
        _selected = item;
        _editName = item.Product.Name;
        _editPrice = item.Product.UnitPrice;
        _editActive = item.Product.IsActive;
        _editStation = item.Product.StationId ?? string.Empty;
        _editSku = item.Product.Sku ?? string.Empty;
        _editCategory = item.Product.Category ?? string.Empty;
        _editTracksStock = item.Product.TracksStock;
        _editReorderLevel = item.Product.ReorderLevel;
        _movementQuantity = 0m;
        _movementReason = string.Empty;

        await LoadHistoryAsync();
    }

    private void CancelEdit()
    {
        _selected = null;
        _history = [];
        ClearMessage();
    }

    private async Task LoadHistoryAsync()
    {
        if (_selected is not { } item)
        {
            _history = [];
            return;
        }

        try
        {
            _history = [.. await Stock.GetHistoryAsync(
                item.Product.StoreId, item.Product.Id, _cts.Token)];
        }
        catch (Exception ex)
        {
            TerminalLog.CatalogHistoryFailed(Logger, ex);
            _history = [];
        }
    }

    private async Task SaveProductAsync()
    {
        if (_selected is not { } item)
        {
            return;
        }

        _busy = true;

        try
        {
            await Stock.UpdateLocalProductAsync(
                item.Product,
                new ProductEdit(
                    Name: _editName,
                    UnitPrice: _editPrice,
                    IsActive: _editActive,
                    StationId: _editStation,
                    Sku: _editSku,
                    Category: _editCategory,
                    TracksStock: _editTracksStock,
                    ReorderLevel: _editReorderLevel),
                _cts.Token);

            Succeed($"Saved {_editName}.");

            await LoadAsync();

            // Re-select so the panel keeps showing the product that was just edited.
            var refreshed = _items.FirstOrDefault(i => i.Product.Id == item.Product.Id);

            if (refreshed.Product is not null)
            {
                _selected = refreshed;
                _editName = refreshed.Product.Name;
                _editPrice = refreshed.Product.UnitPrice;
                _editActive = refreshed.Product.IsActive;
                _editStation = refreshed.Product.StationId ?? string.Empty;
                _editSku = refreshed.Product.Sku ?? string.Empty;
                _editCategory = refreshed.Product.Category ?? string.Empty;
                _editTracksStock = refreshed.Product.TracksStock;
                _editReorderLevel = refreshed.Product.ReorderLevel;
            }
            else
            {
                // It fell out of the filter, e.g. it was just withdrawn while withdrawn items are
                // hidden. Closing the panel is less confusing than leaving a stale one open.
                CancelEdit();
            }
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

    private async Task RecordMovementAsync()
    {
        if (_selected is not { } item)
        {
            return;
        }

        _busy = true;

        try
        {
            var employee = Session.Employee?.Name ?? "unknown operator";

            var movement = _movementKind switch
            {
                "count" => await Stock.RecordCountAsync(
                    item.Product.Id, _movementQuantity, employee, _movementReason, _cts.Token),

                "receipt" => await Stock.RecordGoodsReceiptAsync(
                    item.Product.Id, _movementQuantity, _movementReason, _cts.Token),

                _ => await Stock.RecordShrinkageAsync(
                    item.Product.Id, _movementQuantity, _movementReason, _cts.Token),
            };

            var direction = movement.QtyDelta > 0m ? "added" : "removed";
            Succeed($"Recorded: {Quantity(Math.Abs(movement.QtyDelta))} {direction}.");

            _movementQuantity = 0m;
            _movementReason = string.Empty;

            await LoadAsync();

            var refreshed = _items.FirstOrDefault(i => i.Product.Id == item.Product.Id);
            if (refreshed.Product is not null)
            {
                _selected = refreshed;
            }

            await LoadHistoryAsync();
        }
        catch (Exception ex)
        {
            // Policy refusals are expected outcomes, not faults, so they read as a message.
            Fail(ex.Message);
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

    private static string When(string occurredAt) =>
        DateTimeOffset.TryParse(occurredAt, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var at)
                ? at.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture)
                : occurredAt;

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
