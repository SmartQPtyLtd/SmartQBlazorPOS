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
/// One line being returned.
/// </summary>
/// <param name="ProductId">Product coming back.</param>
/// <param name="Barcode">Barcode as sold.</param>
/// <param name="Name">Product name as sold.</param>
/// <param name="Quantity">Units returned. Must not exceed what remains refundable.</param>
/// <param name="UnitRefund">Amount refunded per unit.</param>
/// <param name="TaxRate">Tax rate that applied to the original sale.</param>
/// <param name="TaxAmount">Tax being reversed on this line.</param>
public readonly record struct ReturnLine(
    ProductId ProductId,
    string Barcode,
    string Name,
    decimal Quantity,
    Money UnitRefund,
    TaxRate TaxRate,
    decimal TaxAmount)
{
    /// <summary>Total refunded for this line, before tax is considered separately.</summary>
    public decimal LineRefund => CartLine.Round(Quantity * UnitRefund.Amount);

    /// <summary>Refund excluding the tax being reversed.</summary>
    public decimal NetRefund => CartLine.Round(LineRefund - TaxAmount);
}

/// <summary>
/// Why goods came back.
/// </summary>
/// <remarks>
/// Recorded on every return because the reason is what makes a returns report actionable. A
/// spike in "faulty" means a supplier problem; a spike in "changed mind" means a merchandising
/// one.
/// </remarks>
public enum ReturnReason
{
    /// <summary>Customer changed their mind.</summary>
    ChangedMind = 0,

    /// <summary>Item was faulty.</summary>
    Faulty = 1,

    /// <summary>Wrong item or size.</summary>
    WrongItem = 2,

    /// <summary>Not as described.</summary>
    NotAsDescribed = 3,

    /// <summary>Damaged in transit or on the shelf.</summary>
    Damaged = 4,

    /// <summary>Goods were out of date.</summary>
    Expired = 5,

    /// <summary>Goodwill gesture, not covered by the policy above.</summary>
    Goodwill = 6,
}

/// <summary>
/// A refund against a completed sale.
/// </summary>
/// <remarks>
/// <para>
/// A return is its own record rather than a modification of the sale. The sale is a historical
/// fact — the customer paid, and holds a receipt — and rewriting it would destroy the audit
/// trail and unbalance the day's takings. A return is a separate, append-only financial event
/// that happens to reference the sale.
/// </para>
/// <para>
/// Returns are recorded per line so a partial return of a multi-item basket is expressible,
/// which is the common case in practice.
/// </para>
/// </remarks>
public sealed class SalesReturn
{
    public required ReturnId Id { get; init; }

    public required StoreId StoreId { get; init; }

    /// <summary>The sale the goods came from.</summary>
    public required SaleId OriginalSaleId { get; init; }

    /// <summary>Receipt number of the original sale, printed on the refund document.</summary>
    public required SaleNumber OriginalSaleNumber { get; init; }

