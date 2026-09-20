// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Microsoft.EntityFrameworkCore;
using Pos.Sync.Server.Data;
using Pos.Sync.Server.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// The hub is a single deployable: sync API plus the Blazor WASM till hosted on the same
// origin. Serving both from one HTTPS origin is what satisfies WebUSB's secure-context
// requirement for terminals on the shop LAN, with no separate certificate to manage.
builder.Services.AddDbContext<SyncDbContext>(options =>
{
    var path = builder.Configuration["Sync:DatabasePath"] ?? "data/pos-sync.db";
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    options.UseSqlite($"Data Source={path}");
});

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

// The head-office credential gates every endpoint that can create a store, mint an enrolment
// code, or revoke a terminal. It is read from configuration rather than the database, so a dumped
// database yields nothing that can provision anything.
//
// When it is absent the endpoints refuse everything rather than allowing everything. A hub that
// looks configured, runs happily, and silently hands out device credentials to anyone who asks is
// the worst of both worlds: the operator believes the estate is closed.
builder.Services.AddSingleton(new Pos.Sync.Server.Auth.HeadOfficeCredential(
    builder.Configuration[Pos.Sync.Server.Auth.HeadOfficeCredential.ConfigurationKey]));

// Terminals push batches of sales; the default 8 KB header limit is ample but the body
// limit needs headroom for a large offline backlog.
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Create the schema on first run so a franchise can stand the hub up with no migration
// tooling, which matters when the person deploying it is a shop owner rather than a DBA.
// Then check it: EnsureCreated never alters an existing table, so a hub that has been running
// since before a column was added would otherwise fail on every authenticated request with a
// database error that names neither the cause nor the fix.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
    await db.Database.EnsureCreatedAsync();
    await SchemaGuard.VerifyAsync(db, app.Logger);
}

app.MapHealthChecks("/health");
app.MapSyncEndpoints();
app.MapEnrollmentEndpoints();
app.MapDeviceCredentialEndpoints();
app.MapHeadOfficeEndpoints();

// Say so plainly at startup when the estate is open. Every head-office endpoint will refuse, and
// an operator who cannot provision their first store needs to know why before they start reading
// logs.
if (!app.Services.GetRequiredService<Pos.Sync.Server.Auth.HeadOfficeCredential>().IsConfigured)
{
    Pos.Sync.Server.ServerLog.HeadOfficeNotConfigured(
        app.Logger,
        Pos.Sync.Server.Auth.HeadOfficeCredential.MinimumLength);
}

// Serve the terminal UI from the same origin. Blazor's published output is copied into
// wwwroot by the target in the server project file, and is served as plain static files:
// no additional hosting package is needed, because nothing here does server-side Blazor
// rendering. When the UI has not been published yet the API still runs, and the Blazor dev
// server is used during development instead.
if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html")))
{
    app.UseStaticFiles();
    app.MapFallbackToFile("index.html");
}

app.Run();

/// <summary>
/// Exposed so integration tests can drive the real host through WebApplicationFactory.
/// </summary>
public partial class Program;
