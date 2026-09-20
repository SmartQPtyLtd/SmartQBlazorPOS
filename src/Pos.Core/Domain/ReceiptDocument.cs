// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

namespace Pos.Core.Domain;

/// <summary>The kind of document being printed.</summary>
public enum PosDocumentType
{
    /// <summary>Customer receipt, handed to the buyer.</summary>
    SalesReceipt = 0,

    /// <summary>Duplicate copy kept by the store or printed on request.</summary>
    SalesReceiptCopy = 1,

    /// <summary>Kitchen or bar preparation ticket.</summary>
    KitchenTicket = 2,

    /// <summary>Refund document produced when goods come back.</summary>
    RefundReceipt = 3,

    /// <summary>Non-fiscal report such as an X or Z reading.</summary>
    Report = 4,
}

/// <summary>
/// Everything needed to render a printable document.
/// </summary>
/// <remarks>
/// Bundles the sale together with the store configuration it must be formatted
/// against. Kept as a plain data carrier in the pure domain so that rendering can be
/// tested without a browser, a printer, or a database.
/// </remarks>
/// <param name="Store">Store whose branding, tax mode, and paper width apply.</param>
/// <param name="Sale">The completed sale to render.</param>
/// <param name="Type">Which document to produce.</param>
/// <param name="CashierName">Optional operator name printed for accountability.</param>
/// <param name="CopyIndex">
/// Which copy this is, for the "COPY 2" marker. Zero means the original.
/// </param>
/// <param name="StationId">
/// For a kitchen ticket, the station whose lines to print. Null carries the whole sale, which is
/// what a single-printer shop that sends everything to one machine wants.
/// </param>
public readonly record struct ReceiptDocument(
    Store Store,
    Sale Sale,
    PosDocumentType Type = PosDocumentType.SalesReceipt,
    string? CashierName = null,
    int CopyIndex = 0,
    string? StationId = null)
{
    /// <summary>True when this document is a reprint rather than the original.</summary>
    public bool IsCopy => Type == PosDocumentType.SalesReceiptCopy || CopyIndex > 0;
}
