// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

namespace Pos.Devices.Transport;

/// <summary>
/// An in-memory transport that records everything written to it.
/// </summary>
/// <remarks>
/// <para>
/// This is not a stub for tests alone — it is the primary development and CI transport.
/// Real thermal hardware cannot be attached to a build agent, and a wrong ESC/POS byte
/// is invisible without one. Every job sent here is retained as bytes plus a decoded
/// view, so a receipt can be inspected and asserted long after it was "printed".
/// </para>
/// <para>
/// It also lets the whole checkout flow be exercised end to end on a machine with no
/// peripherals at all, which is what makes automated verification of the till possible.
/// </para>
/// </remarks>
public sealed class SimulatedTransport(int columns = 48) : IDeviceTransport
{
    private readonly List<PrintedJob> _jobs = [];

    public string TransportId => "simulated";

    public string DisplayName => "Simulated printer";

    public DeviceState State { get; private set; } =
        new(DeviceStatus.Ready, "Simulated", "Virtual ESC/POS printer");

    public PrinterCapabilities Capabilities { get; } =
        new(CanCut: true, CanOpenDrawer: true, CanPrintImages: true, CanPrintQrCode: true, Columns: columns);

    public event Action<DeviceState>? StateChanged;

    /// <summary>Every job written, oldest first.</summary>
    public IReadOnlyList<PrintedJob> Jobs => _jobs;

    /// <summary>Total bytes written across all jobs.</summary>
    public int TotalBytes => _jobs.Sum(j => j.Bytes.Length);

    /// <summary>Set to simulate a printer error on the next write.</summary>
    public Exception? FailNextWrite { get; set; }

    /// <summary>When true, writes are rejected as though the printer were offline.</summary>
    public bool SimulateOffline { get; set; }

    public ValueTask<bool> IsAvailableAsync(CancellationToken ct = default) => ValueTask.FromResult(true);

    public ValueTask<bool> TryReconnectAsync(CancellationToken ct = default)
    {
        SetState(State with { Status = DeviceStatus.Ready });
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> RequestDeviceAsync(CancellationToken ct = default)
    {
        SetState(new DeviceState(DeviceStatus.Ready, "Simulated", "Virtual ESC/POS printer"));
        return ValueTask.FromResult(true);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (FailNextWrite is { } failure)
        {
            FailNextWrite = null;
            SetState(State with { Status = DeviceStatus.Faulted, Detail = failure.Message });
            throw failure;
        }

        if (SimulateOffline)
        {
            SetState(State with { Status = DeviceStatus.Disconnected, Detail = "Simulated offline" });
            throw new IOException("The simulated printer is offline.");
        }

        _jobs.Add(new PrintedJob(DateTimeOffset.UtcNow, payload.ToArray()));
        SetState(State with { Status = DeviceStatus.Ready, Detail = null });

        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        SetState(State with { Status = DeviceStatus.NotConfigured });
        return ValueTask.CompletedTask;
    }

    /// <summary>Discards all recorded jobs.</summary>
    public void Clear() => _jobs.Clear();

    /// <summary>Returns the most recent job, or null when nothing has been printed.</summary>
    public PrintedJob? LastJob => _jobs.Count == 0 ? null : _jobs[^1];

    private void SetState(DeviceState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>One recorded print job.</summary>
/// <param name="WrittenAt">When the job was submitted.</param>
/// <param name="Bytes">The exact bytes that would have reached the printer.</param>
public sealed record PrintedJob(DateTimeOffset WrittenAt, byte[] Bytes)
{
    /// <summary>
    /// The printable text of the job, with command bytes omitted.
    /// </summary>
    /// <remarks>
    /// Decodes CP437 bytes to the characters a printer would render, so a test or an
    /// operator log can read the receipt rather than a wall of hex.
    /// </remarks>
    public string Text
    {
        get
        {
            var builder = new System.Text.StringBuilder(Bytes.Length);
            var i = 0;

            while (i < Bytes.Length)
            {
                var b = Bytes[i];

                // Skip known command sequences so only printable content remains.
                if (b == 0x1B)
                {
                    i += SkipEsc(Bytes, i);
                    continue;
                }

                if (b == 0x1D)
                {
                    i += SkipGs(Bytes, i);
                    continue;
                }

                if (b == 0x0A)
                {
                    builder.Append('\n');
                    i++;
                    continue;
                }

                builder.Append(b < 0x80 ? (char)b : '?');
                i++;
            }

            return builder.ToString();
        }
    }

    private static int SkipEsc(byte[] bytes, int i)
    {
        if (i + 1 >= bytes.Length)
        {
            return 1;
        }

        return bytes[i + 1] switch
        {
            0x70 => 5, // ESC p — cash drawer
            0x40 => 2, // ESC @ — initialise
            _ => 3,    // two-argument commands (align, bold, feed, code page)
        };
    }

    private static int SkipGs(byte[] bytes, int i)
    {
        if (i + 1 >= bytes.Length)
        {
            return 1;
        }

        if (bytes[i + 1] == 0x28 && i + 6 < bytes.Length)
        {
            var length = bytes[i + 5] | (bytes[i + 6] << 8);
            return 4 + length;
        }

        return bytes[i + 1] switch
        {
            0x6B => 4, // GS k — basic barcode form
            0x56 => 3, // GS V — cut
            _ => 3,    // GS ! / GS h / GS H / GS w
        };
    }
}
