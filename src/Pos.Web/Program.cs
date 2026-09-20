// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Pos.Web;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The whole composition root lives in TerminalServices.AddPosTerminal, rather than here.
//
// Top-level statements cannot be called from a test, so while this file held the registrations the
// only thing that ever checked them was a browser loading the page. A mistake there is not a wrong
// number on a report — it is a till that does not start, discovered by a cashier at opening time.
// Extracted, a test can build the same container, validate it, and resolve every service.
builder.Services.AddPosTerminal(new Uri(builder.HostEnvironment.BaseAddress));

await builder.Build().RunAsync();
