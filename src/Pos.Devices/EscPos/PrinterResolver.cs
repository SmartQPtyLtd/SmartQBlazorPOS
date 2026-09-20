// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Devices.Transport;

namespace Pos.Devices.EscPos;

/// <summary>
/// Chooses which printer transport to use.
/// </summary>
/// <remarks>
/// <para>
/// This is where the browser-portability requirement is actually honoured. Callers ask
/// for "a printer" and get the best one this environment can support, in preference
/// order:
/// </para>
/// <list type="number">
/// <item><b>WebUSB</b> — direct ESC/POS, full capability, Chromium only.</item>
/// <item><b>Web Serial</b> — direct ESC/POS, most tolerant of printer quirks, Chromium only.</item>
/// <item><b>Web Bluetooth</b> — direct ESC/POS for portable printers, Chromium only.</item>
/// <item><b>HTTP bridge</b> — a local agent, works in any browser but needs installing.</item>
/// <item><b>Browser print</b> — always available, but cannot cut or open a drawer.</item>
/// <item><b>Simulated</b> — development and CI, records everything it is sent.</item>
/// </list>
/// <para>
/// Selection is by capability, never by assuming a browser. On Firefox the first three
/// are skipped because they report themselves unavailable, and the system degrades to a
/// transport that works — reporting honestly what it cannot do rather than failing at
/// the point of sale.
/// </para>
/// </remarks>
public sealed class PrinterResolver
{
    private readonly List<TransportCandidate> _candidates = [];

    /// <summary>A transport plus the preference order it competes at.</summary>
    private sealed record TransportCandidate(int Rank, Func<CancellationToken, ValueTask<IDeviceTransport?>> Factory, string Reason);

    /// <summary>Registers a transport provider at the given preference rank. Lower is preferred.</summary>
    public PrinterResolver Add(int rank, string reason, Func<CancellationToken, ValueTask<IDeviceTransport?>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _candidates.Add(new TransportCandidate(rank, factory, reason));
        return this;
    }

    /// <summary>
    /// The transport chosen by the last successful <see cref="ResolveAsync"/>.
    /// </summary>
    public IDeviceTransport? Selected { get; private set; }

    /// <summary>Why the selected transport was chosen, for the device settings screen.</summary>
    public string? SelectionReason { get; private set; }

    /// <summary>
    /// Resolves the best available transport, trying each preference in order.
    /// </summary>
    /// <param name="preferReconnect">
    /// When true, transports attempt to silently reattach to an already-authorised
    /// device first. This is the normal path on page load.
    /// </param>
    /// <returns>The chosen transport, or null when nothing is usable.</returns>
    public async ValueTask<IDeviceTransport?> ResolveAsync(
        bool preferReconnect = true,
        CancellationToken ct = default)
    {
        foreach (var candidate in _candidates.OrderBy(c => c.Rank))
        {
            ct.ThrowIfCancellationRequested();

            IDeviceTransport? transport = null;

            try
            {
                transport = await candidate.Factory(ct).ConfigureAwait(false);

                if (transport is null)
                {
                    continue;
                }

                if (!await transport.IsAvailableAsync(ct).ConfigureAwait(false))
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                // A transport that works but has no device paired is still the right
                // choice: the operator needs to pair it once, rather than being pushed
                // onto a lesser transport permanently.
                if (preferReconnect && await transport.TryReconnectAsync(ct).ConfigureAwait(false))
                {
                    Selected = transport;
                    SelectionReason = candidate.Reason;
                    return transport;
                }

                if (!preferReconnect)
                {
                    Selected = transport;
                    SelectionReason = candidate.Reason;
                    return transport;
                }

                // Available but not yet paired. Keep it as the answer only if no
                // lower-ranked transport is ready, so a paired fallback still wins.
                if (Selected is null)
                {
                    Selected = transport;
                    SelectionReason = $"{candidate.Reason} (not yet paired)";
                }

                // Continue looking for one that is actually connected.
                if (transport.State.IsReady)
                {
                    SelectionReason = candidate.Reason;
                    return transport;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A transport that throws while probing must not prevent the next one
                // from being tried; a broken browser API should not block a sale.
                if (transport is not null)
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        return Selected;
    }

    /// <summary>
    /// Describes what this terminal can currently do, for the device settings screen.
    /// </summary>
    public string DescribeSelection() => Selected is null
        ? "No printer transport is available in this browser."
        : $"{Selected.DisplayName} ({Selected.State.Status})" +
          (SelectionReason is null ? string.Empty : $" — {SelectionReason}");
}
