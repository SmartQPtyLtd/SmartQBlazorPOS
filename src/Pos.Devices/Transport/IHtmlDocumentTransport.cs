// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;

namespace Pos.Devices.Transport;

/// <summary>
/// A transport that prints documents as HTML rather than as ESC/POS bytes.
/// </summary>
/// <remarks>
/// <para>
/// The browser's print dialog is the one route to paper that works in every browser, including the
/// two that have no device APIs at all. It is not a lesser WebUSB: it takes markup, not control
/// bytes, and it can neither cut nor open a drawer.
/// </para>
/// <para>
/// A separate interface rather than another flag on <see cref="PrinterCapabilities"/>, because the
/// difference is not a degree of capability — it is a different input. A transport implementing this
/// refuses raw bytes precisely so that a caller who has not noticed cannot emit a page of command
/// characters onto a customer's receipt.
/// </para>
/// </remarks>
public interface IHtmlDocumentTransport
{
    /// <summary>Prints a document rendered as HTML.</summary>
    /// <param name="title">Job name shown in the print dialog.</param>
    /// <param name="htmlBody">Printable HTML fragment.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask PrintHtmlAsync(string title, string htmlBody, CancellationToken ct = default);
}
