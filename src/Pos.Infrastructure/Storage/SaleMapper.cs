// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Globalization;
using Pos.Core.Domain;

namespace Pos.Infrastructure.Storage;

/// <summary>
/// Translates between the domain model and the persisted records.
/// </summary>
/// <remarks>
/// Kept separate from both so the domain stays free of storage concerns and the stored
/// shape can evolve without touching pricing or tax logic.
/// </remarks>
public static class SaleMapper
{
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// Projects a completed sale into its stored form.
    /// </summary>
    /// <param name="sale">The sale to persist.</param>
    /// <param name="terminalId">Identity of the till that took the sale.</param>
    /// <param name="terminalSeq">Monotonic per-terminal sequence for sync ordering.</param>
    /// <param name="catalogVersion">Catalogue version the sale was priced against, when known.</param>
    public static StoredSale ToStored(
        Sale sale,
        string terminalId,
        long terminalSeq,
        string? catalogVersion = null,
        string? shiftId = null)
    {
        ArgumentNullException.ThrowIfNull(sale);

        return new StoredSale
        {
            Id = sale.Id.Value.ToString("N"),
            StoreId = sale.StoreId.ToString(),
            Number = sale.Number.ToString(),
            TerminalId = terminalId,
            TerminalSeq = terminalSeq,
            CompletedAt = sale.CompletedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            BusinessDate = sale.BusinessDate.ToString(DateFormat, CultureInfo.InvariantCulture),

            // Recorded now, in the store's local time, so an hourly report is reproducible and
            // does not depend on where it is later run.
            LocalHour = sale.CompletedAt.ToLocalTime().Hour,
            Currency = sale.Currency,
            TaxMode = sale.TaxMode.ToString(),
            Status = sale.Status.ToString(),
            Lines = [.. sale.Lines.Select(ToStoredLine)],
            Tenders = [.. sale.Tenders.Select(ToStoredTender)],
            Subtotal = sale.Subtotal,
            TotalDiscount = sale.TotalDiscount,
            TaxTotal = sale.Tax.Tax,

            // The charged total is stored verbatim rather than recomputed from the lines
            // on read. Recomputing would mean a later rounding change silently altered a
            // historic receipt.
            Total = sale.Total,
            EmployeeId = sale.EmployeeId,
            CustomerId = sale.CustomerId,
            VoidReason = sale.VoidReason,
            CatalogVersion = catalogVersion,

            // Recorded so a cash-up finds activity by shift rather than by a time window.
            ShiftId = shiftId,
        };
    }

    private static StoredSaleLine ToStoredLine(SaleLine line) => new()
    {
        ProductId = line.ProductId.ToString(),
        Barcode = line.Barcode,
        Name = line.Name,
        Quantity = line.Quantity,
        UnitPrice = line.UnitPrice.Amount,
        TaxName = line.TaxRate.Name,
        TaxRate = line.TaxRate.Rate,
        DiscountAmount = line.DiscountAmount,
        TaxableAmount = line.TaxableAmount,
        TaxAmount = line.TaxAmount,
        Note = line.Note,
        StationId = line.StationId,
    };

    private static StoredTender ToStoredTender(Tender tender) => new()
    {
        Type = tender.Type.ToString(),
        Amount = tender.Amount.Amount,
        Tendered = tender.Tendered?.Amount,
        Reference = tender.Reference,
    };

