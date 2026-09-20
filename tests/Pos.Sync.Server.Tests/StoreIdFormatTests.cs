// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Infrastructure.Sync;

namespace Pos.Sync.Server.Tests;

/// <summary>
/// Tests for comparing store ids written in different forms.
/// </summary>
/// <remarks>
/// This exists because comparing them as strings silently broke transfer relaying. The hub mints
/// store ids without dashes and the till renders them with, so a transfer addressed by a real till
/// matched no store and was never relayed — while every test passed, because the tests had been
/// written using the hub's own spelling.
/// </remarks>
public sealed class StoreIdFormatTests
{
    private static readonly Guid Store = Guid.Parse("0193f2c1-4d5e-7a8b-9c0d-1e2f3a4b5c6d");

    [Fact]
    public void SameStore_SpelledDifferently_IsTheSameStore()
    {
        Assert.True(StoreIdFormat.Same(Store.ToString("N"), Store.ToString("D")));
        Assert.True(StoreIdFormat.Same(Store.ToString("D"), Store.ToString("N")));
        Assert.True(StoreIdFormat.Same(Store.ToString("B"), Store.ToString("N")));
        Assert.True(StoreIdFormat.Same(Store.ToString("D"), Store.ToString("D").ToUpperInvariant()));
    }

    [Fact]
    public void DifferentStores_AreNotTheSameStore()
    {
        var other = Guid.CreateVersion7();

        Assert.False(StoreIdFormat.Same(Store.ToString("N"), other.ToString("N")));
        Assert.False(StoreIdFormat.Same(Store.ToString("D"), other.ToString("D")));
    }

    /// <summary>
    /// An id that is not a GUID is compared exactly, not treated as equal to everything.
    /// </summary>
    /// <remarks>
    /// A hub that predates GUID store ids, or a hand-written configuration, must not suddenly match
    /// whichever store happens to be first in the list.
    /// </remarks>
    [Fact]
    public void ANonGuidId_MatchesOnlyItself()
    {
        Assert.True(StoreIdFormat.Same("MAIN", "MAIN"));
        Assert.False(StoreIdFormat.Same("MAIN", "BRANCH"));
        Assert.False(StoreIdFormat.Same("MAIN", Store.ToString("N")));
    }

    /// <summary>An absent id matches nothing, including another absent id.</summary>
    /// <remarks>
    /// Two unset values are not evidence of the same store. Treating them as equal would let a
    /// payload with no destination at all be relayed to whichever store had no id.
    /// </remarks>
    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "0193f2c1-4d5e-7a8b-9c0d-1e2f3a4b5c6d")]
    [InlineData("", "0193f2c1-4d5e-7a8b-9c0d-1e2f3a4b5c6d")]
    [InlineData("  ", "0193f2c1-4d5e-7a8b-9c0d-1e2f3a4b5c6d")]
    public void AnAbsentId_MatchesNothing(string? left, string? right)
    {
        Assert.False(StoreIdFormat.Same(left, right));
    }

    [Fact]
    public void Canonical_AnIdWithoutDashes_IsUnchanged()
    {
        Assert.Equal(Store.ToString("N"), StoreIdFormat.Canonical(Store.ToString("N")));
    }

    [Fact]
    public void Canonical_AnIdWithDashes_LosesThem()
    {
        Assert.Equal(Store.ToString("N"), StoreIdFormat.Canonical(Store.ToString("D")));
    }

    /// <summary>A non-GUID id is passed through rather than mangled into a GUID.</summary>
    [Fact]
    public void Canonical_ANonGuidId_IsUnchanged()
    {
        Assert.Equal("MAIN", StoreIdFormat.Canonical("MAIN"));
    }
}
