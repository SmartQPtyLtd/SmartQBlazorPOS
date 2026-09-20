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
/// A place an order is prepared: the kitchen, the bar, the coffee machine.
/// </summary>
/// <remarks>
/// <para>
/// A station exists because a shop has more than one place food and drink are made, and the people
/// in those places need different pieces of paper. A barista does not need the burger, and the
/// kitchen does not need the wine — printing everything everywhere means every station reads past
/// most of what it is handed, which is exactly how a real item gets missed.
/// </para>
/// <para>
/// Kept as an id and a name rather than an enum so a shop can name its own stations without a code
/// change, and declared on the store so the order tickets are grouped in is the shop's order
/// rather than whatever the till happened to hash to.
/// </para>
/// </remarks>
/// <param name="Id">Stable identifier, referenced by products. Matched case-insensitively.</param>
/// <param name="Name">Name printed at the head of the ticket, e.g. "KITCHEN".</param>
public readonly record struct PreparationStation(string Id, string Name)
{
    /// <summary>True when this station has enough of an id to be routed to.</summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(Id);
}

/// <summary>
/// One station's share of a sale: the lines it has to make.
/// </summary>
/// <remarks>
/// Not a <see cref="Sale"/>. A station ticket is a work instruction, not a transaction — it has no
/// totals, no tenders, and no tax, because the person reading it is making food rather than
/// handling money. Keeping it a separate shape means a kitchen ticket physically cannot leak a
/// price onto the pass.
/// </remarks>
/// <param name="StationId">Station the ticket is for.</param>
/// <param name="StationName">Name to print at the head of the ticket.</param>
/// <param name="Lines">Only the lines this station prepares, in the order they were scanned.</param>
public readonly record struct StationTicket(
    string StationId,
    string StationName,
    IReadOnlyList<SaleLine> Lines)
{
    /// <summary>Total units on this ticket, including fractional weights.</summary>
    public decimal TotalQuantity => Lines.Sum(l => l.Quantity);

    /// <summary>True when there is nothing to make.</summary>
    public bool IsEmpty => Lines.Count == 0;
}

/// <summary>
/// Splits a completed sale into one ticket per preparation station.
/// </summary>
/// <remarks>
/// <para>
/// This is the rule that decides what the kitchen and the bar each get, so it lives in the pure
/// domain and is tested without a printer.
/// </para>
/// <para>
/// Three decisions are deliberate:
/// </para>
/// <list type="bullet">
/// <item><b>A line with no station produces no ticket.</b> A bottled drink or a bag of chips is
/// handed over at the till; sending it to a kitchen printer wastes paper, and worse, trains the
/// kitchen to ignore tickets.</item>
/// <item><b>A voided sale produces nothing at all.</b> The goods were never made. A ticket for a
/// sale that was reversed is a real cost — someone cooks food nobody pays for.</item>
/// <item><b>Unknown station ids still get a ticket.</b> A product assigned to a station the store
/// has not declared is a configuration gap, and dropping its line silently is the one outcome
/// that loses a customer's order. It prints under its raw id so the gap is visible and fixable.
/// </item>
/// </list>
/// </remarks>
public static class StationTicketRouter
{
    /// <summary>
    /// Groups a sale's lines by the station that prepares them.
    /// </summary>
    /// <param name="store">Store whose station list sets the printing order.</param>
    /// <param name="sale">The completed sale.</param>
    /// <returns>One ticket per station that has work, in the store's declared order.</returns>
    public static IReadOnlyList<StationTicket> Route(Store store, Sale sale)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sale);

        if (sale.IsVoided || sale.Lines.Count == 0)
        {
            return [];
        }

        var grouped = new Dictionary<string, List<SaleLine>>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in sale.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.StationId))
            {
                continue;
            }

            if (!grouped.TryGetValue(line.StationId, out var lines))
            {
                lines = [];
                grouped[line.StationId] = lines;
            }

            // Order is the scan order, which is the order the customer asked for things and the
            // order the kitchen would otherwise have to reconstruct from the receipt.
            lines.Add(line);
        }

        if (grouped.Count == 0)
        {
            return [];
        }

        var tickets = new List<StationTicket>(grouped.Count);

        // Declared stations first, in the shop's own order.
        foreach (var station in store.Stations)
        {
            if (!station.IsUsable || !grouped.Remove(station.Id, out var lines))
            {
                continue;
            }

            tickets.Add(new StationTicket(station.Id, station.Name, lines));
        }

        // Anything left is a product pointing at a station this store has not declared. Sorted so
        // the output is stable rather than dependent on dictionary ordering.
        foreach (var (stationId, lines) in grouped.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            tickets.Add(new StationTicket(stationId, store.StationNameFor(stationId), lines));
        }

        return tickets;
    }
}
