// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;

namespace Pos.Core.Tests;

/// <summary>
/// Tests for building up payments against a sale.
/// </summary>
/// <remarks>
/// Split tender is ordinary retail — part cash, part card, a voucher for the rest — and the rule
/// that decides whether it adds up has sharp edges. The sharpest is that cash and everything else
/// overtender differently: a customer handing over 100 for a 70 bill is normal and produces change,
/// while "paying" 100 on a card for a 70 bill is a cash advance the shop never earned.
/// </remarks>
public sealed class TenderSessionTests
{
    private const string Zar = "ZAR";

    private static TenderSession Session(decimal total) => new(new Money(total, Zar));

    // ------------------------------------------------------------------ exact payment

    [Fact]
    public void A_single_exact_payment_settles_the_sale()
    {
        var session = Session(115.00m);

        session.AddExact(TenderType.ExternalCard, 115.00m);

        Assert.True(session.IsSettled);
        Assert.Equal(0m, session.Outstanding);
        Assert.Equal(0m, session.ChangeDue);
    }

    [Fact]
    public void An_unsettled_session_refuses_to_produce_tenders()
    {
        // The guard that stops an under-paid basket reaching the recorder.
        var session = Session(115.00m);
        session.AddExact(TenderType.ExternalCard, 100.00m);

        var exception = Assert.Throws<InvalidOperationException>(() => session.ToTenders());

        Assert.Contains("15.00", exception.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------- split tender

    [Fact]
    public void Cash_and_card_together_settle_the_sale()
    {
        var session = Session(115.00m);

        session.AddCash(50.00m);
        session.AddExact(TenderType.ExternalCard, 65.00m);

        Assert.True(session.IsSettled);
        Assert.Equal(2, session.Applied.Count);
        Assert.Equal(115.00m, session.AppliedAmount);
    }

    [Fact]
    public void A_three_way_split_settles_the_sale()
    {
        var session = Session(100.00m);

        session.AddCash(30.00m);
        session.AddExact(TenderType.GiftCard, 40.00m, "GC-1234");
        session.AddExact(TenderType.ExternalCard, 30.00m);

        Assert.True(session.IsSettled);
        Assert.Equal(100.00m, session.AppliedAmount);
    }

    [Fact]
    public void The_outstanding_balance_shrinks_as_payments_are_applied()
    {
        // What the operator reads off the screen to know what to ask the customer for next.
        var session = Session(115.00m);

        Assert.Equal(115.00m, session.Outstanding);

        session.AddCash(50.00m);
        Assert.Equal(65.00m, session.Outstanding);

        session.AddExact(TenderType.ExternalCard, 65.00m);
        Assert.Equal(0m, session.Outstanding);
    }

    // -------------------------------------------------------------------------- cash

    [Fact]
    public void Cash_may_exceed_the_balance_and_produces_change()
    {
        // The ordinary case: a customer pays a 70 bill with a 100 note.
        var session = Session(70.00m);

        var tender = session.AddCash(100.00m);

        // What is applied is what was owed; what was handed over is kept so the change is
        // recoverable from the record.
        Assert.Equal(70.00m, tender.Amount.Amount);
        Assert.Equal(100.00m, tender.Tendered!.Value.Amount);
        Assert.Equal(30.00m, session.ChangeDue);

        Assert.True(session.IsSettled);
        Assert.Equal(70.00m, session.AppliedAmount);
    }

    [Fact]
    public void Cash_below_the_balance_leaves_the_sale_open()
    {
        var session = Session(70.00m);

        var tender = session.AddCash(50.00m);

        Assert.Equal(50.00m, tender.Amount.Amount);
        Assert.Equal(20.00m, session.Outstanding);
        Assert.Equal(0m, session.ChangeDue);
        Assert.False(session.IsSettled);
    }

    [Fact]
    public void Cash_handed_over_must_be_greater_than_zero()
    {
        var session = Session(70.00m);

        Assert.Throws<InvalidOperationException>(() => session.AddCash(0m));
        Assert.Throws<InvalidOperationException>(() => session.AddCash(-5m));
    }

    [Fact]
    public void Cash_cannot_be_added_to_a_settled_sale()
    {
        // Otherwise the till would take money for a sale that is already paid for, and the excess
        // would sit in the drawer with nothing to explain it.
        var session = Session(70.00m);
        session.AddCash(70.00m);

        var exception = Assert.Throws<InvalidOperationException>(() => session.AddCash(10.00m));

        Assert.Contains("already settled", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cash_handled_through_the_wrong_method_is_refused_with_an_explanation()
    {
        // AddExact cannot express change, so a note handed over for more than is owed would be
        // silently refused — the ordinary case, rejected. The error says which method to use.
        var session = Session(70.00m);

        var exception = Assert.Throws<ArgumentException>(
            () => session.AddExact(TenderType.Cash, 100.00m));

        Assert.Contains("AddCash", exception.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------- everything else

    [Fact]
    public void A_card_payment_may_not_exceed_the_balance()
    {
        // Over-paying by card is not change: it is a cash advance. Recording it as a sale would
        // book 30 of income the shop never earned, and the excess would have to come back as a
        // refund.
        var session = Session(70.00m);

        var exception = Assert.Throws<InvalidOperationException>(
            () => session.AddExact(TenderType.ExternalCard, 100.00m));

        Assert.Contains("more than", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cash", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_card_payment_may_settle_exactly_the_remainder()
    {
        var session = Session(70.00m);
        session.AddCash(50.00m);

        session.AddExact(TenderType.ExternalCard, 20.00m);

        Assert.True(session.IsSettled);
    }

    [Fact]
    public void A_non_cash_payment_must_be_greater_than_zero()
    {
        var session = Session(70.00m);

        Assert.Throws<InvalidOperationException>(() => session.AddExact(TenderType.GiftCard, 0m));
        Assert.Throws<InvalidOperationException>(() => session.AddExact(TenderType.Voucher, -10m));
    }

    [Fact]
    public void A_non_cash_payment_cannot_be_added_to_a_settled_sale()
    {
        var session = Session(70.00m);
        session.AddExact(TenderType.ExternalCard, 70.00m);

        Assert.Throws<InvalidOperationException>(
            () => session.AddExact(TenderType.GiftCard, 10.00m));
    }

    [Fact]
    public void A_reference_is_carried_onto_the_tender()
    {
        // The approval code the operator reads off the card terminal, which is what reconciles
        // against that terminal's own log.
        var session = Session(70.00m);

        var tender = session.AddExact(TenderType.ExternalCard, 70.00m, "AUTH-9931");

        Assert.Equal("AUTH-9931", tender.Reference);
    }

    // ---------------------------------------------------------------------- correction

    [Fact]
    public void A_mistyped_payment_can_be_removed_before_the_sale_is_rung()
    {
        var session = Session(115.00m);

        var wrong = session.AddExact(TenderType.ExternalCard, 115.00m);
        session.Remove(wrong);

        Assert.True(session.IsEmpty);
        Assert.Equal(115.00m, session.Outstanding);
    }

    [Fact]
    public void Removing_the_last_payment_reopens_the_balance()
    {
        // What the operator expects when they correct a mistyped amount: the sale is not settled
        // any more, and the till asks for the money again.
        var session = Session(115.00m);
        session.AddCash(115.00m);

        Assert.True(session.IsSettled);

        var removed = session.RemoveLast();

        Assert.NotNull(removed);
        Assert.False(session.IsSettled);
        Assert.Equal(115.00m, session.Outstanding);
    }

    [Fact]
    public void Removing_from_an_empty_session_returns_nothing()
    {
        var session = Session(115.00m);

        Assert.Null(session.RemoveLast());
    }

    [Fact]
    public void Removing_a_tender_that_was_never_applied_does_nothing()
    {
        var session = Session(115.00m);
        session.AddCash(20.00m);

        var stranger = new Tender(TenderType.ExternalCard, new Money(10.00m, Zar));

        Assert.False(session.Remove(stranger));
        Assert.Equal(20.00m, session.AppliedAmount);
    }

    [Fact]
    public void Clearing_discards_every_payment()
    {
        var session = Session(115.00m);
        session.AddCash(50.00m);
        session.AddExact(TenderType.GiftCard, 65.00m);

        session.Clear();

        Assert.True(session.IsEmpty);
        Assert.Equal(115.00m, session.Outstanding);
        Assert.Equal(0m, session.ChangeDue);
    }

    // ------------------------------------------------------------------------- edges

    [Fact]
    public void A_zero_total_sale_is_settled_from_the_start()
    {
        // Reachable with a 100% discount, where nothing is owed and nothing is tendered. The
        // alternative — demanding a zero payment — would block a legitimate give-away.
        var session = Session(0m);

        Assert.True(session.IsSettled);
        Assert.Empty(session.ToTenders());
    }

    [Fact]
    public void A_negative_total_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Session(-1m));
    }

    [Fact]
    public void Rounding_does_not_block_a_sale()
    {
        // 0.1 + 0.2 style drift must not leave a basket unpayable. The comparison is at minor-unit
        // precision for exactly this reason.
        var session = Session(0.30m);

        session.AddCash(0.10m);
        session.AddExact(TenderType.GiftCard, 0.20m);

        Assert.True(session.IsSettled);
    }

    [Fact]
    public void The_maximum_exact_payment_is_what_is_still_owed()
    {
        // What a UI prefills as the card amount, so the common case is one tap.
        var session = Session(115.00m);

        Assert.Equal(115.00m, session.MaximumExactPayment);

        session.AddCash(15.00m);

        Assert.Equal(100.00m, session.MaximumExactPayment);
    }

    [Fact]
    public void Change_is_derived_from_the_tenders_rather_than_tracked_beside_them()
    {
        // A second counter is how a till ends up displaying change that was never given.
        var session = Session(100.00m);

        session.AddCash(60.00m);
        session.AddExact(TenderType.ExternalCard, 40.00m);

        // 60 was handed over for a 60 payment, so there is no change even though the basket needed
        // a second payment to settle.
        Assert.Equal(0m, session.ChangeDue);
    }

    [Fact]
    public void Change_is_only_ever_produced_by_cash()
    {
        var session = Session(50.00m);
        session.AddExact(TenderType.Voucher, 50.00m);

        Assert.Equal(0m, session.ChangeDue);
    }
}
