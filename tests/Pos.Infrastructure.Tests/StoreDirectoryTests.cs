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
using Pos.Infrastructure.Sync;

namespace Pos.Infrastructure.Tests;

/// <summary>
/// Tests for reading the estate's store directory.
/// </summary>
/// <remarks>
/// The directory is what lets a shop address a transfer to another branch without ever holding the
/// head-office token. Two properties matter and are tested here: it must never throw, because a till
/// that cannot reach the hub must still sell; and it must not offer a destination that the hub will
/// refuse.
/// </remarks>
public sealed class StoreDirectoryTests
{
    private static readonly Guid Own = Guid.Parse("0193f2c1-4d5e-7a8b-9c0d-1e2f3a4b5c6d");
    private static readonly Guid Open = Guid.Parse("0193f2c1-4d5e-7a8b-9c0d-1e2f3a4b5c6e");
    private static readonly Guid Closed = Guid.Parse("0193f2c1-4d5e-7a8b-9c0d-1e2f3a4b5c6f");

    // ------------------------------------------------------------------ the target list

    /// <summary>
    /// A store is not a destination for its own stock, and a closed branch is not a destination
    /// for anyone's.
    /// </summary>
    /// <remarks>
    /// The hub refuses a transfer to its own sender, and sending stock into a shop that has stopped
    /// trading is a mistake a dropdown should not make available.
    /// </remarks>
    [Fact]
    public void TransferTargets_ExcludeThisStoreAndClosedStores()
    {
        var directory = new StoreDirectory(
            Own.ToString("N"),
            [
                new SyncStoreSummary(Own.ToString("N"), "CT01", "Cape Town", true),
                new SyncStoreSummary(Open.ToString("N"), "JN01", "Johannesburg", true),
                new SyncStoreSummary(Closed.ToString("N"), "PM01", "Pietermaritzburg", false),
            ]);

        var target = Assert.Single(directory.TransferTargets);

        Assert.Equal("JN01", target.Code);
    }

    /// <summary>
    /// This store is recognised as itself even when the two ids are spelled differently.
    /// </summary>
    /// <remarks>
    /// The hub reports its own store with the id it issued, and a terminal's store id is that same
    /// value rendered by the domain type. Comparing the strings would leave the till offering to
    /// transfer stock to itself.
    /// </remarks>
    [Fact]
    public void TransferTargets_ExcludeThisStoreSpelledDifferently()
    {
        var directory = new StoreDirectory(
            Own.ToString("N"),
            [
                new SyncStoreSummary(Own.ToString("D"), "CT01", "Cape Town", true),
                new SyncStoreSummary(Open.ToString("N"), "JN01", "Johannesburg", true),
            ]);

        var target = Assert.Single(directory.TransferTargets);

        Assert.Equal("JN01", target.Code);
    }

    [Fact]
    public void AnEmptyDirectory_HasNoTargets() =>
        Assert.Empty(StoreDirectory.Empty.TransferTargets);

    // ------------------------------------------------------------------------ fetching

    [Fact]
    public async Task Fetch_ReadsTheDirectory()
    {
        var handler = new StubHandler(HttpStatusCode.OK, $$"""
            {
              "storeId": "{{Own:N}}",
              "stores": [
                { "id": "{{Own:N}}", "code": "CT01", "name": "Cape Town", "isActive": true },
                { "id": "{{Open:N}}", "code": "JN01", "name": "Johannesburg", "isActive": true }
              ]
            }
            """);

        var directory = await Client(handler).FetchAsync();

        Assert.Equal(2, directory.Stores.Count);
        Assert.Equal("CT01", directory.Stores[0].Code);

        // The caller's own store, so the screen can tell which branch it is looking at.
        Assert.Equal(Own.ToString("N"), directory.OwnStoreId);
    }

    /// <summary>
    /// The request carries the device credential, because this endpoint is not public.
    /// </summary>
    /// <remarks>
    /// Asserted rather than assumed. The directory is reachable without the head-office token, and
    /// the only thing keeping it to enrolled terminals is this header.
    /// </remarks>
    [Fact]
    public async Task Fetch_SendsTheDeviceCredential()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"storeId":"x","stores":[]}""");

        await Client(handler).FetchAsync();

        Assert.Equal("Bearer secret", handler.Authorization);
    }

    /// <summary>
    /// A hub that is down, slow, or replaced by a captive portal yields no destinations rather than
    /// an exception.
    /// </summary>
    /// <remarks>
    /// This is the property that keeps a shop trading. The directory is a convenience for raising a
    /// transfer; failing to read it must not take the transfers screen, or the till, down with it.
    /// </remarks>
    [Fact]
    public async Task Fetch_WhenTheHubFails_ReturnsNothingRatherThanThrowing()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "boom");

        var directory = await Client(handler).FetchAsync();

        Assert.Empty(directory.Stores);
    }

    [Fact]
    public async Task Fetch_WhenTheNetworkIsUnreachable_ReturnsNothing()
    {
        var handler = new ThrowingHandler(new HttpRequestException("no route to host"));

        var directory = await Client(handler).FetchAsync();

        Assert.Empty(directory.Stores);
    }

    /// <summary>
    /// A captive portal answering with a sign-in page must not be mistaken for a directory.
    /// </summary>
    [Fact]
    public async Task Fetch_WhenTheResponseIsNotJson_ReturnsNothing()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "<html>Hotel WiFi</html>");

        var directory = await Client(handler).FetchAsync();

        Assert.Empty(directory.Stores);
    }

    /// <summary>An unenrolled terminal has no hub to ask, so it asks nobody.</summary>
    [Fact]
    public async Task Fetch_WhenNotEnrolled_DoesNotCallTheHub()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"storeId":"x","stores":[]}""");

        var client = new StoreDirectoryClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://hub.example.com/") },
            new SyncEndpoint(new Uri("https://hub.example.com/"), string.Empty, string.Empty));

        var directory = await client.FetchAsync();

        Assert.Empty(directory.Stores);
        Assert.Null(handler.Authorization);
    }

    /// <summary>A hub that omits the store list yields an empty one rather than a null reference.</summary>
    [Fact]
    public async Task Fetch_WithNoStoreList_ReturnsNothing()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"storeId":"x"}""");

        var directory = await Client(handler).FetchAsync();

        Assert.Empty(directory.Stores);
    }

    private static StoreDirectoryClient Client(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://hub.example.com/") },
            new SyncEndpoint(
                new Uri("https://hub.example.com/"),
                "secret",
                Own.ToString("N")));

    /// <summary>A hub that answers with a fixed status and body.</summary>
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        /// <summary>The Authorization header the last request carried, if any.</summary>
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>A hub that cannot be reached at all.</summary>
    private sealed class ThrowingHandler(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(failure);
    }
}
