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
/// Builds up the payments against one sale, and knows what is still owed.
/// </summary>
/// <remarks>
/// <para>
/// Exists because a customer routinely pays in more than one way — part cash, part card, a gift
/// voucher for the rest — and the rule that decides whether that adds up is not a UI concern. It
/// is arithmetic with a few sharp edges, so it lives in the pure domain and is tested without a
/// till.
/// </para>
/// <para>
/// The sharp edge is that <b>cash and everything else overtender differently</b>. A customer
/// handing over 100 for a 70 bill is normal and produces 30 change. A customer "paying" 100 on a
/// card for a 70 bill is not change — it is a cash advance, and a shop that records it as a sale
/// has booked 30 of income it never earned. So cash may exceed what is owed and everything else
/// may not.
/// </para>
/// <para>
/// Applied amounts always sum to the sale total exactly, which is what
/// <see cref="Sale.ValidateTenders"/> requires. The amount handed over is carried alongside on the
/// cash tender so the change is recoverable from the record.
/// </para>
/// </remarks>
public sealed class TenderSession
{
    private readonly List<Tender> _applied = [];

    /// <summary>Starts a session for a sale of the given total.</summary>
    /// <param name="total">What the customer owes.</param>
    public TenderSession(Money total)
    {
        if (total.IsNegative)
        {
            throw new ArgumentOutOfRangeException(nameof(total), "A sale total cannot be negative.");
        }

        Total = total;
    }

    /// <summary>What the customer owes.</summary>
    public Money Total { get; }

    /// <summary>Payments applied so far, in the order they were taken.</summary>
    public IReadOnlyList<Tender> Applied => _applied;

    /// <summary>Sum of amounts applied, at minor-unit precision.</summary>
    public decimal AppliedAmount => CartLine.Round(_applied.Sum(t => t.Amount.Amount));

    /// <summary>What is still owed. Zero once settled.</summary>
    public decimal Outstanding => CartLine.Round(Total.Amount - AppliedAmount);

    /// <summary>True when the applied payments cover the total exactly.</summary>
    public bool IsSettled => Outstanding == 0m;

    /// <summary>
    /// Change to hand back, summed across cash tenders.
    /// </summary>
    /// <remarks>
    /// Derived from the tenders rather than tracked separately, so it cannot disagree with them.
    /// A second counter is how a till ends up showing change that was never given.
    /// </remarks>
    public decimal ChangeDue => CartLine.Round(
        _applied.Sum(t => t.Type == TenderType.Cash ? t.ChangeDue(Total.Currency).Amount : 0m));

    /// <summary>True when nothing has been applied yet.</summary>
    public bool IsEmpty => _applied.Count == 0;

    /// <summary>
    /// Records cash handed over, applying only what is still owed.
    /// </summary>
    /// <param name="handedOver">
    /// What the customer physically handed across. May exceed the outstanding balance, which
    /// produces change.
    /// </param>
    /// <returns>The tender that was recorded.</returns>
    /// <exception cref="InvalidOperationException">
    /// The sale is already settled, or nothing was handed over.
    /// </exception>
    public Tender AddCash(decimal handedOver)
    {
        if (IsSettled)
        {
            throw new InvalidOperationException(
                "This sale is already settled. There is nothing left to pay.");
        }

        if (handedOver <= 0m)
        {
            throw new InvalidOperationException("Cash handed over must be greater than zero.");
        }

        // The amount applied is capped at what is owed; everything above it becomes change. This
        // is the whole reason cash is handled separately: the customer's 100 against a 70 bill is
        // a 70 payment, not a 100 one.
        var applied = Math.Min(CartLine.Round(handedOver), Outstanding);

        var tender = new Tender(
            TenderType.Cash,
            new Money(applied, Total.Currency),
            new Money(CartLine.Round(handedOver), Total.Currency));

        _applied.Add(tender);
        return tender;
    }

    /// <summary>
    /// Records an exact-amount payment: card, voucher, gift card, loyalty.
    /// </summary>
    /// <param name="type">How the customer paid.</param>
    /// <param name="amount">Amount to apply. May not exceed what is still owed.</param>
    /// <param name="reference">
    /// Non-sensitive reference from the payment instrument — an approval code, a voucher number.
    /// Never a card number.
    /// </param>
    /// <returns>The tender that was recorded.</returns>
    public Tender AddExact(TenderType type, decimal amount, string? reference = null)
    {
        if (type == TenderType.Cash)
        {
            // Cash overtenders and produces change; routing it through here would silently refuse
            // the ordinary case of a customer paying with a note.
            throw new ArgumentException(
                "Use AddCash for cash, which allows the customer to hand over more than is owed.",
                nameof(type));
        }

        if (IsSettled)
        {
            throw new InvalidOperationException(
                "This sale is already settled. There is nothing left to pay.");
        }

        if (amount <= 0m)
        {
            throw new InvalidOperationException("A payment must be greater than zero.");
        }

        var rounded = CartLine.Round(amount);

        if (rounded > Outstanding)
        {
            // Over-paying by card is not change. Recording it as a sale would book income the shop
            // never earned, and the excess would have to come back as a refund.
            throw new InvalidOperationException(
                $"A {Describe(type)} payment of {rounded:0.00} {Total.Currency} is more than the " +
                $"{Outstanding:0.00} {Total.Currency} still owed. Only cash may be over-tendered, " +
                "because only cash produces change.");
        }

        var tender = new Tender(type, new Money(rounded, Total.Currency), Tendered: null, reference);

        _applied.Add(tender);
        return tender;
    }

    /// <summary>
    /// Removes a payment that was entered in error.
    /// </summary>
    /// <remarks>
    /// Only permitted while the sale is unsettled. Once the tenders balance, the sale is complete
    /// and undoing a payment is a refund, which is a different document with a different paper
    /// trail — not an edit to a sale that has already been rung up.
    /// </remarks>
    /// <returns>True when the tender was present and removed.</returns>
    public bool Remove(Tender tender)
    {
        if (!_applied.Remove(tender))
        {
            return false;
        }

        // Removing the payment that settled the sale reopens the balance, which is what the
        // operator expects when they correct a mistyped amount.
        return true;
    }

    /// <summary>
    /// Removes the most recently applied payment.
    /// </summary>
    /// <returns>The tender that was removed, or null when nothing was applied.</returns>
    public Tender? RemoveLast()
    {
        if (_applied.Count == 0)
        {
            return null;
        }

        var last = _applied[^1];
        _applied.RemoveAt(_applied.Count - 1);

        return last;
    }

    /// <summary>Discards every payment, to start the tender again.</summary>
    public void Clear() => _applied.Clear();

    /// <summary>
    /// The tenders to record, ready for <see cref="CheckoutService.CompleteSaleAsync"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The sale is not settled.</exception>
    public IReadOnlyList<Tender> ToTenders()
    {
        if (!IsSettled)
        {
            throw new InvalidOperationException(
                $"The sale is not settled: {Outstanding:0.00} {Total.Currency} is still owed.");
        }

        return [.. _applied];
    }

    /// <summary>
    /// How much a card or voucher payment may still take, for a UI that prefills the amount.
    /// </summary>
    public decimal MaximumExactPayment => Math.Max(0m, Outstanding);

    private static string Describe(TenderType type) => type switch
    {
        TenderType.ExternalCard => "card",
        TenderType.Voucher => "voucher",
        TenderType.GiftCard => "gift card",
        TenderType.LoyaltyPoints => "loyalty",
        _ => type.ToString().ToLowerInvariant(),
    };
}
