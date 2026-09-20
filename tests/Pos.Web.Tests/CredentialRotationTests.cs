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
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pos.Infrastructure.Sync;
using Pos.Web.Terminal;

namespace Pos.Web.Tests;

/// <summary>
/// Tests for rotating a till's device credential.
/// </summary>
/// <remarks>
/// <para>
/// The rule these exist to protect is the order of operations: a replacement is written down before it
/// is sent, and the credential in use only changes once the hub has confirmed it. Get that wrong and the
/// failure is not a failed rotation — it is a till that presents a superseded refresh token with
/// different credentials, which the hub reads as a stolen token and revokes the device for.
/// </para>
/// <para>
/// The connection is a stub, so the lost-response case is a first call that throws and a second that
/// succeeds. That is the whole reason the terminal generates the replacement rather than receiving it.
/// </para>
/// </remarks>
public sealed class CredentialRotationTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_due_rotation_replaces_the_stored_credential()
    {
        var harness = await EnrolledAsync(dueIn: TimeSpan.FromDays(-1));

        await harness.RotateAsync();

        var sent = Assert.Single(harness.Handler.Requests);

        Assert.Equal("/api/devices/rotate", sent.Path);

        // The refresh token travels in its own header, and the pair that goes in the body is the one the
        // till then adopts. Asserting on the body rather than on a returned value is the point: nothing
        // about the replacement comes back from the hub.
        Assert.Equal(TerminalSeed.TestRefreshToken, sent.RefreshToken);

        var body = JsonDocument.Parse(sent.Body).RootElement;

        Assert.Equal(harness.Identity.DeviceSecret, body.GetProperty("newSecret").GetString());
        Assert.Equal(harness.Identity.RefreshToken, body.GetProperty("newRefreshToken").GetString());
        Assert.NotEqual(TerminalSeed.TestSecret, harness.Identity.DeviceSecret);

        Assert.Null(harness.Identity.PendingRotation);
        Assert.Equal(Now.AddDays(7), harness.Identity.RotateAfter);
    }

    [Fact]
    public async Task Nothing_is_sent_before_the_credential_falls_due()
    {
        // A rotation on every sync would churn credentials for no benefit, and the hub would be
        // replacing a secret it has only just issued.
        var harness = await EnrolledAsync(dueIn: TimeSpan.FromDays(3));

        await harness.RotateAsync();

        Assert.Empty(harness.Handler.Requests);
        Assert.Equal(TerminalSeed.TestSecret, harness.Identity.DeviceSecret);
    }

    [Fact]
    public async Task An_unenrolled_terminal_never_rotates()
    {
        var browser = new Dictionary<string, string?>();

        var identity = new TerminalIdentity(new FakeJsRuntime(browser));
        await identity.InitialiseAsync();

        var handler = new RotationHandler();
        var preflight = Preflight(identity, handler);

        Assert.False(identity.RotationDue(Now));

        await preflight.PrepareAsync();

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_lost_response_is_retried_with_the_same_replacement()
    {
        // The heart of the design. The first attempt reaches the hub and the answer does not come back,
        // so the till must ask again for exactly what it already asked for. A fresh pair here would look
        // to the hub like a second party holding the refresh token.
        var harness = await EnrolledAsync(dueIn: TimeSpan.FromDays(-1));

        harness.Handler.Answers.Enqueue(() => throw new HttpRequestException("the connection dropped"));
        harness.Handler.Answers.Enqueue(() => Rotated());

        await harness.RotateAsync();
        await harness.RotateAsync();

        Assert.Equal(2, harness.Handler.Requests.Count);
        Assert.Equal(harness.Handler.Requests[0].Body, harness.Handler.Requests[1].Body);
        Assert.Equal(harness.Handler.Requests[0].RefreshToken, harness.Handler.Requests[1].RefreshToken);
    }

    [Fact]
    public async Task A_replacement_is_written_down_before_it_is_sent()
    {
        // If the replacement were held in memory until the hub confirmed it, a browser closed mid-request
        // would leave the till with no record of the pair it had asked for — and no safe way to retry.
        var harness = await EnrolledAsync(dueIn: TimeSpan.FromDays(-1));

        harness.Handler.Answers.Enqueue(() => throw new HttpRequestException("the connection dropped"));

        await harness.RotateAsync();

        Assert.NotNull(harness.Identity.PendingRotation);

        // Rebuilt from the same storage, the way a reload does it.
        var reloaded = new TerminalIdentity(new FakeJsRuntime(harness.Browser));
        await reloaded.InitialiseAsync();

        Assert.Equal(
            harness.Identity.PendingRotation!.Secret,
            reloaded.PendingRotation!.Secret);

        Assert.Equal(
            harness.Identity.PendingRotation.RefreshToken,
            reloaded.PendingRotation.RefreshToken);

        Assert.True(reloaded.RotationDue(Now));
    }

    [Fact]
    public async Task A_failed_rotation_keeps_using_the_credential_the_hub_already_knows()
    {
        // A shop must not stop selling because housekeeping failed. The old secret is inside its grace
        // window and still works, so that is what the till keeps presenting.
        var harness = await EnrolledAsync(dueIn: TimeSpan.FromDays(-1));

        harness.Handler.Answers.Enqueue(() => throw new HttpRequestException("the hub is unreachable"));

        await harness.RotateAsync();

        Assert.Equal(TerminalSeed.TestSecret, harness.Identity.DeviceSecret);
        Assert.Equal(TerminalSeed.TestRefreshToken, harness.Identity.RefreshToken);
    }

    [Fact]
    public async Task A_revoked_terminal_reports_an_authorisation_failure_rather_than_retrying()
    {
        // The hub revokes a device when a superseded refresh token is used to mint different
        // credentials. Retrying would be pointless, and the screen that handles a revoked terminal is
        // reached through the same exception a rejected push raises.
        var harness = await EnrolledAsync(dueIn: TimeSpan.FromDays(-1));

        harness.Handler.Answers.Enqueue(() => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"title":"Device is not authorised"}""", Encoding.UTF8, "application/json"),
        });

        await Assert.ThrowsAsync<SyncAuthorisationException>(() => harness.RotateAsync());
    }

    [Fact]
    public async Task Enrolling_again_discards_a_rotation_that_was_left_half_done()
    {
        // A pending pair belongs to the credential it was generated for. Carrying it into a new
        // enrolment would present a replacement that the new credential never asked for.
        var harness = await EnrolledAsync(dueIn: TimeSpan.FromDays(-1));

        await harness.Identity.StageRotationAsync(DeviceCredentialGenerator.NewPair());

        Assert.True(harness.Identity.RotationDue(Now));

        await harness.Identity.SaveEnrolmentAsync(
            "store-2",
            "fresh-secret",
            "fresh-refresh",
            Now.AddDays(7),
            "https://hub.example.com/");

        Assert.Null(harness.Identity.PendingRotation);
        Assert.False(harness.Identity.RotationDue(Now));
    }

    [Fact]
    public async Task A_till_enrolled_before_rotation_existed_keeps_working()
    {
        // A terminal in the field may be older than this feature. It has no refresh token, so it cannot
        // rotate; what it must not do is stop syncing or send a rotation it cannot authorise.
        var browser = new Dictionary<string, string?>
        {
            ["pos.terminalId"] = "legacy-terminal",
            ["pos.storeId"] = "legacy-store",
            ["pos.deviceSecret"] = "a-legacy-secret-that-is-long-enough-01",
            ["pos.hub"] = "https://hub.example.com/",
        };

        var identity = new TerminalIdentity(new FakeJsRuntime(browser));
        await identity.InitialiseAsync();

        var handler = new RotationHandler();
        var preflight = Preflight(identity, handler);

        // No due date was ever recorded, so nothing is due.
        Assert.False(identity.RotationDue(Now));

        await preflight.PrepareAsync();

        Assert.Empty(handler.Requests);
        Assert.True(identity.IsEnrolled);
    }

    [Fact]
    public async Task Clearing_the_credential_clears_everything_a_rotation_left_behind()
    {
        // Revocation wipes the credential. A refresh token or a pending replacement surviving that would
        // be a credential the operator believes is gone.
        var harness = await EnrolledAsync(dueIn: TimeSpan.FromDays(-1));

        await harness.Identity.StageRotationAsync(DeviceCredentialGenerator.NewPair());
        await harness.Identity.ClearEnrolmentAsync();

        var reloaded = new TerminalIdentity(new FakeJsRuntime(harness.Browser));
        await reloaded.InitialiseAsync();

        Assert.Null(reloaded.RefreshToken);
        Assert.Null(reloaded.PendingRotation);
        Assert.Null(reloaded.RotateAfter);
        Assert.False(reloaded.IsEnrolled);
    }

    // ------------------------------------------------------------------ harness

    /// <summary>An enrolled till with a stub hub behind it.</summary>
    private sealed record Harness(
        TerminalIdentity Identity,
        Dictionary<string, string?> Browser,
        RotationHandler Handler)
    {
        /// <summary>Runs one sync pass's worth of rotation.</summary>
        public Task RotateAsync() => Preflight(Identity, Handler).PrepareAsync();
    }

    private static CredentialRotationPreflight Preflight(TerminalIdentity identity, RotationHandler handler) =>
        new(
            identity,
            new DeviceRotationClient(
                new HttpClient(handler) { BaseAddress = new Uri("https://hub.example.com/") },
                new SyncEndpoint(
                    new Uri("https://hub.example.com/"),
                    identity.DeviceSecret ?? TerminalSeed.TestSecret,
                    identity.StoreId ?? "store")),
            NullLogger<CredentialRotationPreflight>.Instance);

    /// <summary>
    /// An enrolled till whose credential falls due in <paramref name="dueIn"/>.
    /// </summary>
    /// <remarks>
    /// Relative to the wall clock rather than to the fixed <see cref="Now"/> above. The preflight asks
    /// the system what the time is, as it should, so a due date written as an absolute instant would make
    /// these tests pass or fail according to the day they were run.
    /// </remarks>
    private static async Task<Harness> EnrolledAsync(TimeSpan dueIn)
    {
        var browser = new Dictionary<string, string?>();

        var identity = new TerminalIdentity(new FakeJsRuntime(browser));
        await identity.InitialiseAsync();

        await identity.SaveEnrolmentAsync(
            Guid.CreateVersion7().ToString("N"),
            TerminalSeed.TestSecret,
            TerminalSeed.TestRefreshToken,
            DateTimeOffset.UtcNow + dueIn,
            "https://hub.example.com/");

        return new Harness(identity, browser, new RotationHandler());
    }

    /// <summary>The hub's answer to a rotation, with the pair the till asked for echoed nowhere.</summary>
    private static HttpResponseMessage Rotated() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                deviceId = "device-1",
                rotateAfter = Now.AddDays(7),
            }),
            Encoding.UTF8,
            "application/json"),
    };

    /// <summary>One rotation request, as the hub would see it.</summary>
    private sealed record Request(string Path, string? RefreshToken, string Body);

    /// <summary>
    /// A hub that records what a rotation sent and answers however the test says.
    /// </summary>
    /// <remarks>
    /// Records the refresh header and the body rather than only the path, because the two properties
    /// under test are which token was presented and whether a retry asked for the same replacement.
    /// </remarks>
    private sealed class RotationHandler : HttpMessageHandler
    {
        /// <summary>Queued answers. Anything left over is answered as a successful rotation.</summary>
        public Queue<Func<HttpResponseMessage>> Answers { get; } = new();

        public List<Request> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            var refresh = request.Headers.TryGetValues(SyncHeaders.RefreshToken, out var values)
                ? string.Join(",", values)
                : null;

            Requests.Add(new Request(request.RequestUri?.AbsolutePath ?? string.Empty, refresh, body));

            var answer = Answers.Count > 0 ? Answers.Dequeue() : Rotated;

            return answer();
        }
    }
}