    /// <summary>
    /// Builds the stock movements caused by a sale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Negative quantities, one movement per sold line. Weighed goods carry fractional
    /// quantities, so the movement is decimal rather than integral.
    /// </para>
    /// <para>
    /// A voided sale produces no movements: the goods never left, and appending a
    /// compensating pair would double the ledger entries for one event.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<StockMovement> ToStockMovements(
        Sale sale,
        string terminalId,
        long firstTerminalSeq)
    {
        ArgumentNullException.ThrowIfNull(sale);

        if (sale.IsVoided)
        {
            return [];
        }

        var movements = new List<StockMovement>(sale.Lines.Count);
        var seq = firstTerminalSeq;

        foreach (var line in sale.Lines)
        {
            movements.Add(new StockMovement
            {
                Id = Guid.CreateVersion7().ToString("N"),
                StoreId = sale.StoreId.ToString(),
                TerminalId = terminalId,
                TerminalSeq = seq++,
                ProductId = line.ProductId.ToString(),
                QtyDelta = -line.Quantity,
                Reason = StockMovementReason.Sale.ToString(),
                Reference = sale.Id.Value.ToString("N"),
                OccurredAt = sale.CompletedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            });
        }

        return movements;
    }

    /// <summary>
    /// Computes the derived stock level for a product from its movement ledger.
    /// </summary>
    /// <remarks>
    /// The level is never stored or synced as a value. Two terminals selling the last unit
    /// both append a decrement, and both are counted here; last-write-wins on a stored
    /// level would silently discard one of them.
    /// </remarks>
    public static decimal DeriveStockLevel(IEnumerable<StockMovement> movements, string storeId, string productId) =>
        movements
            .Where(m => m.StoreId == storeId && m.ProductId == productId)
            .Sum(m => m.QtyDelta);

    /// <summary>Formats a business date the way it is stored.</summary>
    public static string FormatBusinessDate(DateOnly date) =>
        date.ToString(DateFormat, CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------------ refunds

    /// <summary>
    /// Rebuilds a stored sale as a domain sale.
    /// </summary>
    /// <remarks>
    /// Needed so the refund policy runs against the same domain type it was written and tested
    /// against, rather than a second implementation over the stored shape that could drift from
    /// it. The stored prices and tax are used, so a policy decision is made on what was actually
    /// charged.
    /// </remarks>
    public static Sale ToDomain(StoredSale stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var lines = stored.Lines
            .Select(l => new SaleLine(
                ProductId: Guid.TryParse(l.ProductId, out var productId) ? new ProductId(productId) : ProductId.New(),
                Barcode: l.Barcode,
                Name: l.Name,
                Quantity: l.Quantity,
                UnitPrice: new Money(l.UnitPrice, stored.Currency),
                TaxRate: new TaxRate(l.TaxName, l.TaxRate),
                DiscountAmount: l.DiscountAmount,
                TaxableAmount: l.TaxableAmount,
                TaxAmount: l.TaxAmount,
                Note: l.Note,
                StationId: l.StationId))
            .ToArray();

        var tenders = stored.Tenders
            .Select(t => new Tender(
                Type: Enum.TryParse<TenderType>(t.Type, out var type) ? type : TenderType.Cash,
                Amount: new Money(t.Amount, stored.Currency),
                Tendered: t.Tendered is { } tendered ? new Money(tendered, stored.Currency) : null,
                Reference: t.Reference))
            .ToArray();

        return new Sale
        {
            Id = Guid.TryParse(stored.Id, out var saleId) ? new SaleId(saleId) : SaleId.New(),
            StoreId = Guid.TryParse(stored.StoreId, out var storeId) ? new StoreId(storeId) : StoreId.New(),
            Number = new SaleNumber(
                stored.Number.Split('-') is [var code, ..] ? code : stored.StoreId,
                DateOnly.TryParseExact(stored.BusinessDate, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? date
                    : DateOnly.FromDateTime(DateTime.Now),
                SequenceFrom(stored.Number)),
            CompletedAt = DateTimeOffset.TryParse(
                stored.CompletedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var completed)
                    ? completed
                    : DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.TryParseExact(
                stored.BusinessDate, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var businessDate)
                    ? businessDate
                    : DateOnly.FromDateTime(DateTime.Now),
            Currency = stored.Currency,
            TaxMode = Enum.TryParse<TaxMode>(stored.TaxMode, out var mode) ? mode : TaxMode.Inclusive,
            Lines = lines,
            Tenders = tenders,
            Tax = new TaxCalculation(
                Enum.TryParse<TaxMode>(stored.TaxMode, out var taxMode) ? taxMode : TaxMode.Inclusive,
                stored.Subtotal - stored.TaxTotal,
                stored.TaxTotal,
                stored.Total,
                []),
            Subtotal = stored.Subtotal,
            TotalDiscount = stored.TotalDiscount,
            Total = stored.Total,
            EmployeeId = stored.EmployeeId,
            CustomerId = stored.CustomerId,
            Status = Enum.TryParse<SaleStatus>(stored.Status, out var status) ? status : SaleStatus.Completed,
            VoidReason = stored.VoidReason,
        };
    }

    /// <summary>
    /// Recovers the numeric sequence from a receipt number such as <c>CT01-20260325-0042</c>.
    /// </summary>
    /// <remarks>
    /// Only used for display on a refund slip. A number that cannot be parsed yields zero rather
    /// than throwing, because failing to print a refund because of a cosmetic field would be a
    /// poor trade.
    /// </remarks>
    private static int SequenceFrom(string saleNumber)
    {
        var parts = saleNumber.Split('-');
        return parts.Length > 0 && int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence)
            ? sequence
            : 0;
    }

    /// <summary>
    /// Rebuilds a stored refund as a domain refund, for reprinting and policy checks.
    /// </summary>
    public static SalesReturn ToDomain(StoredReturn stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var lines = stored.Lines
            .Select(l => new ReturnLine(
                ProductId: Guid.TryParse(l.ProductId, out var productId) ? new ProductId(productId) : ProductId.New(),
                Barcode: l.Barcode,
                Name: l.Name,
                Quantity: l.Quantity,
                UnitRefund: new Money(l.UnitRefund, stored.Currency),
                TaxRate: new TaxRate(l.TaxName, l.TaxRate),
                TaxAmount: l.TaxAmount))
            .ToArray();

        var refunds = stored.Refunds
            .Select(t => new Tender(
                Type: Enum.TryParse<TenderType>(t.Type, out var type) ? type : TenderType.Cash,
                Amount: new Money(t.Amount, stored.Currency),
                Tendered: t.Tendered is { } tendered ? new Money(tendered, stored.Currency) : null,
                Reference: t.Reference))
            .ToArray();

        return new SalesReturn
        {
            Id = Guid.TryParse(stored.Id, out var returnId) ? new ReturnId(returnId) : ReturnId.New(),
            StoreId = Guid.TryParse(stored.StoreId, out var storeId) ? new StoreId(storeId) : StoreId.New(),
            OriginalSaleId = Guid.TryParse(stored.OriginalSaleId, out var saleId) ? new SaleId(saleId) : SaleId.New(),
            OriginalSaleNumber = ParseSaleNumber(stored.OriginalSaleNumber),
            Number = stored.Number,
            CompletedAt = DateTimeOffset.TryParse(
                stored.CompletedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var completed)
                    ? completed
                    : DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.TryParseExact(
                stored.BusinessDate, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? date
                    : DateOnly.FromDateTime(DateTime.Now),
            Currency = stored.Currency,
            Lines = lines,
            Refunds = refunds,
            Reason = Enum.TryParse<ReturnReason>(stored.Reason, out var reason) ? reason : ReturnReason.ChangedMind,
            Note = stored.Note,
            EmployeeId = stored.EmployeeId,
        };
    }

    private static SaleNumber ParseSaleNumber(string saleNumber)
    {
        var parts = saleNumber.Split('-');

        // Expected shape is CODE-yyyyMMdd-NNNN; anything else degrades rather than throwing.
        if (parts.Length >= 3 &&
            DateOnly.TryParseExact(parts[^2], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return new SaleNumber(
                string.Join('-', parts[..^2]),
                date,
                int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq) ? seq : 0);
        }

        return new SaleNumber(saleNumber, DateOnly.FromDateTime(DateTime.Now), 0);
    }

    /// <summary>
    /// Projects a domain refund into its stored form.
    /// </summary>
    public static StoredReturn ToStoredReturn(
        SalesReturn salesReturn,
        string terminalId,
        long terminalSeq,
        string? shiftId = null)
    {
        ArgumentNullException.ThrowIfNull(salesReturn);

        return new StoredReturn
        {
            Id = salesReturn.Id.Value.ToString("N"),
            StoreId = salesReturn.StoreId.ToString(),
            OriginalSaleId = salesReturn.OriginalSaleId.Value.ToString("N"),
            OriginalSaleNumber = salesReturn.OriginalSaleNumber.ToString(),
            Number = salesReturn.Number,
            TerminalId = terminalId,
            TerminalSeq = terminalSeq,
            CompletedAt = salesReturn.CompletedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            BusinessDate = salesReturn.BusinessDate.ToString(DateFormat, CultureInfo.InvariantCulture),

            // Recorded now, in the store's local time, so an hourly report is reproducible.
            LocalHour = salesReturn.CompletedAt.ToLocalTime().Hour,
            Currency = salesReturn.Currency,
            Lines =
            [
                .. salesReturn.Lines.Select(l => new StoredReturnLine
                {
                    ProductId = l.ProductId.ToString(),
                    Barcode = l.Barcode,
                    Name = l.Name,
                    Quantity = l.Quantity,
                    UnitRefund = l.UnitRefund.Amount,
                    TaxName = l.TaxRate.Name,
                    TaxRate = l.TaxRate.Rate,
                    TaxAmount = l.TaxAmount,
                }),
            ],
            Refunds =
            [
                .. salesReturn.Refunds.Select(t => new StoredTender
                {
                    Type = t.Type.ToString(),
                    Amount = t.Amount.Amount,
                    Tendered = t.Tendered?.Amount,
                    Reference = t.Reference,
                }),
            ],
            Reason = salesReturn.Reason.ToString(),
            Note = salesReturn.Note,
            EmployeeId = salesReturn.EmployeeId,

            // Cash refunds leave the drawer, so they must be attributable to a shift.
            ShiftId = shiftId,
            TotalRefund = salesReturn.TotalRefund,
            TaxReversed = salesReturn.TaxReversed,
        };
    }

    /// <summary>
    /// Builds the stock movements returning goods to the ledger.
    /// </summary>
    /// <remarks>
    /// Positive quantities, one per returned line. Goods that come back are back in stock, and
    /// the ledger records that as a compensating movement rather than by editing the original
    /// sale's movements — which is what keeps the stock history auditable.
    /// </remarks>
    public static IReadOnlyList<StockMovement> ToReturnStockMovements(
        SalesReturn salesReturn,
        string terminalId,
        long firstTerminalSeq)
    {
        ArgumentNullException.ThrowIfNull(salesReturn);

        var movements = new List<StockMovement>(salesReturn.Lines.Count);
        var seq = firstTerminalSeq;

        foreach (var line in salesReturn.Lines)
        {
            movements.Add(new StockMovement
            {
                Id = Guid.CreateVersion7().ToString("N"),
                StoreId = salesReturn.StoreId.ToString(),
                TerminalId = terminalId,
                TerminalSeq = seq++,
                ProductId = line.ProductId.ToString(),

                // Positive: the goods are back on the shelf.
                QtyDelta = line.Quantity,
                Reason = StockMovementReason.CustomerReturn.ToString(),
                Reference = salesReturn.Id.Value.ToString("N"),
                OccurredAt = salesReturn.CompletedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            });
        }

        return movements;
    }
}
