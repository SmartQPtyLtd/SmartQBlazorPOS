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
/// The method by which a customer paid.
/// </summary>
/// <remarks>
/// Record-only: the POS notes <em>how</em> a sale was settled but never captures,
/// transmits, or stores card data. Pan, expiry, and track data must never reach
/// this type — keeping them out is what keeps the system out of PCI DSS scope.
/// </remarks>
public enum TenderType
{
    Cash = 0,

    /// <summary>Card settled on a separate terminal; recorded here for reconciliation only.</summary>
    ExternalCard = 1,

    GiftCard = 2,

    Voucher = 3,

    StoreCredit = 4,

    /// <summary>Loyalty points redeemed as payment.</summary>
    LoyaltyPoints = 5,
}

/// <summary>
/// One payment applied to a sale. A sale may carry several (split tender).
/// </summary>
/// <param name="Type">How the customer paid.</param>
/// <param name="Amount">Amount actually applied to the balance — never more than was owed.</param>
/// <param name="Tendered">
/// For cash, what the customer handed over. Change is <c>Tendered - Amount</c>.
/// Null for exact-amount tenders such as cards.
/// </param>
/// <param name="Reference">
/// Non-sensitive reference: a gift-card number, voucher code, or the approval code
/// printed by an external card terminal. Never a PAN or a track value.
/// </param>
public readonly record struct Tender(
    TenderType Type,
    Money Amount,
    Money? Tendered = null,
    string? Reference = null)
{
    /// <summary>Change due back to the customer. Zero for anything but cash.</summary>
    public Money ChangeDue(string currency)
    {
        if (Tendered is not { } tendered)
        {
            return Money.Zero(currency);
        }

        var change = tendered - Amount;
        return change.IsNegative ? Money.Zero(currency) : change;
    }

    /// <summary>
    /// True when the tendered amount covers the applied amount. Guards against a
    /// cashier recording R50 received against a R70 payment.
    /// </summary>
    public bool IsSufficient =>
        Type != TenderType.Cash
        || Tendered is not { } tendered
        || tendered.Amount >= Amount.Amount;
}
