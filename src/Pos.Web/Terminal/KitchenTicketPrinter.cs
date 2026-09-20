// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;
using Pos.Devices.EscPos;
using Pos.Infrastructure.Checkout;

namespace Pos.Web.Terminal;

/// <summary>
/// Sends an order to the kitchen and bar printers.
/// </summary>
/// <remarks>
/// <para>
/// A wrapper rather than a bare second <see cref="TerminalPrinterProvider"/> so the till can be
/// injected with something whose name says which printer it is. Two providers of the same type in
/// one component would compile happily and be swapped by mistake, and the symptom of that mistake
/// is customer receipts coming out of the kitchen printer.
/// </para>
/// <para>
/// Every failure here is swallowed after being logged. A kitchen ticket is a work instruction for
/// food that has already been paid for: the sale is recorded, the receipt is printed, and the
/// customer is holding their goods. Throwing would abort a completed sale because a second
/// printer was unplugged.
/// </para>
/// </remarks>
public sealed class KitchenTicketPrinter(
    TerminalPrinterProvider provider,
    ILogger<KitchenTicketPrinter> logger)
{
    private readonly TerminalPrinterProvider _provider =
        provider ?? throw new ArgumentNullException(nameof(provider));

    private readonly ILogger<KitchenTicketPrinter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>True when a kitchen printer is paired and reachable.</summary>
    public bool IsAvailable => _provider.IsReady;

    /// <summary>
    /// Which physical printer this sends to.
    /// </summary>
    /// <remarks>
    /// Exposed so the wiring can be asserted rather than assumed. A kitchen ticket routed to the
    /// receipt printer, or a label to the kitchen, is a copy-paste error in the container that
    /// every other kind of test passes straight through — the two services are the same type and
    /// both resolve happily.
    /// </remarks>
    public PrinterRole Role => _provider.Role;

    /// <summary>
    /// Resolves the kitchen printer, or null when none is paired.
    /// </summary>
    /// <remarks>
    /// Exposed so the device screen can attempt a connection and report what happened, rather than
    /// showing a remembered binding that may no longer be plugged in.
    /// </remarks>
    public Task<IReceiptPrinter?> ResolveAsync(CancellationToken ct = default) => _provider.GetAsync(ct);

    /// <summary>Forgets the resolved printer, so the next attempt re-reads the binding.</summary>
    public void Reset() => _provider.Reset();

    /// <summary>
    /// Routes a completed sale to every station that has work, and prints each ticket.
    /// </summary>
    /// <returns>How many tickets were sent.</returns>
    public async Task<int> PrintAsync(
        Store store,
        CompletedSale completed,
        string? cashierName = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        // Routing first, so a sale with nothing to prepare costs nothing — not even waking the
        // printer. Most sales in most shops have no station at all.
        var tickets = StationTicketRouter.Route(store, completed.Sale);

        if (tickets.Count == 0)
        {
            return 0;
        }

        var printer = await _provider.GetAsync(ct).ConfigureAwait(false);

        if (printer is null)
        {
            // Deliberately not an error. A shop that prepares food but has not paired a kitchen
            // printer is a shop that hands orders over the counter, and the till says so on the
            // device screen rather than interrupting the operator mid-sale.
            TerminalLog.KitchenPrinterMissing(_logger, tickets.Count);
            return 0;
        }

        var sent = 0;

        foreach (var ticket in tickets)
        {
            try
            {
                var document = new ReceiptDocument(
                    Store: store,
                    Sale: completed.Sale,
                    Type: PosDocumentType.KitchenTicket,
                    CashierName: cashierName,
                    StationId: ticket.StationId);

                var bytes = ReceiptRenderer.RenderKitchenTicket(document);

                await printer.Transport.WriteAsync(bytes, ct).ConfigureAwait(false);
                sent++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One station's printer being down must not stop the next station's ticket. The
                // bar still needs to know about the drinks even if the kitchen is unplugged.
                TerminalLog.StationTicketFailed(_logger, ticket.StationId, ex);
            }
        }

        return sent;
    }
}
