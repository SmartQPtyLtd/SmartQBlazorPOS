// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Globalization;
using System.Text.Json;
using Microsoft.JSInterop;
using Pos.Devices.EscPos;
using Pos.Devices.Transport;
using Pos.Infrastructure.Checkout;
using Pos.Infrastructure.Sync;

namespace Pos.Web.Terminal;

/// <summary>
/// Identifies this installation to the hub and holds its credential.
/// </summary>
/// <remarks>
/// <para>
/// The terminal id is generated once and persisted, because it is the sync replica
/// identity: records are ordered by <c>(terminalId, terminalSeq)</c>, so a till that
/// changed its identity on every reload would look like a new device with a restarting
/// sequence, and the hub could no longer detect gaps.
/// </para>
/// <para>
/// The device secret is issued at enrolment and stored in local storage. It is a
/// bearer credential for one store only, which is what bounds the damage if a till in an
/// insecure shop is stolen.
/// </para>
/// </remarks>
public sealed class TerminalIdentity(IJSRuntime js) : ITerminalIdentity
{
    private const string TerminalIdKey = "pos.terminalId";
    private const string SecretKey = "pos.deviceSecret";
    private const string RefreshTokenKey = "pos.refreshToken";
    private const string RotateAfterKey = "pos.rotateAfter";
    private const string PendingRotationKey = "pos.pendingRotation";
    private const string StoreIdKey = "pos.storeId";
    private const string HubKey = "pos.hub";

    private readonly IJSRuntime _js = js ?? throw new ArgumentNullException(nameof(js));

    /// <summary>Stable identity for this till.</summary>
    public string TerminalId { get; private set; } = string.Empty;

    /// <summary>Bearer credential issued at enrolment, or null when not enrolled.</summary>
    public string? DeviceSecret { get; private set; }

    /// <summary>
    /// Presented only when rotating, and never on an ordinary request.
    /// </summary>
    /// <remarks>
    /// A separate value from the secret so that a secret harvested from a request log — a proxy, a
    /// crash dump, a developer tool left open — is not by itself enough to mint a replacement.
    /// </remarks>
    public string? RefreshToken { get; private set; }

    /// <summary>When this credential should be replaced, or null when not enrolled.</summary>
    public DateTimeOffset? RotateAfter { get; private set; }

    /// <summary>
    /// A rotation that has been sent but not yet confirmed by the hub.
    /// </summary>
    /// <remarks>
    /// Persisted <em>before</em> the request goes out, and that order is the whole reason it exists. If
    /// the response is lost the till still holds the exact pair it asked the hub to adopt, so its retry
    /// is the same request and the hub recognises it. Generating a fresh pair instead would present a
    /// superseded refresh token with different credentials, which is indistinguishable from a stolen
    /// token and revokes the device.
    /// </remarks>
    public DeviceCredentialPair? PendingRotation { get; private set; }

    /// <summary>Store the credential is scoped to.</summary>
    public string? StoreId { get; private set; }

    /// <summary>Hub the terminal syncs against, or null when unenrolled.</summary>
    public string? HubAddress { get; private set; }

    /// <summary>True once the terminal has a hub and a credential.</summary>
    public bool IsEnrolled =>
        !string.IsNullOrWhiteSpace(DeviceSecret) &&
        !string.IsNullOrWhiteSpace(StoreId) &&
        !string.IsNullOrWhiteSpace(HubAddress);

    /// <summary>True when this credential is past its rotation date, or a rotation is unconfirmed.</summary>
    /// <remarks>
    /// An unconfirmed rotation makes this true whatever the date says, so a till whose response was lost
    /// retries on its next pass rather than waiting out a week.
    /// </remarks>
    public bool RotationDue(DateTimeOffset now) =>
        IsEnrolled && (PendingRotation is not null || (RotateAfter is { } due && now >= due));

