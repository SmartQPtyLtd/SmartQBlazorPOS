// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Devices.Transport;

namespace Pos.Devices.Tests;

/// <summary>
/// Tests for how a transport decides which already-authorised device it may attach to.
/// </summary>
/// <remarks>
/// A shop till has a receipt printer and a kitchen printer. A transport that simply takes the
/// first authorised device it can open lets both roles grab the same one, so a sale prints the
/// kitchen ticket on the receipt printer depending on which resolved first. That is worse than a
/// printer that does not work, because it looks like it did.
/// </remarks>
public sealed class PrinterBindingTests
{
    private static readonly string[] TwoDevices = ["usb-aaa", "usb-bbb"];

    [Fact]
    public void An_unbound_role_may_use_any_authorised_device()
    {
        // The single-printer shop, and the behaviour every transport had before roles existed.
        var selection = PrinterBinding.Any.Select(TwoDevices);

        Assert.True(selection.HasCandidates);
        Assert.Equal(TwoDevices, selection.Usable);
        Assert.Null(selection.Refusal);
    }

    [Fact]
    public void A_bound_role_may_use_only_its_own_device()
    {
        var selection = PrinterBinding.Only("usb-bbb").Select(TwoDevices);

        Assert.True(selection.HasCandidates);
        Assert.Equal(["usb-bbb"], selection.Usable);
    }

    [Fact]
    public void A_bound_role_never_falls_back_to_another_printer()
    {
        // The important one. If the kitchen printer is unplugged, quietly printing kitchen tickets
        // on the receipt printer sends food orders to the customer-facing roll — and the operator
        // has no reason to look, because a ticket came out.
        var selection = PrinterBinding.Only("usb-ccc").Select(TwoDevices);

        Assert.False(selection.HasCandidates);
        Assert.Empty(selection.Usable);
        Assert.Contains("not available", selection.Refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unpaired_second_role_attaches_to_nothing()
    {
        // So that pairing the kitchen printer cannot re-point the receipt printer. A second role
        // waits to be paired rather than claiming a device nobody gave it.
        var selection = PrinterBinding.Unpaired.Select(TwoDevices);

        Assert.False(selection.HasCandidates);
        Assert.Contains("has been paired", selection.Refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unpaired_role_says_so_when_nothing_is_authorised_at_all()
    {
        // A different message from the above, because the operator's next action is different:
        // one role needs pairing, the other has nothing to pair with.
        var selection = PrinterBinding.Unpaired.Select([]);

        Assert.False(selection.HasCandidates);
        Assert.NotNull(selection.Refusal);
    }

    [Fact]
    public void An_unbound_role_with_nothing_authorised_says_nothing_is_paired()
    {
        var selection = PrinterBinding.Any.Select([]);

        Assert.False(selection.HasCandidates);
        Assert.Contains("paired", selection.Refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_binding_is_bound_only_when_it_names_a_device()
    {
        Assert.True(PrinterBinding.Only("usb-aaa").IsBound);
        Assert.False(PrinterBinding.Any.IsBound);
        Assert.False(PrinterBinding.Unpaired.IsBound);

        // Whitespace is not a device.
        Assert.False(new PrinterBinding("   ", ClaimUnboundDevices: false).IsBound);
    }

    [Fact]
    public void Matching_a_bound_device_is_exact()
    {
        // A prefix match would let "usb-aaa" claim "usb-aaab", and connection ids are opaque.
        var selection = PrinterBinding.Only("usb-aa").Select(TwoDevices);

        Assert.False(selection.HasCandidates);
    }

    [Fact]
    public void The_refusal_names_what_kind_of_device_is_missing()
    {
        // "The paired printer is not available" and "no serial printer has been paired" lead an
        // operator to different actions, so the noun is carried through.
        var selection = PrinterBinding.Only("usb-ccc").Select(TwoDevices, deviceNoun: "serial printer");

        Assert.Contains("serial printer", selection.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_connection_list_is_a_programming_error_not_a_silent_pass()
    {
        Assert.Throws<ArgumentNullException>(() => PrinterBinding.Any.Select(null!));
    }
}
