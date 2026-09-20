// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;

namespace Pos.Core.Payments;

/// <summary>
/// A request to take one payment.
/// </summary>
/// <param name="Type">How the customer is paying.</param>
/// <param name="Amount">Amount to charge. Never more than the outstanding balance.</param>
/// <param name="SaleReference">
/// Human-readable sale or basket reference, for a terminal that prints or displays one during the
/// transaction. Never used for authorisation.
/// </param>
public readonly record struct PaymentRequest(
    TenderType Type,
    Money Amount,
    string? SaleReference = null);

/// <summary>
/// What came back from the payment instrument.
/// </summary>
/// <param name="Approved">True when the money is actually secured.</param>
/// <param name="Amount">Amount secured. Matches the request on success.</param>
/// <param name="Reference">
/// Non-sensitive reference for reconciliation — an approval code, a voucher number. <b>Never</b> a
/// card number, expiry, or track value.
/// </param>
/// <param name="DeclineReason">Why it was refused, in words an operator can act on.</param>
public readonly record struct PaymentResult(
    bool Approved,
    Money Amount,
    string? Reference = null,
    string? DeclineReason = null)
{
    /// <summary>The payment went through.</summary>
    public static PaymentResult ApprovedFor(Money amount, string? reference = null) =>
        new(true, amount, reference);

    /// <summary>The payment was refused. The sale must not complete.</summary>
    public static PaymentResult Declined(Money amount, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new(false, amount, null, reason);
    }
}

/// <summary>
/// Takes payment for a sale.
/// </summary>
/// <remarks>
/// <para>
/// The seam exists because the till must not know which kind of payment it is taking. Today the
/// shop has a standalone card machine and the operator tells the POS that the card went through.
/// Tomorrow the POS may drive an integrated terminal itself. Checkout code that called either one
/// directly would have to change on the day the hardware did.
/// </para>
/// <para>
/// The behaviour that makes this a real seam rather than a decorative one is
/// <see cref="PaymentResult.Approved"/>: <b>a sale must not complete on a declined payment.</b>
/// A provider that can refuse — an integrated terminal, a gift-card balance check — is the whole
/// reason the return value is not just a reference string.
/// </para>
/// <para>
/// Nothing here may carry card data. The reference field is for an approval code; a PAN or track
/// value reaching this type would put the entire system into PCI DSS scope.
/// </para>
/// </remarks>
public interface IPaymentProvider
{
    /// <summary>Stable identifier, e.g. "record-only".</summary>
    string ProviderId { get; }

    /// <summary>Name shown to the operator, e.g. "Standalone card machine".</summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether this provider can actually refuse a payment.
    /// </summary>
    /// <remarks>
    /// <b>False for a record-only provider, and that is the honest answer.</b> It records what the
    /// operator says happened; it cannot verify that a card was approved, because it never spoke
    /// to the terminal. The UI uses this to avoid implying a guarantee the shop does not have.
    /// </remarks>
    bool CanDecline { get; }

    /// <summary>
    /// Takes a payment.
    /// </summary>
    /// <remarks>
    /// Must not throw for a decline; a refusal is an ordinary outcome and is reported through
    /// <see cref="PaymentResult"/>. Throwing is reserved for a provider that is broken or
    /// unreachable, which the caller surfaces as a fault rather than as a decline.
    /// </remarks>
    Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct = default);
}

/// <summary>
/// Records a payment the operator has already taken out of band.
/// </summary>
/// <remarks>
/// <para>
/// The provider a shop uses with a standalone card machine, and the only one the system ships
/// today. Cash is counted by the operator; a card is run on the terminal on the counter. The POS
/// is told the outcome and writes it down.
/// </para>
/// <para>
/// <b>It approves everything, because it has nothing to check.</b> That is not a weakness to be
/// papered over — <see cref="CanDecline"/> is false and says so, so the interface does not imply a
/// verification that never happened. A shop that wants the POS to refuse a bad card needs an
/// integrated terminal, which is a hardware change rather than a configuration one.
/// </para>
/// <para>
/// It still refuses a request that is structurally impossible — a zero or negative amount, or a
/// currency that does not match the sale — because those are programming errors, and silently
/// recording them would put a wrong figure in the day's takings.
/// </para>
/// </remarks>
public sealed class RecordOnlyPaymentProvider : IPaymentProvider
{
    public string ProviderId => "record-only";

    public string DisplayName => "Recorded by the operator";

    public bool CanDecline => false;

    public Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct = default)
    {
        if (request.Amount.Amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), "A payment must be greater than zero.");
        }

        if (request.Amount.IsNegative)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), "A payment cannot be negative.");
        }

        // Approves immediately and records nothing beyond what the caller already holds. It cannot
        // produce a reference, because it did not talk to anything that could issue one.
        return Task.FromResult(PaymentResult.ApprovedFor(request.Amount));
    }
}
