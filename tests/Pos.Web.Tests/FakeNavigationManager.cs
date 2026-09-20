// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Microsoft.AspNetCore.Components;

namespace Pos.Web.Tests;

/// <summary>
/// A navigation manager that records where a page tried to go instead of moving.
/// </summary>
/// <remarks>
/// The Blazor host supplies the real one. Providing a fake is what allows a page that navigates in
/// response to a click — or, in the enrolment path, one that forces a full reload — to be rendered
/// without a browser attached.
/// </remarks>
internal sealed class FakeNavigationManager : NavigationManager
{
    public FakeNavigationManager(string baseUri = "http://localhost:5043/")
    {
        Initialize(baseUri, baseUri);
    }

    /// <summary>Every location navigated to, in order.</summary>
    public List<string> Navigations { get; } = [];

    /// <summary>True when the last navigation asked for a full page load.</summary>
    public bool LastNavigationForced { get; private set; }

    /// <summary>The URI most recently navigated to, or null.</summary>
    public string? LastNavigation => Navigations.Count > 0 ? Navigations[^1] : null;

    protected override void NavigateToCore(string uri, bool forceLoad)
    {
        Navigations.Add(uri);
        LastNavigationForced = forceLoad;

        Uri = ToAbsoluteUri(uri).ToString();
    }
}