    /// <summary>Human-readable refund number, allocated like a sale number.</summary>
    public required string Number { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public required DateOnly BusinessDate { get; init; }

    public required string Currency { get; init; }

    public required IReadOnlyList<ReturnLine> Lines { get; init; }

    /// <summary>
    /// How the money went back.
    /// </summary>
    /// <remarks>
    /// Normally mirrors the original tender. Refunding cash for a card sale is a fraud vector,
    /// so <see cref="RefundPolicy"/> flags the mismatch rather than silently allowing it.
    /// </remarks>
    public required IReadOnlyList<Tender> Refunds { get; init; }

    public required ReturnReason Reason { get; init; }

    /// <summary>Optional note, e.g. a fault description.</summary>
    public string? Note { get; init; }

    /// <summary>Operator who processed the return, for audit.</summary>
    public string? EmployeeId { get; init; }

    /// <summary>Tax being reversed. This reduces the tax owed onward.</summary>
    public decimal TaxReversed => CartLine.Round(Lines.Sum(l => l.TaxAmount));

    /// <summary>Total refunded to the customer.</summary>
    public decimal TotalRefund => CartLine.Round(Lines.Sum(l => l.LineRefund));

    /// <summary>Refund excluding the tax being reversed.</summary>
    public decimal NetRefund => CartLine.Round(TotalRefund - TaxReversed);

    /// <summary>Total units going back into stock.</summary>
    public decimal TotalQuantity => Lines.Sum(l => l.Quantity);

    /// <summary>True when this is the whole sale coming back.</summary>
    public bool IsFullReturn(decimal totalSoldQuantity) => TotalQuantity >= totalSoldQuantity;
}

/// <summary>Strongly-typed return identifier.</summary>
public readonly record struct ReturnId(Guid Value)
{
    public static ReturnId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// What remains refundable on a sale.
/// </summary>
/// <param name="Sale">The original sale.</param>
/// <param name="AlreadyReturned">
/// Units already returned, keyed by barcode.
/// </param>
public sealed record RefundableState(Sale Sale, IReadOnlyDictionary<string, decimal> AlreadyReturned)
{
    /// <summary>
    /// How many units of a line may still be returned.
    /// </summary>
    /// <remarks>
    /// Keyed on barcode rather than product id: the same product can appear on two lines at
    /// different prices, and a customer returning one of them should not be blocked by the
    /// other. The remainder is floored at zero so a data anomaly cannot produce a negative
    /// allowance and an accidental extra refund.
    /// </remarks>
    public decimal RefundableQuantity(SaleLine line)
    {
        ArgumentNullException.ThrowIfNull(AlreadyReturned);

        AlreadyReturned.TryGetValue(line.Barcode, out var returned);
        var remaining = line.Quantity - returned;

        return remaining < 0m ? 0m : remaining;
    }

    /// <summary>True when nothing on the sale can be returned any more.</summary>
    public bool IsFullyReturned => Sale.Lines.All(l => RefundableQuantity(l) <= 0m);
}

/// <summary>
/// Validates a proposed return against the original sale.
/// </summary>
/// <remarks>
/// The single most abused operation at a till is the refund, so the rules are enforced in the
/// domain rather than left to the UI. Nothing here trusts the caller.
/// </remarks>
public static class RefundPolicy
{
    /// <summary>
    /// Works out what may still be returned from a sale.
    /// </summary>
    /// <param name="sale">The sale being refunded against.</param>
    /// <param name="previousReturns">Any returns already recorded against it.</param>
    public static RefundableState GetRefundableState(Sale sale, IEnumerable<SalesReturn>? previousReturns = null)
    {
        ArgumentNullException.ThrowIfNull(sale);

        var returned = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var line in previousReturns?.SelectMany(r => r.Lines) ?? [])
        {
            returned[line.Barcode] = returned.TryGetValue(line.Barcode, out var running)
                ? running + line.Quantity
                : line.Quantity;
        }

        return new RefundableState(sale, returned);
    }

    /// <summary>
    /// Checks a proposed return, throwing with a reason a cashier can act on.
    /// </summary>
    /// <exception cref="InvalidOperationException">The return is not permitted.</exception>
    public static void Validate(
        Sale sale,
        IReadOnlyList<ReturnLine> proposedLines,
        IEnumerable<SalesReturn>? previousReturns = null)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(proposedLines);

        if (sale.IsVoided)
        {
            throw new InvalidOperationException(
                "This sale was voided, so there is nothing to refund. Voided sales took no money.");
        }

        if (proposedLines.Count == 0)
        {
            throw new InvalidOperationException("A return must contain at least one line.");
        }

        var state = GetRefundableState(sale, previousReturns);

        // Aggregate the proposal by barcode first: two lines of the same product must be checked
        // together, or each could individually pass while their sum exceeds what was bought.
        var proposedByBarcode = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var line in proposedLines)
        {
            if (line.Quantity <= 0m)
            {
                throw new InvalidOperationException(
                    $"The quantity for '{line.Name}' must be greater than zero.");
            }

            proposedByBarcode[line.Barcode] = proposedByBarcode.TryGetValue(line.Barcode, out var running)
                ? running + line.Quantity
                : line.Quantity;
        }

