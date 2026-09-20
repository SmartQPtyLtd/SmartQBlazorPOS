// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

// verify-customer-display-e2e.cs — proves the customer display end to end in a real browser.
//
// Run:  dotnet run tools/verify-customer-display-e2e.cs
//
// The repo's other browser checks are Node scripts, but this machine has no Node — and a POS
// that must build in a locked-down environment cannot gain a runtime dependency for a check.
// .NET 10 runs a single C# file directly, and ClientWebSocket is all the DevTools protocol
// needs, so the check stays zero-dependency either way.
//
// What it does:
//   1. Launches headless Edge with remote debugging, in a throwaway profile.
//   2. Opens the till (/) in one target and the customer display (/display) in a second.
//      Two targets in one browser profile are exactly what window.open produces, and
//      BroadcastChannel works between them the same way.
//   3. Waits for both Blazor apps to boot.
//   4. Diagnoses the channel in both directions: a raw BroadcastChannel post from the till
//      target (proves the display's receive path), and a direct module publish from the till
//      (proves the JS module loads in the till page).
//   5. Scans a barcode into the till the way a keyboard-wedge scanner types it, then reads
//      the display target's DOM for the basket line.
//   6. Prints every console message and uncaught exception from BOTH targets — a swallowed
//      JSException on the publish path only shows here.
//
// Exit code 0 when the scanned line reaches the display, 1 otherwise.

using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var tillUrl = args.Length > 0 ? args[0] : "http://localhost:5043/";
var displayUrl = args.Length > 1 ? args[1] : "http://localhost:5043/display";
const int DebugPort = 9223;

var edgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
if (!File.Exists(edgePath))
{
    edgePath = @"C:\Program Files\Microsoft\Edge\Application\msedge.exe";
}

