// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;
using Pos.Core.Payments;
using Pos.Infrastructure.Storage;

namespace Pos.Infrastructure.Checkout;

/// <summary>
/// Takes payment for a basket and then records the sale.
/// </summary>
/// <remarks>
/// <para>
/// The ordering here is the whole point, and it is the reverse of the printing order: <b>money is
/// secured first, then the sale is written</b>. A sale recorded against a payment that later
/// declined is a sale the shop cannot collect on, and the customer is already walking out with the
/// goods.
/// </para>
/// <para>
/// It sits above <see cref="CheckoutRecordingService"/> rather than inside it so the two rules stay
/// separable: recording a sale must work with no payment provider at all — a terminal replaying an
/// offline backlog, or a test — while taking payment is what a live till does.
/// </para>
/// <para>
/// <b>Cash is not charged to a provider.</b> There is no instrument to talk to: the operator has
/// the notes in their hand. Only card, voucher, gift card, and loyalty payments go to a provider,
/// which is what stops the system from "authorising" cash.
/// </para>
/// </remarks>
public sealed class SaleCompletionService(
    CheckoutRecordingService recorder,
    IPaymentProvider payments)
{
    private readonly CheckoutRecordingService _recorder =
        recorder ?? throw new ArgumentNullException(nameof(recorder));

    private readonly IPaymentProvider _payments =
        payments ?? throw new ArgumentNullException(nameof(payments));

    /// <summary>The provider taking payments at this till.</summary>
    public IPaymentProvider Provider => _payments;

    /// <summary>
    /// Secures every payment and then records the sale.
    /// </summary>
    /// <param name="cart">The basket being paid for.</param>
    /// <param name="session">Payments applied to it. Must be settled exactly.</param>
    /// <param name="store">The store, supplying the code used in the receipt number.</param>
    /// <param name="employeeId">Operator recorded against the sale.</param>
    /// <param name="businessDate">Trading day to attribute the sale to.</param>
    /// <param name="shiftId">Shift the sale was rung during, for the cash-up.</param>
    /// <param name="catalogVersion">Catalogue version the sale was priced against.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="PaymentDeclinedException">
    /// A payment was refused. Nothing was recorded, so the basket is intact and the operator can
    /// take another method.
    /// </exception>
    public async Task<CompletedSale> PayAndRecordAsync(
        Cart cart,
        TenderSession session,
        Store store,
        string? employeeId = null,
        DateOnly? businessDate = null,
        string? shiftId = null,
        string? catalogVersion = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cart);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(store);

        // ToTenders throws unless the balance is settled exactly, so an under-paid basket cannot
        // reach the recorder by any route through here.
        var tenders = session.ToTenders();

        var authorised = new List<Tender>(tenders.Count);

        foreach (var tender in tenders)
        {
            if (!RequiresInstrument(tender.Type))
            {
                // Cash. The operator physically has it; there is nothing to authorise.
                authorised.Add(tender);
                continue;
            }

            var result = await _payments
                .ChargeAsync(
                    new PaymentRequest(tender.Type, tender.Amount, cart.CustomerId),
                    ct)
                .ConfigureAwait(false);

            if (!result.Approved)
            {
                // Nothing is recorded and no earlier payment in this basket is kept. A partially
                // authorised sale is worse than none: the shop would hold money for a sale that
                // does not exist, and the customer would have no receipt for it.
                throw new PaymentDeclinedException(
                    result.DeclineReason ??
                    $"The {Describe(tender.Type)} payment of {tender.Amount.Amount:0.00} " +
                    $"{tender.Amount.Currency} was declined.");
            }

            // The provider's reference replaces whatever the caller had, because an approval code
            // issued by the instrument is the one that reconciles against the terminal's own log.
            authorised.Add(result.Reference is { Length: > 0 } reference
                ? tender with { Reference = reference }
                : tender);
        }

        return await _recorder
            .RecordSaleAsync(
                cart,
                authorised,
                store,
                employeeId,
                businessDate,
                catalogVersion,
                shiftId,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a tender type has an instrument behind it that can be charged.
    /// </summary>
    /// <remarks>
    /// Cash does not, which is not a detail: sending cash to a payment provider would mean the till
    /// could report a cash sale as declined, and a cashier cannot decline a note they are holding.
    /// </remarks>
    public static bool RequiresInstrument(TenderType type) => type != TenderType.Cash;

    private static string Describe(TenderType type) => type switch
    {
        TenderType.ExternalCard => "card",
        TenderType.Voucher => "voucher",
        TenderType.GiftCard => "gift card",
        TenderType.LoyaltyPoints => "loyalty",
        _ => type.ToString().ToLowerInvariant(),
    };
}