    /// <summary>Loads or creates the persistent identity. Call once at startup.</summary>
    public async Task InitialiseAsync(CancellationToken ct = default)
    {
        TerminalId = await GetOrCreateAsync(TerminalIdKey, ct).ConfigureAwait(false);

        // Created rather than merely read, for the same reason as the terminal id above. The store
        // id keys every local record — sales, refunds, the roster, shifts, stock movements,
        // transfers, and the sale-number sequence — so a till that minted a fresh one on each load
        // would lose its own roster, empty its own catalogue, and reissue receipt numbers that are
        // already printed. It is replaced with the hub's store id at enrolment, which is what makes
        // a transfer addressed to this store receivable here.
        StoreId = await GetOrCreateAsync(StoreIdKey, ct).ConfigureAwait(false);

        DeviceSecret = await GetAsync(SecretKey, ct).ConfigureAwait(false);
        RefreshToken = await GetAsync(RefreshTokenKey, ct).ConfigureAwait(false);
        HubAddress = await GetAsync(HubKey, ct).ConfigureAwait(false);
        RotateAfter = await GetMomentAsync(RotateAfterKey, ct).ConfigureAwait(false);
        PendingRotation = await GetPairAsync(PendingRotationKey, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the credential returned by a successful enrolment.
    /// </summary>
    /// <returns>
    /// True when this enrolment moved the terminal to a different store, meaning everything already
    /// recorded locally belongs to a store these books are not.
    /// </returns>
    /// <remarks>
    /// The return value matters rather than being a courtesy. An unenrolled till trades happily on a
    /// locally-minted store id, queuing its sales for a hub it has never met. Adopting the hub's
    /// store id without clearing that queue would push a demonstration shop's takings into a real
    /// shop's books, and report them as that shop's own.
    /// </remarks>
    public async Task<bool> SaveEnrolmentAsync(
        string storeId,
        string deviceSecret,
        string refreshToken,
        DateTimeOffset rotateAfter,
        string hubAddress,
        CancellationToken ct = default)
    {
        var movedStore = !string.IsNullOrWhiteSpace(StoreId)
            && !string.Equals(StoreId, storeId, StringComparison.Ordinal);

        StoreId = storeId;
        DeviceSecret = deviceSecret;
        RefreshToken = refreshToken;
        RotateAfter = rotateAfter;

        // A fresh enrolment supersedes any rotation left half-done by the credential it replaced.
        // Keeping it would present a pair the new credential never asked for.
        PendingRotation = null;

        HubAddress = hubAddress;

        await SetAsync(StoreIdKey, storeId, ct).ConfigureAwait(false);
        await SetAsync(SecretKey, deviceSecret, ct).ConfigureAwait(false);
        await SetAsync(RefreshTokenKey, refreshToken, ct).ConfigureAwait(false);
        await SetAsync(RotateAfterKey, rotateAfter.ToString("O", CultureInfo.InvariantCulture), ct)
            .ConfigureAwait(false);
        await RemoveAsync(PendingRotationKey, ct).ConfigureAwait(false);
        await SetAsync(HubKey, hubAddress, ct).ConfigureAwait(false);

        return movedStore;
    }

    /// <summary>
    /// Records a rotation that is about to be sent.
    /// </summary>
    /// <remarks>
    /// Must be called before the request, not after it. Written first, a rotation whose response is lost
    /// is retried with the same pair; written after, the till would have to invent a new pair against a
    /// hub that has already rotated, which is the reuse case that revokes a device.
    /// </remarks>
    public async Task StageRotationAsync(DeviceCredentialPair replacement, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        PendingRotation = replacement;

        await SetAsync(PendingRotationKey, JsonSerializer.Serialize(replacement), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Adopts a rotation the hub has confirmed.
    /// </summary>
    /// <remarks>
    /// The credential only changes here, so until the hub has answered, the till keeps authenticating
    /// with the pair the hub already knows. A rotation the hub refused leaves the old pair in use and
    /// the pending one recorded for the next attempt.
    /// </remarks>
    public async Task ConfirmRotationAsync(DateTimeOffset rotateAfter, CancellationToken ct = default)
    {
        if (PendingRotation is not { } confirmed)
        {
            return;
        }

        DeviceSecret = confirmed.Secret;
        RefreshToken = confirmed.RefreshToken;
        RotateAfter = rotateAfter;
        PendingRotation = null;

        await SetAsync(SecretKey, confirmed.Secret, ct).ConfigureAwait(false);
        await SetAsync(RefreshTokenKey, confirmed.RefreshToken, ct).ConfigureAwait(false);
        await SetAsync(RotateAfterKey, rotateAfter.ToString("O", CultureInfo.InvariantCulture), ct)
            .ConfigureAwait(false);
        await RemoveAsync(PendingRotationKey, ct).ConfigureAwait(false);
    }

    /// <summary>Removes the credential, e.g. on revocation.</summary>
    public async Task ClearEnrolmentAsync(CancellationToken ct = default)
    {
        DeviceSecret = null;
        RefreshToken = null;
        RotateAfter = null;
        PendingRotation = null;
        StoreId = null;

        await RemoveAsync(SecretKey, ct).ConfigureAwait(false);
        await RemoveAsync(RefreshTokenKey, ct).ConfigureAwait(false);
        await RemoveAsync(RotateAfterKey, ct).ConfigureAwait(false);
        await RemoveAsync(PendingRotationKey, ct).ConfigureAwait(false);
        await RemoveAsync(StoreIdKey, ct).ConfigureAwait(false);
    }

    /// <summary>Builds the endpoint the sync transport should use.</summary>
    public SyncEndpoint? ToEndpoint() =>
        IsEnrolled
            ? new SyncEndpoint(new Uri(HubAddress!, UriKind.Absolute), DeviceSecret!, StoreId!)
            : null;

    private async Task<DeviceCredentialPair?> GetPairAsync(string key, CancellationToken ct)
    {
        var json = await GetAsync(key, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DeviceCredentialPair>(json);
        }
        catch (JsonException)
        {
            // Unreadable rather than absent: treated as absent, so the till rotates again instead of
            // retrying a pair it cannot reconstruct.
            return null;
        }
    }

    private async Task<DateTimeOffset?> GetMomentAsync(string key, CancellationToken ct)
    {
        var text = await GetAsync(key, ct).ConfigureAwait(false);

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var moment)
            ? moment
            : null;
    }

    private async Task<string> GetOrCreateAsync(string key, CancellationToken ct)
    {
        var existing = await GetAsync(key, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var created = Guid.CreateVersion7().ToString("N");
        await SetAsync(key, created, ct).ConfigureAwait(false);

        return created;
    }

    private async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        try
        {
            return await _js.InvokeAsync<string?>("localStorage.getItem", ct, key).ConfigureAwait(false);
        }
        catch (JSException)
        {
            // Storage can be unavailable in a locked-down browser profile. A terminal
            // without persistence is degraded but must still start.
            return null;
        }
    }

    private async Task SetAsync(string key, string value, CancellationToken ct)
    {
        try
        {
            await _js.InvokeVoidAsync("localStorage.setItem", ct, key, value).ConfigureAwait(false);
        }
        catch (JSException)
        {
            // Ignored for the same reason as above.
        }
    }

    private async Task RemoveAsync(string key, CancellationToken ct)
    {
        try
        {
            await _js.InvokeVoidAsync("localStorage.removeItem", ct, key).ConfigureAwait(false);
        }
        catch (JSException)
        {
            // Ignored.
        }
    }
}

/// <summary>
/// Carries the bearer credential on every hub request.
/// </summary>
/// <remarks>
/// Applied at the message level rather than per call so no future endpoint can
/// accidentally be added without authentication.
/// </remarks>
public sealed class SyncAuthHandler(TerminalIdentity identity) : DelegatingHandler
{
    private readonly TerminalIdentity _identity = identity ?? throw new ArgumentNullException(nameof(identity));

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_identity.IsEnrolled)
        {
            request.Headers.TryAddWithoutValidation(
                SyncHeaders.Authorization,
                $"Bearer {_identity.DeviceSecret}");

            request.Headers.TryAddWithoutValidation(SyncHeaders.StoreId, _identity.StoreId!);
            request.Headers.TryAddWithoutValidation(SyncHeaders.TerminalId, _identity.TerminalId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// What the checkout screen needs to know about the current sale.
/// </summary>
/// <remarks>
/// Scoped to the terminal session so the basket survives navigation between the checkout,
/// tender, and receipt screens without being serialised.
/// </remarks>
public sealed class CheckoutSession
{
    private readonly List<string> _recentScans = [];

    /// <summary>The basket being built. Null until a store is loaded.</summary>
    public Pos.Core.Domain.Cart? Cart { get; set; }

    /// <summary>The store this terminal trades for.</summary>
    public Pos.Core.Domain.Store? Store { get; set; }

    /// <summary>
    /// The operator currently signed in, or null when nobody is.
    /// </summary>
    /// <remarks>
    /// Held so every sale, refund, stock correction, and drawer opening can be attributed to a
    /// person. A terminal with no signed-in operator can still sell, but nothing it records can be
    /// traced to anyone.
    /// </remarks>
    public Pos.Core.Domain.Employee? Employee { get; set; }

    /// <summary>The shift currently open on this terminal, if any.</summary>
    public Pos.Infrastructure.Storage.StoredShift? Shift { get; set; }

    /// <summary>True when an operator is signed in and a shift is running.</summary>
    public bool IsSignedIn => Employee is not null && Shift is not null;

    /// <summary>
    /// Signs the operator out without touching the shift.
    /// </summary>
    /// <remarks>
    /// A plain sign-out is a handover: the shift record stays open in the store, and the next
    /// operator who signs in joins the drawer that is already running. Closing the shift is a
    /// separate act, done on the cash drawer screen, and it signs the operator out as well.
    /// </remarks>
    public void SignOut()
    {
        Employee = null;
        Shift = null;
    }

    /// <summary>The last sale completed, for the receipt screen.</summary>
    public CompletedSale? LastSale { get; set; }

    /// <summary>Barcodes scanned recently, newest first, for the activity strip.</summary>
    public IReadOnlyList<string> RecentScans => _recentScans;

    /// <summary>Records a scan so the operator can see the input was received.</summary>
    public void NoteScan(string barcode)
    {
        _recentScans.Insert(0, barcode);

        if (_recentScans.Count > 8)
        {
            _recentScans.RemoveAt(_recentScans.Count - 1);
        }
    }
}

/// <summary>
/// Remembers which operator signed in on this tab, so a refresh can tell a mid-shift
/// reload apart from an explicit sign-out.
/// </summary>
/// <remarks>
/// The marker lives in session storage: it survives a page refresh in the same tab and dies
/// with the tab, which is exactly the lifetime of "the operator standing at this till". It is
/// written once at sign-in and cleared wherever the session is explicitly signed out. Startup
/// only re-attaches the operator named by the open shift when the marker agrees — without it,
/// a refresh after a sign-out would silently sign the previous operator back in.
/// </remarks>
public sealed class OperatorSignInMarker(IJSRuntime js)
{
    private const string Key = "pos.signedInEmployee";

    private readonly IJSRuntime _js = js ?? throw new ArgumentNullException(nameof(js));

    /// <summary>The employee id recorded at sign-in, or null when this tab was signed out.</summary>
    public async Task<string?> GetAsync(CancellationToken ct = default)
    {
        try
        {
            return await _js.InvokeAsync<string?>("sessionStorage.getItem", ct, Key).ConfigureAwait(false);
        }
        catch (JSException)
        {
            // Storage can be unavailable in a locked-down browser profile. Read as signed out,
            // which is the safe failure: nobody is re-attached without an explicit sign-in.
            return null;
        }
    }

    /// <summary>Records the operator who just signed in.</summary>
    public async Task MarkAsync(string employeeId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(employeeId);

        try
        {
            await _js.InvokeVoidAsync("sessionStorage.setItem", ct, Key, employeeId).ConfigureAwait(false);
        }
        catch (JSException)
        {
            // Ignored for the same reason as above. The cost is being asked to sign in again
            // after a refresh, not a wrong attribution.
        }
    }

    /// <summary>Forgets the operator. Called wherever the session is explicitly signed out.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        try
        {
            await _js.InvokeVoidAsync("sessionStorage.removeItem", ct, Key).ConfigureAwait(false);
        }
        catch (JSException)
        {
            // Ignored.
        }
    }
}

/// <summary>
/// Resolves and holds the terminal's printer.
/// </summary>
/// <remarks>
/// <para>
/// Resolution is asynchronous, and deliberately so. Blazor WebAssembly runs on a single
/// thread, so waiting synchronously on an async operation deadlocks the runtime outright —
/// there is no second thread to complete the task. Anything that reaches the browser's
/// device APIs must therefore be awaited from an async method.
/// </para>
/// <para>
/// The resolved transport is cached for the terminal's lifetime so that every receipt does
/// not re-probe the browser, but reconnection is retried on demand because a printer can be
/// unplugged and plugged back in mid-shift.
/// </para>
/// </remarks>
public sealed class TerminalPrinterProvider(
    PrinterResolver resolver,
    ReceiptRenderer renderer,
    ILogger<TerminalPrinterProvider> logger,
    PrinterRole role = PrinterRole.Receipt)
{
    private readonly PrinterResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly ReceiptRenderer _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    private readonly ILogger<TerminalPrinterProvider> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private ReceiptPrinter? _printer;
    private bool _resolved;

    /// <summary>Which printer this provider is responsible for.</summary>
    public PrinterRole Role => role;

    /// <summary>The printer, or null when nothing has been resolved yet.</summary>
    public IReceiptPrinter? Current => _printer;

    /// <summary>Why this transport was chosen, for the device settings screen.</summary>
    public string SelectionReason => _resolver.DescribeSelection();

    /// <summary>
    /// The printer for this role, or null when there is nowhere to print.
    /// </summary>
    /// <remarks>
    /// Null is only reachable for the kitchen role. The receipt role always resolves to at least
    /// the simulated transport, because a sale the customer cannot prove is not a completed sale.
    /// A kitchen ticket has no such obligation, and inventing a printer for it would be worse than
    /// printing nothing — see the class remarks. Callers that need the receipt guarantee should
    /// use <see cref="RequireAsync"/>, which says so.
    /// </remarks>
    public async Task<IReceiptPrinter?> GetAsync(CancellationToken ct = default)
    {
        if (_resolved)
        {
            return _printer;
        }

        var transport = await _resolver.ResolveAsync(preferReconnect: true, ct).ConfigureAwait(false);

        if (transport is null)
        {
            // Only the receipt role invents a printer for itself.
            //
            // Gating this on "not the receipt role" rather than listing the other roles is
            // deliberate: a role added later gets the safe behaviour by default. An earlier version
            // named Kitchen explicitly, so the label role fell through to the simulated transport
            // and reported labels as printed when nothing was there at all.
            if (role != PrinterRole.Receipt)
            {
                _resolved = true;
                return null;
            }

            // The simulated transport is always available, so in practice this fallback is
            // unreachable; it exists so the till can never be left with no printer at all.
            transport = new SimulatedTransport();
        }

        _printer = new ReceiptPrinter(transport, _renderer);
        _resolved = true;

        TerminalLog.PrinterResolved(_logger, transport.DisplayName, transport.State.Status);

        return _printer;
    }

    /// <summary>
    /// The receipt printer, which is guaranteed to exist.
    /// </summary>
    /// <remarks>
    /// Exists so a call site states which guarantee it is relying on. Ten call sites that print
    /// receipts all depend on a printer being there, and the compiler cannot tell them apart from
    /// a kitchen ticket that legitimately has nowhere to go.
    /// </remarks>
    public async Task<IReceiptPrinter> RequireAsync(CancellationToken ct = default) =>
        await GetAsync(ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException(
            $"No printer is available for the {role} role, which is required to have one.");

    /// <summary>
    /// Whether this role has a printer ready to accept a job.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="GetAsync"/> returning non-null: a transport can be selected and
    /// not yet paired, which for the kitchen means there is nowhere to send a ticket.
    /// </remarks>
    public bool IsReady => _printer?.Transport.State.IsReady == true;

    /// <summary>Forgets the current printer so the next call re-resolves it.</summary>
    public void Reset()
    {
        _printer = null;
        _resolved = false;
    }
}

/// <summary>
/// Starts the terminal: loads the store, seeds a demo catalogue on first run, and
/// reconnects the printer.
/// </summary>
public sealed class TerminalStartup(
    TerminalIdentity identity,
    Pos.Infrastructure.Storage.ILocalStore store,
    Pos.Infrastructure.Storage.IShiftStore roster,
    CheckoutSession session,
    OperatorSignInMarker signInMarker,
    ILogger<TerminalStartup> logger)
{
    private readonly TerminalIdentity _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    private readonly Pos.Infrastructure.Storage.ILocalStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly Pos.Infrastructure.Storage.IShiftStore _roster = roster ?? throw new ArgumentNullException(nameof(roster));
    private readonly CheckoutSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly OperatorSignInMarker _signInMarker =
        signInMarker ?? throw new ArgumentNullException(nameof(signInMarker));
    private readonly ILogger<TerminalStartup> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Runs once when the checkout screen first loads.</summary>
    public async Task InitialiseAsync(CancellationToken ct = default)
    {
        await _identity.InitialiseAsync(ct).ConfigureAwait(false);

        var info = await _store.InitialiseAsync(ct).ConfigureAwait(false);

        if (!info.Persisted)
        {
            // Local data is not a cache. If the browser evicts it, a day of trading is
            // gone, so this is surfaced rather than swallowed.
            TerminalLog.StorageNotDurable(_logger);
        }

        var store = BuildStore(_identity.StoreId);
        _session.Store = store;

        if (_session.Cart is null)
        {
            _session.Cart = new Pos.Core.Domain.Cart(store.Id, store.Currency, store.TaxMode);
        }

        // Demo data is for a terminal nobody has enrolled yet — a machine being evaluated, or a
        // developer's browser. Once this till belongs to a real store, its catalogue and its staff
        // come from head office, and seeding invented products into a shop's books would make the
        // shop's own stock report wrong from the first day.
        if (!_identity.IsEnrolled)
        {
            await SeedCatalogueIfEmptyAsync(store, ct).ConfigureAwait(false);
            await SeedRosterIfEmptyAsync(store, ct).ConfigureAwait(false);
        }

        // Whatever shift the previous session left open is picked back up, so a browser refresh
        // mid-shift does not silently orphan a drawer with cash in it.
        await RestoreSessionAsync(store, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-attaches to an operator and shift that were already running on this terminal.
    /// </summary>
    /// <remarks>
    /// A refresh is the most ordinary event in a browser, and it must not look to the till like
    /// the drawer was never opened. Without this the next sale would record no shift, and a
    /// cash-up would come up short by exactly the takings since the reload with nothing to
    /// explain them.
    /// </remarks>
    /// <para>
    /// The restore is gated on the sign-in marker. An explicit sign-out clears it, so a refresh
    /// after a sign-out — or a marker left by a different operator — restores nobody. The shift
    /// itself is untouched either way: it stays open in the store, and the next sign-in joins it.
    /// </para>
    private async Task RestoreSessionAsync(Pos.Core.Domain.Store store, CancellationToken ct)
    {
        if (_session.IsSignedIn)
        {
            return;
        }

        var open = await _roster.GetOpenShiftAsync(_identity.TerminalId, ct).ConfigureAwait(false);

        if (open is null)
        {
            return;
        }

        var marker = await _signInMarker.GetAsync(ct).ConfigureAwait(false);

        if (!string.Equals(marker, open.EmployeeId, StringComparison.Ordinal))
        {
            TerminalLog.ShiftRestoreSkipped(_logger, open.Id, open.EmployeeName);
            return;
        }

        var employee = await _roster.GetEmployeeAsync(open.EmployeeId, ct).ConfigureAwait(false);

        if (employee is null)
        {
            // The shift names an operator this terminal no longer has. The drawer is still open,
            // so it is worth saying so rather than starting the day as if it were not.
            TerminalLog.ShiftOrphaned(_logger, open.Id, open.EmployeeName);
            return;
        }

        _session.Employee = employee.ToDomain();
        _session.Shift = open;
    }

    /// <summary>
    /// Builds the store record for this terminal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The id is the terminal's persisted store id, and once the till is enrolled it is the hub's
    /// store id. Both properties are load-bearing.
    /// </para>
    /// <para>
    /// <b>Persisted</b>, because it keys every local record: a fresh id per load would hide the
    /// day's sales from every report, empty the catalogue, orphan the open shift, and restart sale
    /// numbering at one.
    /// </para>
    /// <para>
    /// <b>The hub's</b>, because a stock transfer is addressed to a store id the hub issued. A
    /// relayed transfer arrives carrying that id, and the receiving store accepts it only if the
    /// operator's own store id matches — so a till keeping a private id of its own could never book
    /// in goods sent to it.
    /// </para>
    /// <para>
    /// The rest of the record is still locally configured. Pulling name, currency, tax and stations
    /// from the hub is a separate piece of work; the id is what had to be right first.
    /// </para>
    /// </remarks>
    private static Pos.Core.Domain.Store BuildStore(string? storeId) => new()
    {
        Id = Guid.TryParse(storeId, out var parsed)
            ? new Pos.Core.Domain.StoreId(parsed)

            // Unreachable in practice: the id is either minted here or issued by the hub, and both
            // are GUIDs. Falling back to a fresh id keeps the till selling if a stored value is
            // ever corrupted, which is better than refusing to open.
            : Pos.Core.Domain.StoreId.New(),
        Name = "CORNER STORE",
        Code = "CT01",
        Currency = "ZAR",
        TaxMode = Pos.Core.Domain.TaxMode.Inclusive,
        DefaultTaxRate = new Pos.Core.Domain.TaxRate("VAT", 0.15m),
        AddressLines = ["12 Main Road", "Cape Town"],
        TaxRegistrationNumber = "4123456789",
        Phone = "021 555 0100",
        ReceiptFooter = "Thank you for your business!",
        ReceiptColumns = 48,

        // The order tickets are produced in. A shop that prepares nothing leaves this empty and
        // prints no kitchen tickets at all.
        Stations =
        [
            new Pos.Core.Domain.PreparationStation("kitchen", "KITCHEN"),
            new Pos.Core.Domain.PreparationStation("bar", "BAR"),
        ],
    };

    /// <summary>
    /// Seeds a small catalogue so a fresh terminal can ring up a sale immediately.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this the till starts with an empty catalogue and every scan fails, which
    /// makes the system impossible to evaluate. Real products arrive by catalogue sync.
    /// </para>
    /// <para>
    /// The probe is scoped to this store. It used to look for the barcode anywhere in the local
    /// catalogue, which meant a terminal that had moved to another store found the previous store's
    /// copy, concluded there was nothing to do, and then showed an empty catalogue — because the
    /// listing is store-scoped even when the probe was not.
    /// </para>
    /// </remarks>
    private async Task SeedCatalogueIfEmptyAsync(Pos.Core.Domain.Store store, CancellationToken ct)
    {
        var probe = await _store
            .FindProductByBarcodeAsync(store.Id.ToString(), "6001000000017", ct)
            .ConfigureAwait(false);

        if (probe is not null)
        {
            return;
        }

        var products = new List<Pos.Infrastructure.Storage.StoredProduct>
        {
            NewProduct(store, "6001000000017", "Cola 500ml", 15.00m),
            NewProduct(store, "6001000000024", "Still Water 750ml", 12.50m),
            NewProduct(store, "6001000000031", "White Bread", 18.99m),
            NewProduct(store, "6001000000048", "Milk 2L", 32.99m),
            NewProduct(store, "6001000000055", "Bananas (per kg)", 22.99m, soldByWeight: true),
            NewProduct(store, "6001000000062", "Chocolate Bar", 9.99m),
            NewProduct(store, "6001000000079", "Coffee 250g", 89.99m),
            NewProduct(store, "6001000000086", "Eggs (dozen)", 44.99m),

            // Prepared items, so the station routing is reachable on a fresh terminal rather than
            // being a feature with nothing to demonstrate it. Shelf goods above deliberately have
            // no station: they are handed over at the till and produce no ticket.
            NewProduct(store, "6001000000093", "Chicken Burger", 79.99m, stationId: "kitchen"),
            NewProduct(store, "6001000000109", "Chips", 29.99m, stationId: "kitchen"),
            NewProduct(store, "6001000000116", "Flat White", 34.99m, stationId: "bar"),
            NewProduct(store, "6001000000123", "Craft Beer", 45.00m, stationId: "bar"),
        };

        await _store.UpsertProductsAsync(products, ct).ConfigureAwait(false);
        TerminalLog.CatalogueSeeded(_logger, products.Count);
    }

    private static Pos.Infrastructure.Storage.StoredProduct NewProduct(
        Pos.Core.Domain.Store store,
        string barcode,
        string name,
        decimal price,
        bool soldByWeight = false,
        string? stationId = null) => new()
        {
            Id = Pos.Core.Domain.ProductId.New().ToString(),
            StoreId = store.Id.ToString(),
            Barcode = barcode,
            Name = name,
            UnitPrice = price,
            TaxName = store.DefaultTaxRate.Name,
            TaxRate = store.DefaultTaxRate.Rate,
            IsSoldByWeight = soldByWeight,
            StationId = stationId,
        };

    /// <summary>
    /// Seeds a small roster so a fresh terminal can sign an operator in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same reasoning as the catalogue seed: without it a new terminal has no employees, so
    /// nobody can sign in and every sale is unattributable. Real rosters arrive from head office.
    /// </para>
    /// <para>
    /// The PINs are printed on the sign-in screen, which would be indefensible in production and
    /// is deliberate here — a demo roster nobody can log into is not a demo. They exist only
    /// until the first roster sync replaces them.
    /// </para>
    /// </remarks>
    private async Task SeedRosterIfEmptyAsync(Pos.Core.Domain.Store store, CancellationToken ct)
    {
        var existing = await _roster.GetEmployeesAsync(store.Id.ToString(), ct).ConfigureAwait(false);

        if (existing.Count > 0)
        {
            return;
        }

        var employees = new List<Pos.Infrastructure.Storage.StoredEmployee>
        {
            NewEmployee(
                store,
                "Thandi Mokoena",
                "TM",
                "4821",
                Pos.Core.Domain.EmployeePermissions.Sell | Pos.Core.Domain.EmployeePermissions.OpenDrawer),

            // Supervisors can refund, discount, and void, and can close someone else's drawer.
            NewEmployee(
                store,
                "Sipho Ndlovu",
                "SN",
                "7395",
                Pos.Core.Domain.EmployeePermissions.Sell |
                Pos.Core.Domain.EmployeePermissions.Refund |
                Pos.Core.Domain.EmployeePermissions.OpenDrawer |
                Pos.Core.Domain.EmployeePermissions.Discount |
                Pos.Core.Domain.EmployeePermissions.Void |
                Pos.Core.Domain.EmployeePermissions.CloseShift |
                Pos.Core.Domain.EmployeePermissions.Supervisor),

            NewEmployee(
                store,
                "Anele Botha",
                "AB",
                "9152",
                Pos.Core.Domain.EmployeePermissions.Manager |
                Pos.Core.Domain.EmployeePermissions.ManageCatalog),
        };

        await _roster.UpsertEmployeesAsync(employees, ct).ConfigureAwait(false);
        TerminalLog.RosterSeeded(_logger, employees.Count);
    }

    private static Pos.Infrastructure.Storage.StoredEmployee NewEmployee(
        Pos.Core.Domain.Store store,
        string name,
        string initials,
        string pin,
        Pos.Core.Domain.EmployeePermissions permissions)
    {
        var id = Pos.Core.Domain.EmployeeId.New();

        return new Pos.Infrastructure.Storage.StoredEmployee
        {
            Id = id.ToString(),
            StoreId = store.Id.ToString(),
            Name = name,
            Initials = initials,

            // Hashed with the employee id as the salt, exactly as a rostered employee would be.
            // The seed must not be a second, weaker path into the same table.
            PinHash = Pos.Infrastructure.Security.PinHasher.Hash(id, pin),
            Permissions = permissions.ToString(),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}
