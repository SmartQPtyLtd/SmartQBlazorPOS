// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

namespace Pos.Core.Domain;

/// <summary>
/// An in-progress sale at the till.
/// </summary>
/// <remarks>
/// <para>
/// Pure domain logic with no browser, storage, or UI dependencies, so the whole
/// checkout calculation is directly unit-testable. This is the single most valuable
/// property of the design: if the cart maths is wrong, everything downstream is wrong.
/// </para>
/// <para>
/// Totals are computed on demand rather than cached, so a cart can never render a
/// stale total after an edit. Carts are small (tens of lines), so the cost is
/// irrelevant next to the risk of a wrong number on a receipt.
/// </para>
/// </remarks>
public sealed class Cart
{
    private readonly List<CartLine> _lines = [];

    public Cart(StoreId storeId, string currency, TaxMode taxMode)
    {
        _ = new Money(0m, currency); // validates the currency code
        StoreId = storeId;
        Currency = currency.ToUpperInvariant();
        TaxMode = taxMode;
    }

    public StoreId StoreId { get; }

    public string Currency { get; }

    public TaxMode TaxMode { get; }

    /// <summary>Order-level discount applied across all lines.</summary>
    public Discount? OrderDiscount { get; set; }

    /// <summary>Optional customer reference for this sale.</summary>
    public string? CustomerId { get; set; }

    public IReadOnlyList<CartLine> Lines => _lines;

    public bool IsEmpty => _lines.Count == 0;

    /// <summary>Total unit count across all lines, including fractional weights.</summary>
    public decimal TotalQuantity => _lines.Sum(l => l.Quantity);

    /// <summary>
    /// Adds a product to the cart, merging into an existing line when the product
    /// and unit price match.
    /// </summary>
    /// <remarks>
    /// Merging on price as well as product is deliberate: scanning the same item at a
    /// changed price must produce two lines, otherwise the receipt would misstate
    /// what was actually charged and the sale could not be reconciled.
    /// </remarks>
    /// <returns>The line that was created or incremented.</returns>
    public CartLine Add(
        ProductId productId,
        string barcode,
        string name,
        TaxRate taxRate,
        Money unitPrice,
        decimal quantity = 1m,
        string? note = null,
        string? stationId = null)
    {
        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        }

        if (!string.Equals(unitPrice.Currency, Currency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Product priced in {unitPrice.Currency} cannot be added to a {Currency} cart.");
        }

        // The station is part of the identity of a line. Two cappuccinos for the bar and one for
        // the counter are different work, and merging them onto one line would send the wrong
        // quantity to the wrong place.
        var existing = _lines.FirstOrDefault(l =>
            l.ProductId == productId
            && l.UnitPrice.Amount == unitPrice.Amount
            && l.Note == note
            && string.Equals(l.StationId, stationId, StringComparison.OrdinalIgnoreCase)
            && l.LineDiscount is null);

        if (existing is not null)
        {
            existing.Quantity += quantity;
            return existing;
        }

        var line = new CartLine
        {
            ProductId = productId,
            Barcode = barcode,
            Name = name,
            TaxRate = taxRate,
            UnitPrice = unitPrice,
            Quantity = quantity,
            Note = note,
            StationId = stationId,
        };

        _lines.Add(line);
        return line;
    }

    /// <summary>Convenience overload for adding a catalogue product as a single unit.</summary>
    public CartLine AddProduct(Product product, decimal quantity = 1m)
    {
        ArgumentNullException.ThrowIfNull(product);
        return Add(
            product.Id,
            product.Barcode,
            product.Name,
            product.TaxRate,
            product.UnitPrice,
            quantity,
            note: null,
            stationId: product.StationId);
    }

    /// <summary>Removes a line entirely (a void).</summary>
    public bool Remove(CartLine line) => _lines.Remove(line);

    /// <summary>Empties the cart and clears the order-level discount.</summary>
    public void Clear()
    {
        _lines.Clear();
        OrderDiscount = null;
        CustomerId = null;
    }