var profile = Path.Combine(Path.GetTempPath(), "pos-cdp-profile-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(profile);

var edge = Process.Start(new ProcessStartInfo
{
    FileName = edgePath,
    Arguments = $"--headless=new --disable-gpu --no-first-run --remote-debugging-port={DebugPort} " +
                $"--user-data-dir=\"{profile}\" about:blank",
    UseShellExecute = false,
}) ?? throw new InvalidOperationException("Edge did not start.");

var exitCode = 1;

try
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

    // Wait for the DevTools endpoint.
    var up = false;
    for (var i = 0; i < 100 && !up; i++)
    {
        try
        {
            await http.GetStringAsync($"http://127.0.0.1:{DebugPort}/json/version");
            up = true;
        }
        catch
        {
            await Task.Delay(150);
        }
    }

    if (!up)
    {
        throw new InvalidOperationException("Edge's DevTools endpoint never came up.");
    }

    var till = await NewTargetAsync(http, tillUrl);
    var display = await NewTargetAsync(http, displayUrl);

    await using var tillCdp = await CdpClient.ConnectAsync(till);
    await using var displayCdp = await CdpClient.ConnectAsync(display);

    // Boot both apps. Debug WASM starts slowly; give it a minute.
    try
    {
        await WaitForAsync(tillCdp,
            "(() => { const i = document.querySelector('.pos-scan__input'); return !!i && !i.disabled; })()",
            TimeSpan.FromSeconds(90), "till to boot");

        await WaitForAsync(displayCdp,
            "!!document.querySelector('.cd-shell')",
            TimeSpan.FromSeconds(90), "display to boot");
    }
    catch (TimeoutException ex)
    {
        Console.WriteLine($"BOOT FAILURE: {ex.Message}");
        await DumpAsync(tillCdp, "till");
        await DumpAsync(displayCdp, "display");
        return 1;
    }

    Console.WriteLine("Both apps booted.");

    // --- Diagnosis 1: does the display receive a raw BroadcastChannel message? -------------
    await tillCdp.EvaluateAsync(
        "new BroadcastChannel('pos-customer-display')" +
        ".postMessage(JSON.stringify({lines:[{name:'RAW-CHANNEL-PROBE',quantity:'1',lineTotal:0.01}]," +
        "total:0.01,itemCount:1,message:'probe'}))");

    await Task.Delay(1500);

    var afterRaw = await displayCdp.EvaluateAsync("document.querySelector('.cd-shell').innerText");
    var rawReceived = afterRaw.Contains("RAW-CHANNEL-PROBE", StringComparison.Ordinal);
    Console.WriteLine($"[diag] raw BroadcastChannel post reached the display DOM: {rawReceived}");

    // --- Diagnosis 2: does the display module import and publish from the till page? -------
    var moduleProbe = await tillCdp.EvaluateAsync(
        """(async () => { try { const m = await import('./js/customer-display.js'); return 'import-ok:' + m.publish(JSON.stringify({lines:[],total:0,itemCount:0,message:'module-probe'})); } catch (e) { return 'import-failed: ' + e; } })()""",
        awaitPromise: true);
    Console.WriteLine($"[diag] till-side module import/publish: {moduleProbe}");

    // --- The actual test: scan a barcode at the till ---------------------------------------
    await tillCdp.EvaluateAsync(
        """(() => { const input = document.querySelector('.pos-scan__input'); input.value = '6001000000017'; input.dispatchEvent(new Event('input', { bubbles: true })); input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); return true; })()""");

    await Task.Delay(3000);

    var tillText = await tillCdp.EvaluateAsync("document.querySelector('.pos-main').innerText");
    var displayText = await displayCdp.EvaluateAsync("document.querySelector('.cd-shell').innerText");

    var tillHasLine = tillText.Contains("Cola 500ml", StringComparison.Ordinal);
    var displayHasLine = displayText.Contains("Cola 500ml", StringComparison.Ordinal);

    Console.WriteLine($"[result] till shows the scanned line:     {tillHasLine}");
    Console.WriteLine($"[result] DISPLAY shows the scanned line:  {displayHasLine}");

    Console.WriteLine("\n=== till console ===");
    Console.WriteLine(tillCdp.Messages.Count > 0 ? string.Join('\n', tillCdp.Messages) : "(none)");
    Console.WriteLine("\n=== display console ===");
    Console.WriteLine(displayCdp.Messages.Count > 0 ? string.Join('\n', displayCdp.Messages) : "(none)");

    exitCode = displayHasLine ? 0 : 1;
    Console.WriteLine($"\n{(displayHasLine ? "PASS" : "FAIL")}: customer display end to end.");
}
finally
{
    try { edge.Kill(entireProcessTree: true); } catch { /* already gone */ }
    try { Directory.Delete(profile, recursive: true); } catch { /* best effort */ }
}

return exitCode;

// Creates a page target and returns its debugger URL.
static async Task<string> NewTargetAsync(HttpClient http, string url)
{
    var response = await http.PutAsync(
        $"http://127.0.0.1:9223/json/new?{Uri.EscapeDataString(url)}", content: null);

    var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    return json.RootElement.GetProperty("webSocketDebuggerUrl").GetString()
        ?? throw new InvalidOperationException("Target had no debugger URL.");
}

static async Task WaitForAsync(CdpClient cdp, string predicate, TimeSpan timeout, string what)
{
    var deadline = DateTime.UtcNow + timeout;
    var last = string.Empty;

    while (DateTime.UtcNow < deadline)
    {
        last = await cdp.EvaluateAsync(predicate);

        if (string.Equals(last, "True", StringComparison.Ordinal))
        {
            return;
        }

        await Task.Delay(500);
    }

    throw new TimeoutException($"Timed out waiting for {what}. Last probe: {last}");
}

/// <summary>Prints everything a target said and what its page currently shows.</summary>
static async Task DumpAsync(CdpClient cdp, string name)
{
    Console.WriteLine($"\n=== {name} console ===");
    Console.WriteLine(cdp.Messages.Count > 0 ? string.Join('\n', cdp.Messages) : "(none)");

    var probe = await cdp.EvaluateAsync(
        "JSON.stringify({ title: document.title, blazor: typeof window.Blazor, body: document.body ? document.body.innerText.slice(0, 300) : null })");

    Console.WriteLine($"=== {name} DOM probe ===");
    Console.WriteLine(probe);
}

