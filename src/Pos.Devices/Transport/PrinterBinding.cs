// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

namespace Pos.Devices.Transport;

/// <summary>
/// Which already-authorised device a transport is allowed to attach to.
/// </summary>
/// <remarks>
/// <para>
/// This exists because a shop till has more than one printer. The receipt printer and the kitchen
/// printer are separate physical devices, and a transport that simply takes the first authorised
/// connection it can open would let both of them grab the same one — so a sale would print the
/// kitchen ticket on the receipt printer, or the receipt on the kitchen printer, depending on
/// which resolved first. That is worse than a printer that does not work, because it looks like it
/// did.
/// </para>
/// <para>
/// Three states, and the difference between the last two matters:
/// </para>
/// <list type="bullet">
/// <item><b>Bound</b> — this role has been paired with a specific device, and may use only that
/// one. A device that has been unplugged is reported as such, rather than silently swapping to a
/// different printer and sending a customer's receipt to the kitchen.</item>
/// <item><b>Unpaired</b> — this role has never been paired. It attaches to nothing, so that an
/// operator pairing the kitchen printer cannot accidentally re-point the receipt printer.</item>
/// <item><b>Any</b> — no role separation, which is what a single-printer shop has always done and
/// what the transport's original behaviour was.</item>
/// </list>
/// </remarks>
/// <param name="ConnectionId">The specific connection this role is bound to, if any.</param>
/// <param name="ClaimUnboundDevices">
/// Whether this role may attach to an authorised device nobody has claimed. False for a second
/// printer, so it waits to be paired rather than stealing the first one.
/// </param>
public readonly record struct PrinterBinding(string? ConnectionId, bool ClaimUnboundDevices)
{
    /// <summary>Attach to any authorised device. The single-printer behaviour.</summary>
    public static PrinterBinding Any => new(null, true);

    /// <summary>Attach only to one specific device, already paired for this role.</summary>
    public static PrinterBinding Only(string connectionId) => new(connectionId, false);

    /// <summary>Never attach; this role has not been paired.</summary>
    public static PrinterBinding Unpaired => new(null, false);

    /// <summary>True when this role is tied to one specific device.</summary>
    public bool IsBound => !string.IsNullOrWhiteSpace(ConnectionId);

    /// <summary>Outcome of narrowing a set of authorised connections to this role's own.</summary>
    /// <param name="Usable">Connections this transport may try to open, in order.</param>
    /// <param name="Refusal">
    /// Why nothing can be tried, for the operator. Null when <paramref name="Usable"/> is non-empty.
    /// </param>
    public readonly record struct Selection(IReadOnlyList<string> Usable, string? Refusal)
    {
        /// <summary>True when there is something to attempt.</summary>
        public bool HasCandidates => Usable.Count > 0;
    }

    /// <summary>
    /// Narrows the connections the browser has already granted to the ones this role may use.
    /// </summary>
    /// <param name="authorised">Every connection this origin has been granted.</param>
    /// <param name="deviceNoun">What to call the device in a refusal, e.g. "printer".</param>
    public Selection Select(IReadOnlyList<string> authorised, string deviceNoun = "printer")
    {
        ArgumentNullException.ThrowIfNull(authorised);

        if (IsBound)
        {
            // Only ever the paired device. Falling back to another one here would silently send a
            // document to the wrong machine, which on a kitchen ticket means food nobody ordered.
            return authorised.Contains(ConnectionId!, StringComparer.Ordinal)
                ? new Selection([ConnectionId!], null)
                : new Selection(
                    [],
                    $"The paired {deviceNoun} is not available. Plug it in, or pair another one.");
        }

        if (!ClaimUnboundDevices)
        {
            return new Selection(
                [],
                $"No {deviceNoun} has been paired for this role yet.");
        }

        return authorised.Count == 0
            ? new Selection([], $"No {deviceNoun} has been paired with this terminal yet.")
            : new Selection(authorised, null);
    }
}
