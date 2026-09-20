// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

namespace Pos.Core.Domain;

/// <summary>Strongly-typed employee identifier.</summary>
public readonly record struct EmployeeId(Guid Value)
{
    public static EmployeeId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// What an employee is allowed to do.
/// </summary>
/// <remarks>
/// Deliberately coarse. A till in a shop needs to answer one question — may this person take
/// money out of the drawer without asking? — and a fine-grained permission matrix would be
/// configured wrongly and then ignored.
/// </remarks>
[Flags]
public enum EmployeePermissions
{
    /// <summary>Nothing. A misconfigured account, which must never be able to sell.</summary>
    None = 0,

    /// <summary>Ring up sales.</summary>
    Sell = 1 << 0,

    /// <summary>Process refunds. The most abuse-prone operation, so it is granted separately.</summary>
    Refund = 1 << 1,

    /// <summary>Open the cash drawer without a sale.</summary>
    OpenDrawer = 1 << 2,

    /// <summary>Apply a discount. Money leaving the till by another route.</summary>
    Discount = 1 << 3,

    /// <summary>Void a completed sale.</summary>
    Void = 1 << 4,

    /// <summary>Close a shift and read the Z-report.</summary>
    CloseShift = 1 << 5,

    /// <summary>Change prices and catalogue data.</summary>
    ManageCatalog = 1 << 6,

    /// <summary>
    /// Move stock to or from another store, and receive what arrives.
    /// </summary>
    /// <remarks>
    /// Granted separately from <see cref="ManageCatalog"/>, and deliberately: moving stock between
    /// shops is one of the few operations that makes goods disappear from one set of books and
    /// appear in another. It is a documented route for shrinkage, so it is a supervisor act rather
    /// than something anyone who can edit a price also inherits.
    /// </remarks>
    TransferStock = 1 << 7,

    /// <summary>Everything a supervisor can do.</summary>
    Supervisor = Sell | Refund | OpenDrawer | Discount | Void | CloseShift | TransferStock,

    /// <summary>Everything, including catalogue changes.</summary>
    Manager = Supervisor | ManageCatalog,
}

/// <summary>
/// A person who operates a till.
/// </summary>
/// <remarks>
/// <para>
/// The PIN exists for <b>accountability, not security</b>. A four-digit code typed on a shop
/// counter in front of customers is not a secret in any meaningful sense. It is there so a sale,
/// a refund, or a drawer opening can be attributed to a person — which is what makes a cash-up
/// meaningful.
/// </para>
/// <para>
/// Because of that, only a hash of the PIN is stored, and the PIN alone never authorises anything
/// on the hub. It identifies an operator locally; the device credential is what authenticates the
/// terminal.
/// </para>
/// </remarks>
public sealed class Employee
{
    public required EmployeeId Id { get; init; }

    public required StoreId StoreId { get; init; }

    /// <summary>Name printed on receipts and shown on the sign-in screen.</summary>
    public required string Name { get; set; }

    /// <summary>Short code for a receipt, e.g. "TH" for Thandi.</summary>
    public string? Initials { get; set; }

    /// <summary>
    /// Hash of the operator's PIN.
    /// </summary>
    /// <remarks>
    /// Never the PIN itself. A four-digit code is trivially brute-forced from a plaintext store,
    /// and staff reuse PINs elsewhere.
    /// </remarks>
    public required string PinHash { get; set; }

    public EmployeePermissions Permissions { get; set; } = EmployeePermissions.Sell;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>True when this employee may perform the given action.</summary>
    public bool Can(EmployeePermissions permission) =>
        IsActive && (Permissions & permission) == permission;

    /// <summary>True when every sale this person rings up should be flagged for review.</summary>
    /// <remarks>
    /// A trainee or a new starter. Cheap to add and worth having: it lets a supervisor review a
    /// day's takings from one person without reviewing everyone's.
    /// </remarks>
    public bool RequiresSupervision { get; set; }
}

/// <summary>Why a shift ended.</summary>
public enum ShiftEndReason
{
    /// <summary>Normal handover at the end of a trading period.</summary>
    Handover = 0,

    /// <summary>End of the trading day.</summary>
    DayClose = 1,

    /// <summary>Terminal shut down or the operator went home.</summary>
    SignedOff = 2,

    /// <summary>Ended by a supervisor, e.g. after a suspected discrepancy.</summary>
    EndedBySupervisor = 3,
}

/// <summary>
/// A period during which one operator was responsible for a drawer.
/// </summary>
/// <remarks>
/// <para>
/// A shift is the unit of cash accountability. Every sale, refund, and drawer opening during it
/// belongs to one person, which is what makes a cash-up answerable to a name rather than to a
/// terminal.
/// </para>
/// <para>
/// Shifts are opened and closed; the totals are <b>derived</b> from the sales, refunds, and
/// drawer events recorded against them rather than accumulated as the shift runs. An accumulated
/// counter would eventually disagree with the records it claims to summarise, and a cash-up that
/// cannot be reproduced cannot be audited.
/// </para>
/// </remarks>
public sealed class Shift
{
    public required ShiftId Id { get; init; }

    public required StoreId StoreId { get; init; }

    public required EmployeeId EmployeeId { get; init; }

    /// <summary>Operator name at the time of the shift, so a report survives a rename.</summary>
    public required string EmployeeName { get; init; }

    /// <summary>Terminal the shift was worked on.</summary>
    public required string TerminalId { get; init; }

    public required DateTimeOffset OpenedAt { get; init; }

    public DateTimeOffset? ClosedAt { get; set; }

    public ShiftEndReason? EndReason { get; set; }

    /// <summary>Cash counted in the drawer when the shift opened. The float.</summary>
    public required decimal OpeningFloat { get; init; }

    /// <summary>Cash counted in the drawer when the shift closed.</summary>
    public decimal? ClosingCount { get; set; }

    /// <summary>
    /// Expected cash at close, as worked out when the shift was closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recorded at close so the figure a supervisor signed off is preserved even if a sale is
    /// later voided or a refund is processed against an earlier trading day.
    /// </para>
    /// <para>
    /// This is <b>not</b> the source of truth for reporting: <see cref="ShiftCalculator"/>
    /// recomputes the expectation from the underlying sales, refunds, and drawer events, and that
    /// recomputed figure is what a cash-up reports. The stored value exists so a historic
    /// signed-off figure is recoverable, but it must never override the derivation.
    /// </para>
    /// </remarks>
    public decimal? ExpectedCashAtClose { get; set; }

    /// <summary>
    /// Difference between the count and what was expected, at close.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Positive is over, negative is short. This is the number a cash-up exists to produce, and
    /// forcing it to zero would defeat the whole exercise.
    /// </para>
    /// <para>
    /// Takes the expectation as an argument rather than reading a stored field, so there is
    /// exactly one implementation of the arithmetic. A second copy here could disagree with
    /// <see cref="ShiftCalculator"/> and there would be no way to tell which was right.
    /// </para>
    /// </remarks>
    public decimal? VarianceAgainst(decimal expectedCash) =>
        ClosingCount is { } counted ? CartLine.Round(counted - expectedCash) : null;

    public string? Note { get; set; }

    /// <summary>
    /// Supervisor who closed a shift they did not open.
    /// </summary>
    /// <remarks>
    /// A shift closed by someone other than its owner needs a name against it, or accountability
    /// for any discrepancy disappears exactly when it matters most.
    /// </remarks>
    public string? ClosedByEmployeeId { get; set; }

    public bool IsOpen => ClosedAt is null;
}

/// <summary>Strongly-typed shift identifier.</summary>
public readonly record struct ShiftId(Guid Value)
{
    public static ShiftId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>What happened to the cash drawer.</summary>
public enum DrawerEventType
{
    /// <summary>Shift opened; the float was counted in.</summary>
    ShiftOpened = 0,

    /// <summary>Drawer opened without a sale. Always worth recording.</summary>
    NoSale = 1,

    /// <summary>Cash paid in, e.g. to top up change.</summary>
    CashIn = 2,

    /// <summary>Cash removed, e.g. a banking drop or a payout.</summary>
    CashOut = 3,

    /// <summary>Mid-shift count, without closing.</summary>
    CountPerformed = 4,

    /// <summary>Shift closed; the drawer was counted.</summary>
    ShiftClosed = 5,
}

/// <summary>
/// A cash drawer event.
/// </summary>
/// <remarks>
/// Append-only, for the same reason stock movements are: a drawer that opened six times during a
/// quiet hour is information, and a "times opened" counter that gets reset would lose it. Every
/// no-sale opening is the classic cover for a small theft, so these are never deleted.
/// </remarks>
public sealed record DrawerEvent
{
    public required string Id { get; init; }

    public required ShiftId ShiftId { get; init; }

    public required StoreId StoreId { get; init; }

    public required DrawerEventType Type { get; init; }

    /// <summary>Cash added or removed. Zero for a no-sale opening.</summary>
    public decimal Amount { get; init; }

    /// <summary>Employee who caused it.</summary>
    public required EmployeeId EmployeeId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Reason, required for a no-sale opening or a cash movement.</summary>
    public string? Reason { get; init; }

    /// <summary>True when this event moves cash and therefore needs a reason.</summary>
    public bool RequiresReason => Type is DrawerEventType.NoSale
        or DrawerEventType.CashIn
        or DrawerEventType.CashOut;
}

/// <summary>
/// Everything recorded against a shift, from which its totals are derived.
/// </summary>
/// <param name="Shift">The shift itself.</param>
/// <param name="Sales">Completed and voided sales rung during the shift.</param>
/// <param name="Returns">Refunds processed during the shift.</param>
/// <param name="DrawerEvents">Cash movements and no-sale openings.</param>
public sealed record ShiftActivity(
    Shift Shift,
    IReadOnlyList<Sale> Sales,
    IReadOnlyList<SalesReturn> Returns,
    IReadOnlyList<DrawerEvent> DrawerEvents);

/// <summary>
/// The figures a cash-up produces.
/// </summary>
/// <param name="ShiftId">The shift these figures belong to.</param>
/// <param name="EmployeeName">Who is accountable for them.</param>
/// <param name="OpenedAt">When the shift started.</param>
/// <param name="ClosedAt">When it ended, or null while still open.</param>
/// <param name="SaleCount">Completed sales.</param>
/// <param name="VoidCount">Sales voided during the shift.</param>
/// <param name="GrossTakings">Total taken including tax.</param>
/// <param name="TaxCollected">Tax collected, owed onward.</param>
/// <param name="CashTakings">Cash taken, which is what entered the drawer.</param>
/// <param name="NonCashTakings">Card and other tenders, which did not.</param>
/// <param name="RefundTotal">Total refunded.</param>
/// <param name="CashRefunds">Refunds paid out in cash, which left the drawer.</param>
/// <param name="OpeningFloat">Float counted in at open.</param>
/// <param name="CashIn">Cash added during the shift.</param>
/// <param name="CashOut">Cash removed during the shift.</param>
/// <param name="NoSaleCount">Drawer openings with no sale.</param>
/// <param name="ExpectedCash">What should be in the drawer.</param>
/// <param name="ClosingCount">What was actually counted, at close.</param>
/// <param name="Variance">Difference between the two.</param>
public readonly record struct ShiftTotals(
    ShiftId ShiftId,
    string EmployeeName,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    int SaleCount,
    int VoidCount,
    decimal GrossTakings,
    decimal TaxCollected,
    decimal CashTakings,
    decimal NonCashTakings,
    decimal RefundTotal,
    decimal CashRefunds,
    decimal OpeningFloat,
    decimal CashIn,
    decimal CashOut,
    int NoSaleCount,
    decimal ExpectedCash,
    decimal? ClosingCount,
    decimal? Variance)
{
    /// <summary>True while the shift is still running.</summary>
    public bool IsOpen => ClosedAt is null;

    /// <summary>True when the drawer balanced to the cent.</summary>
    public bool Balanced => Variance is { } variance && variance == 0m;

    /// <summary>True when the count was short of what was expected.</summary>
    public bool IsShort => Variance is { } variance && variance < 0m;

    /// <summary>True when the count was over what was expected.</summary>
    public bool IsOver => Variance is { } variance && variance > 0m;
}

/// <summary>
/// Derives shift totals from the activity recorded against a shift.
/// </summary>
/// <remarks>
/// Pure and stateless, so a cash-up is reproducible from the same records. This is the property
/// that makes a shift auditable: two readings of the same shift must agree, and a discrepancy
/// must be explainable from the underlying events rather than from a counter nobody can inspect.
/// </remarks>
public static class ShiftCalculator
{
    /// <summary>
    /// Calculates the totals for a shift.
    /// </summary>
    public static ShiftTotals Calculate(ShiftActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var shift = activity.Shift;

        // Only completed sales are takings. A voided sale took no money.
        var completed = activity.Sales.Where(s => !s.IsVoided).ToArray();
        var voided = activity.Sales.Where(s => s.IsVoided).ToArray();

        var gross = completed.Sum(s => s.Total);
        var tax = completed.Sum(s => s.Tax.Tax);

        var cashTakings = SumTenders(completed, TenderType.Cash);
        var nonCashTakings = CartLine.Round(gross - cashTakings);

        var refundTotal = activity.Returns.Sum(r => r.TotalRefund);
        var cashRefunds = activity.Returns
            .SelectMany(r => r.Refunds)
            .Where(t => t.Type == TenderType.Cash)
            .Sum(t => t.Amount.Amount);

        var cashIn = activity.DrawerEvents
            .Where(e => e.Type == DrawerEventType.CashIn)
            .Sum(e => e.Amount);

        var cashOut = activity.DrawerEvents
            .Where(e => e.Type == DrawerEventType.CashOut)
            .Sum(e => e.Amount);

        var noSaleCount = activity.DrawerEvents.Count(e => e.Type == DrawerEventType.NoSale);

        // What should be in the drawer. Non-cash tenders are excluded because they never entered
        // it; including them would make every shift look over by the card total.
        var expected = CartLine.Round(
            shift.OpeningFloat + cashTakings - cashRefunds + cashIn - cashOut);

        return new ShiftTotals(
            ShiftId: shift.Id,
            EmployeeName: shift.EmployeeName,
            OpenedAt: shift.OpenedAt,
            ClosedAt: shift.ClosedAt,
            SaleCount: completed.Length,
            VoidCount: voided.Length,
            GrossTakings: CartLine.Round(gross),
            TaxCollected: CartLine.Round(tax),
            CashTakings: cashTakings,
            NonCashTakings: nonCashTakings,
            RefundTotal: CartLine.Round(refundTotal),
            CashRefunds: CartLine.Round(cashRefunds),
            OpeningFloat: shift.OpeningFloat,
            CashIn: CartLine.Round(cashIn),
            CashOut: CartLine.Round(cashOut),
            NoSaleCount: noSaleCount,
            ExpectedCash: expected,
            ClosingCount: shift.ClosingCount,

            // Derived from the recomputed expectation, never from the stored one, so a cash-up
            // and the shift record cannot disagree about the same shift.
            Variance: shift.VarianceAgainst(expected));
    }

    private static decimal SumTenders(IEnumerable<Sale> sales, TenderType type) =>
        CartLine.Round(sales
            .SelectMany(s => s.Tenders)
            .Where(t => t.Type == type)
            .Sum(t => t.Amount.Amount));
}