/// <summary>A minimal DevTools-protocol client over a page target's WebSocket.</summary>
sealed class CdpClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly List<string> _messages = [];
    private Task _reader = null!;
    private int _nextId;

    private CdpClient() { }

    public IReadOnlyList<string> Messages
    {
        get { lock (_messages) { return [.. _messages]; } }
    }

    public static async Task<CdpClient> ConnectAsync(string webSocketUrl)
    {
        var client = new CdpClient();
        await client._socket.ConnectAsync(new Uri(webSocketUrl), CancellationToken.None);
        client._reader = Task.Run(client.ReadLoopAsync);

        await client.SendAsync("Runtime.enable");
        await client.SendAsync("Log.enable");
        await client.SendAsync("Page.enable");

        return client;
    }

    /// <summary>Evaluates an expression and returns its value as a string.</summary>
    public async Task<string> EvaluateAsync(string expression, bool awaitPromise = false)
    {
        var result = await SendAsync("Runtime.evaluate", new Dictionary<string, object?>
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
            ["awaitPromise"] = awaitPromise,
        });

        if (result.TryGetProperty("exceptionDetails", out var failure))
        {
            return "JS-ERROR: " + failure.ToString();
        }

        return result.GetProperty("result").TryGetProperty("value", out var value)
            ? value.ToString()
            : string.Empty;
    }

    private Task<JsonElement> SendAsync(string method, Dictionary<string, object?>? parameters = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pending)
        {
            _pending[id] = completion;
        }

        // Built as nodes rather than serialised: file-based apps run with reflection-based
        // JSON disabled, and everything sent here is strings, booleans, and ints anyway.
        var frame = new JsonObject
        {
            ["id"] = id,
            ["method"] = method,
        };

        var paramsNode = new JsonObject();

        foreach (var (key, value) in parameters ?? [])
        {
            paramsNode[key] = value switch
            {
                null => null,
                string text => text,
                bool flag => flag,
                int number => number,
                _ => value.ToString(),
            };
        }

        frame["params"] = paramsNode;

        _ = _socket.SendAsync(
            Encoding.UTF8.GetBytes(frame.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

        return completion.Task;
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[256 * 1024];

        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult part;

                do
                {
                    part = await _socket.ReceiveAsync(buffer, _stopping.Token);
                    stream.Write(buffer, 0, part.Count);
                }
                while (!part.EndOfMessage);

                if (part.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                Dispatch(JsonDocument.Parse(stream.ToArray()).RootElement.Clone());
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (WebSocketException)
        {
            // The target went away.
        }
    }

    private void Dispatch(JsonElement frame)
    {
        if (frame.TryGetProperty("id", out var id))
        {
            TaskCompletionSource<JsonElement>? completion;

            lock (_pending)
            {
                _pending.Remove(id.GetInt32(), out completion);
            }

            completion?.TrySetResult(frame.TryGetProperty("result", out var result) ? result : frame);
            return;
        }

        var method = frame.GetProperty("method").GetString();

        if (method is "Runtime.consoleAPICalled")
        {
            var args = frame.GetProperty("params").GetProperty("args");
            var text = string.Join(' ', args.EnumerateArray().Select(a =>
                a.TryGetProperty("value", out var v) ? v.ToString()
                : a.TryGetProperty("description", out var d) ? d.GetString() : a.GetProperty("type").GetString()));

            lock (_messages)
            {
                _messages.Add($"[{frame.GetProperty("params").GetProperty("type").GetString()}] {text}");
            }
        }
        else if (method is "Runtime.exceptionThrown")
        {
            var details = frame.GetProperty("params").GetProperty("exceptionDetails");
            var text = details.TryGetProperty("exception", out var ex) && ex.TryGetProperty("description", out var d)
                ? d.GetString() : details.GetProperty("text").GetString();

            lock (_messages)
            {
                _messages.Add($"[EXCEPTION] {text}");
            }
        }
        else if (method is "Log.entryAdded")
        {
            var entry = frame.GetProperty("params").GetProperty("entry");
            var level = entry.GetProperty("level").GetString();

            if (level is "error" or "warning")
            {
                lock (_messages)
                {
                    _messages.Add($"[log:{level}] {entry.GetProperty("text").GetString()}");
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        try
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        catch
        {
            // Already gone.
        }

        _socket.Dispose();
    }
}