    /// <summary>
    /// Computes the full totals, allocating any order-level discount across the lines.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order discount is spread <b>proportionally to each line's net amount</b>
    /// and the rounding remainder is dropped onto the largest line. That keeps two
    /// invariants that matter on a real receipt:
    /// </para>
    /// <list type="number">
    /// <item>The allocated shares sum <b>exactly</b> to the order discount — no
    /// half-cent vanishes or appears.</item>
    /// <item>Tax is assessed per rate group on the post-discount amounts, which is
    /// how tax authorities assess a discounted invoice.</item>
    /// </list>
    /// <para>
    /// Per-line tax is then apportioned out of each group's tax so the printed lines
    /// reconcile to the printed tax total to the cent.
    /// </para>
    /// </remarks>
    public CartTotals CalculateTotals()
    {
        if (_lines.Count == 0)
        {
            return CartTotals.Empty(Currency, TaxMode);
        }

        var lineNets = _lines.Select(l => l.NetAmount).ToArray();
        var subtotal = CartLine.Round(lineNets.Sum());

        var orderDiscount = OrderDiscount is { } discount && discount.IsValid
            ? CartLine.Round(Math.Clamp(discount.AmountFor(subtotal), 0m, subtotal))
            : 0m;

        var allocations = AllocateDiscount(lineNets, subtotal, orderDiscount);
        var taxableAmounts = new decimal[_lines.Count];
        for (var i = 0; i < _lines.Count; i++)
        {
            taxableAmounts[i] = CartLine.Round(lineNets[i] - allocations[i]);
        }

        var tax = TaxEngine.Calculate(
            taxableAmounts.Select((amount, i) => new TaxableAmount(_lines[i].TaxRate, amount)),
            TaxMode,
            Currency);

        var lineTotals = ApportionLineTax(taxableAmounts, allocations, tax);

        return new CartTotals(
            Mode: TaxMode,
            Currency: Currency,
            LineTotals: lineTotals,
            Tax: tax,
            NetBeforeOrderDiscount: subtotal,
            OrderDiscount: orderDiscount,
            Total: tax.Gross);
    }

    /// <summary>
    /// Distributes <paramref name="orderDiscount"/> across lines in proportion to
    /// their net, forcing the shares to sum exactly to the discount.
    /// </summary>
    private static decimal[] AllocateDiscount(decimal[] lineNets, decimal subtotal, decimal orderDiscount)
    {
        var allocations = new decimal[lineNets.Length];

        if (orderDiscount == 0m || subtotal <= 0m)
        {
            return allocations;
        }

        var runningTotal = 0m;
        var largestIndex = 0;

        for (var i = 0; i < lineNets.Length; i++)
        {
            if (lineNets[i] > lineNets[largestIndex])
            {
                largestIndex = i;
            }

            var share = CartLine.Round(orderDiscount * (lineNets[i] / subtotal));
            allocations[i] = share;
            runningTotal += share;
        }

        // Absorb the rounding remainder on the largest line so the sum is exact.
        var remainder = CartLine.Round(orderDiscount - runningTotal);
        if (remainder != 0m)
        {
            allocations[largestIndex] += remainder;
        }

        return allocations;
    }

    /// <summary>
    /// Derives per-line tax by apportioning each rate group's tax across its lines.
    /// </summary>
    /// <remarks>
    /// Summing the result always equals the basket tax exactly, because the last line
    /// in each group takes whatever is left rather than its own rounded share.
    /// </remarks>
    private List<CartLineTotal> ApportionLineTax(
        decimal[] taxableAmounts,
        decimal[] allocations,
        TaxCalculation tax)
    {
        var result = new List<CartLineTotal>(_lines.Count);
        var taxByRate = tax.Components.ToDictionary(c => c.Rate, c => c.Tax);

        // Group line indices by rate so each group can be apportioned independently.
        var groups = new Dictionary<TaxRate, List<int>>();
        for (var i = 0; i < _lines.Count; i++)
        {
            var rate = _lines[i].TaxRate;
            if (!groups.TryGetValue(rate, out var indices))
            {
                indices = [];
                groups[rate] = indices;
            }

            indices.Add(i);
        }

        var lineTax = new decimal[_lines.Count];

        foreach (var (rate, indices) in groups)
        {
            var groupTax = taxByRate.TryGetValue(rate, out var t) ? t : 0m;
            var groupAmount = indices.Sum(i => taxableAmounts[i]);

            if (groupTax == 0m || groupAmount == 0m)
            {
                continue;
            }

            var apportioned = 0m;
            for (var position = 0; position < indices.Count; position++)
            {
                var index = indices[position];

                // Final line in the group absorbs the remainder.
                var share = position == indices.Count - 1
                    ? CartLine.Round(groupTax - apportioned)
                    : CartLine.Round(groupTax * (taxableAmounts[index] / groupAmount));

                lineTax[index] = share;
                apportioned += share;
            }
        }

        for (var i = 0; i < _lines.Count; i++)
        {
            result.Add(new CartLineTotal(_lines[i], allocations[i], taxableAmounts[i], lineTax[i]));
        }

        return result;
    }
}
