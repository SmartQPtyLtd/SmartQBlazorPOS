// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Net;
using System.Text;

namespace Pos.Web.Tests;

/// <summary>
/// Stands in for the hub the terminal talks to.
/// </summary>
/// <remarks>
/// <para>
/// Only the store directory is served, because that is the one hub call a screen makes while it is
/// being rendered: the transfers screen reads the estate's branch list so it can offer somewhere to
/// send stock. Everything else the terminal does with a hub goes through the sync transport, which
/// has its own tests and is not exercised by rendering a page.
/// </para>
/// <para>
/// The default answer is an enrolled till's view of a two-store estate. A terminal that knows no
/// other branch cannot raise a transfer at all, so letting this fail would test the emptier path and
/// quietly skip the one with the interesting logic in it.
/// </para>
/// </remarks>
internal sealed class StubHubHandler : HttpMessageHandler
{
    private readonly Func<string>? _body;

    /// <summary>Every path requested, so a test can assert what the page asked for.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>
    /// A hub whose store directory is <paramref name="body"/>, or a two-store estate.
    /// </summary>
    /// <param name="body">JSON to return for any request.</param>
    /// <param name="status">Status to answer with.</param>
    public StubHubHandler(string? body = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        _body = () => body ?? EmptyEstate;
        Status = status;
    }

    /// <summary>A hub that serves the estate's branch list, built when asked.</summary>
    /// <remarks>
    /// Built per request rather than up front because the till's own store id is not known until the
    /// terminal has started, and the directory has to name that same store for the screen to
    /// recognise it as itself.
    /// </remarks>
    /// <param name="ownStoreId">The till's own store id.</param>
    /// <param name="others">Other branches, as (id, code, name, isActive).</param>
    public StubHubHandler(
        Func<string> ownStoreId,
        IReadOnlyList<(string Id, string Code, string Name, bool IsActive)> others)
    {
        ArgumentNullException.ThrowIfNull(ownStoreId);
        ArgumentNullException.ThrowIfNull(others);

        _body = () =>
        {
            var stores = new List<string>
            {
                $$"""{ "id": "{{ownStoreId()}}", "code": "CT01", "name": "Cape Town", "isActive": true }""",
            };

            stores.AddRange(others.Select(s =>
                $$"""{ "id": "{{s.Id}}", "code": "{{s.Code}}", "name": "{{s.Name}}", "isActive": {{(s.IsActive ? "true" : "false")}} }"""));

            return $$"""{ "storeId": "{{ownStoreId()}}", "stores": [ {{string.Join(",", stores)}} ] }""";
        };

        Status = HttpStatusCode.OK;
    }

    /// <summary>Status this hub answers with.</summary>
    public HttpStatusCode Status { get; }

    /// <summary>
    /// The default answer: an estate with no other branch in it.
    /// </summary>
    /// <remarks>
    /// Deliberately the empty estate rather than an invented one. A hub that named a branch the till
    /// could send stock to would have to name the till's own store too, and only the caller knows
    /// what that id is — inventing one would leave the screen offering to transfer stock to itself,
    /// which is a state no hub produces.
    /// </remarks>
    private const string EmptyEstate = """{ "storeId": "", "stores": [] }""";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Requests.Add(request.RequestUri?.AbsolutePath ?? string.Empty);

        var body = _body?.Invoke() ?? "{}";

        return Task.FromResult(new HttpResponseMessage(Status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}