        foreach (var (barcode, proposed) in proposedByBarcode)
        {
            var sold = sale.Lines
                .Where(l => string.Equals(l.Barcode, barcode, StringComparison.Ordinal))
                .Sum(l => l.Quantity);

            var alreadyReturned = state.AlreadyReturned.TryGetValue(barcode, out var returned) ? returned : 0m;
            var refundable = sold - alreadyReturned;

            if (refundable < 0m)
            {
                refundable = 0m;
            }

            if (proposed > refundable)
            {
                var name = proposedLines.First(l => string.Equals(l.Barcode, barcode, StringComparison.Ordinal)).Name;

                throw new InvalidOperationException(
                    $"Cannot refund {proposed:0.###} x '{name}': {sold:0.###} were sold" +
                    (alreadyReturned > 0m ? $", {alreadyReturned:0.###} already refunded" : string.Empty) +
                    $", leaving {refundable:0.###} refundable.");
            }
        }
    }

    /// <summary>
    /// Checks that a return is refunded the same way it was paid.
    /// </summary>
    /// <remarks>
    /// Returns the mismatches rather than throwing, because there are legitimate exceptions — a
    /// card terminal that is down, a customer without the card — but every one of them needs to
    /// be a deliberate, visible decision. Paying cash for a card sale is a classic fraud route,
    /// so it must never happen silently.
    /// </remarks>
    public static IReadOnlyList<string> FindTenderMismatches(Sale sale, SalesReturn proposed)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(proposed);

        var warnings = new List<string>();

        var paidByType = sale.Tenders
            .GroupBy(t => t.Type)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Amount.Amount));

        var refundedByType = proposed.Refunds
            .GroupBy(t => t.Type)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Amount.Amount));

        foreach (var (type, refunded) in refundedByType)
        {
            if (!paidByType.TryGetValue(type, out var paid))
            {
                // Money going back by a method the customer never used. Cash for a card sale is
                // the case that matters.
                warnings.Add(
                    $"Refunding {refunded:0.00} by {type}, but this sale was not paid that way.");
                continue;
            }

            // Over-refunding a single method is allowed when the sale was split, since the
            // customer is entitled to the total either way; only the aggregate is checked.
            if (refunded > paid && refundedByType.Count == 1 && paidByType.Count == 1)
            {
                warnings.Add(
                    $"Refunding {refunded:0.00} by {type}, but only {paid:0.00} was paid that way.");
            }
        }

        return warnings;
    }

    /// <summary>
    /// Checks that a return does not hand back more than the sale took.
    /// </summary>
    /// <exception cref="InvalidOperationException">The refund exceeds what was paid.</exception>
    public static void ValidateRefundAmount(Sale sale, SalesReturn proposed, IEnumerable<SalesReturn>? previousReturns = null)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(proposed);

        var alreadyRefunded = previousReturns?.Sum(r => r.TotalRefund) ?? 0m;
        var totalAfter = CartLine.Round(alreadyRefunded + proposed.TotalRefund);

        if (totalAfter > sale.Total)
        {
            throw new InvalidOperationException(
                $"Refunds against this sale would total {totalAfter:0.00}, " +
                $"but only {sale.Total:0.00} was paid.");
        }

        var refundedTotal = proposed.Refunds.Sum(t => t.Amount.Amount);

        if (CartLine.Round(refundedTotal - proposed.TotalRefund) != 0m)
        {
            throw new InvalidOperationException(
                $"The refund tenders total {refundedTotal:0.00} but the return is {proposed.TotalRefund:0.00}.");
        }
    }

    /// <summary>
    /// Builds the return lines for a set of sale lines at the stated quantities.
    /// </summary>
    /// <remarks>
    /// Refunds the price actually charged, apportioned per unit, so a discounted purchase is
    /// refunded at the discounted price. Refunding the shelf price would let a customer profit
    /// by buying in a sale and returning later.
    /// </remarks>
    public static List<ReturnLine> BuildLines(
        Sale sale,
        IEnumerable<(SaleLine Line, decimal Quantity)> selections,
        string currency)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(selections);

        var lines = new List<ReturnLine>();

        foreach (var (line, quantity) in selections)
        {
            if (quantity <= 0m)
            {
                continue;
            }

            // What was actually charged for one unit, including its share of any discount.
            var unitRefund = line.Quantity == 0m
                ? 0m
                : CartLine.Round(line.TaxableAmount / line.Quantity);

            // Tax is apportioned the same way, so the reversal matches what was declared.
            var taxPerUnit = line.Quantity == 0m
                ? 0m
                : line.TaxAmount / line.Quantity;

            lines.Add(new ReturnLine(
                ProductId: line.ProductId,
                Barcode: line.Barcode,
                Name: line.Name,
                Quantity: quantity,
                UnitRefund: new Money(unitRefund, currency),
                TaxRate: line.TaxRate,
                TaxAmount: CartLine.Round(taxPerUnit * quantity)));
        }

        return lines;
    }
}
