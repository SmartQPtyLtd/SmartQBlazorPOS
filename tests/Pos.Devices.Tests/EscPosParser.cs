// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Text;

namespace Pos.Devices.Tests;

/// <summary>
/// Parses an ESC/POS byte stream back into its text lines, for assertions about what
/// a receipt actually prints.
/// </summary>
/// <remarks>
/// <para>
/// Asserting on raw byte arrays is unreadable and gives useless diffs. Asserting on the
/// decoded text instead tests what the customer sees.
/// </para>
/// <para>
/// The parser also <b>throws on any unrecognised or truncated command</b>. That is the
/// valuable part: a mis-emitted or malformed command fails loudly here rather than
/// silently corrupting a real receipt, which is otherwise impossible to detect without
/// a printer attached.
/// </para>
/// </remarks>
internal sealed class EscPosParser
{
    private readonly List<string> _lines = [];
    private readonly List<string> _commands = [];
    private readonly StringBuilder _current = new();

    /// <summary>Printable text lines in the order they were emitted.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Names of the commands encountered, for structural assertions.</summary>
    public IReadOnlyList<string> Commands => _commands;

    /// <summary>True when a cash-drawer kick was emitted.</summary>
    public bool OpenedDrawer => _commands.Contains("OpenDrawer");

    /// <summary>True when the paper was cut.</summary>
    public bool Cut => _commands.Contains("Cut");

    /// <summary>Payloads of every QR code emitted.</summary>
    public List<string> QrPayloads { get; } = [];

    public static EscPosParser Parse(byte[] bytes) => new(bytes);

    public EscPosParser(byte[] bytes)
    {
        var i = 0;

        while (i < bytes.Length)
        {
            var b = bytes[i];

            // ESC — two- or three-byte commands.
            if (b == 0x1B)
            {
                i = ParseEsc(bytes, i);
                continue;
            }

            // GS — mostly two- or three-byte, plus the GS ( k family.
            if (b == 0x1D)
            {
                i = ParseGs(bytes, i);
                continue;
            }

            if (b == 0x0A)
            {
                FlushLine();
                i++;
                continue;
            }

            // Printable ASCII and CP437 upper half both become text. Upper-half bytes
            // are decoded for display so assertions can use real characters.
            _current.Append(DecodeByte(b));
            i++;
        }

        FlushLine();
    }

    private int ParseEsc(byte[] bytes, int i)
    {
        Require(bytes, i, 2);
        var command = bytes[i + 1];

        switch (command)
        {
            case 0x40: // ESC @ initialise
                _commands.Add("Initialise");
                return i + 2;

            case 0x61: // ESC a n align
                Require(bytes, i, 3);
                _commands.Add($"Align{bytes[i + 2]}");
                return i + 3;

            case 0x45: // ESC E n bold
                Require(bytes, i, 3);
                _commands.Add(bytes[i + 2] == 0 ? "BoldOff" : "BoldOn");
                return i + 3;

            case 0x2D: // ESC - n underline
                Require(bytes, i, 3);
                _commands.Add($"Underline{bytes[i + 2]}");
                return i + 3;

            case 0x74: // ESC t n code page
                Require(bytes, i, 3);
                _commands.Add($"CodePage{bytes[i + 2]}");
                return i + 3;

            case 0x64: // ESC d n feed
                Require(bytes, i, 3);
                _commands.Add($"Feed{bytes[i + 2]}");
                return i + 3;

            case 0x70: // ESC p m t1 t2 cash drawer
                Require(bytes, i, 5);
                _commands.Add("OpenDrawer");
                return i + 5;

            default:
                throw new InvalidOperationException(
                    $"Unrecognised ESC command 0x{command:X2} at offset {i}.");
        }
    }

    private int ParseGs(byte[] bytes, int i)
    {
        Require(bytes, i, 3);
        var command = bytes[i + 1];

        switch (command)
        {
            case 0x21: // GS ! n text size
                _commands.Add($"TextSize{bytes[i + 2]:X2}");
                return i + 3;

            case 0x68: // GS h n barcode height
                _commands.Add($"BarcodeHeight{bytes[i + 2]}");
                return i + 3;

            case 0x48: // GS H n barcode text position
                _commands.Add($"BarcodeText{bytes[i + 2]}");
                return i + 3;

            case 0x77: // GS w n barcode module width
                _commands.Add($"BarcodeWidth{bytes[i + 2]}");
                return i + 3;

            case 0x56: // GS V m cut
                _commands.Add("Cut");
                return i + 3;

            case 0x6B: // GS k m ... barcode
                return ParseBarcode(bytes, i);

            case 0x28: // GS ( k — the two-dimensional symbol family
                return ParseGsParenK(bytes, i);

            default:
                throw new InvalidOperationException(
                    $"Unrecognised GS command 0x{command:X2} at offset {i}.");
        }
    }

    private int ParseBarcode(byte[] bytes, int i)
    {
        Require(bytes, i, 3);
        var symbology = bytes[i + 2];

        switch (symbology)
        {
            case 73: // Code128 — explicit length follows
            case 70: // ITF — explicit length follows
                Require(bytes, i, 4);
                var length = bytes[i + 3];
                Require(bytes, i, 4 + length);
                _commands.Add($"Barcode{symbology}");
                return i + 4 + length;

            case 69: // Code39 — NUL terminated
            {
                var end = Array.IndexOf(bytes, (byte)0x00, i + 3);
                if (end < 0)
                {
                    throw new InvalidOperationException($"Unterminated Code39 barcode at offset {i}.");
                }

                _commands.Add("Barcode69");
                return end + 1;
            }

            case 67: // EAN-13 — fixed length
                Require(bytes, i, 5 + 13);
                _commands.Add("Barcode67");
                return i + 5 + 13;

            case 68: // EAN-8 — fixed length
                Require(bytes, i, 5 + 8);
                _commands.Add("Barcode68");
                return i + 5 + 8;

            case 65: // UPC-A — fixed length
                Require(bytes, i, 5 + 12);
                _commands.Add("Barcode65");
                return i + 5 + 12;

            default:
                throw new InvalidOperationException(
                    $"Unrecognised barcode symbology 0x{symbology:X2} at offset {i}.");
        }
    }

    private int ParseGsParenK(byte[] bytes, int i)
    {
        Require(bytes, i, 8);

        // Layout is: GS ( k | cn | fn | pL | pH | operands.
        var cn = bytes[i + 3];
        var fn = bytes[i + 4];
        var length = bytes[i + 5] | (bytes[i + 6] << 8);

        // Hard bounds guard: a GS ( k byte sequence can also occur inside QR payload
        // data, so a malformed match must be rejected rather than followed off the end.
        // The combined length covers cn, fn and every operand byte, so it is never
        // smaller than 3 (cn + fn + one operand).
        if (cn != 0x31 || length < 3)
        {
            throw new InvalidOperationException(
                $"Malformed GS ( k at offset {i}: cn=0x{cn:X2} fn=0x{fn:X2} length={length}.");
        }

        var operandStart = i + 7;
        var operandLength = length - 2; // exclude cn and fn

        if (operandStart + operandLength > bytes.Length)
        {
            throw new InvalidOperationException(
                $"GS ( k at offset {i} declares {length} bytes but the stream ends early.");
        }

        _commands.Add($"GsParenK_49_{fn}");

        // fn = 80 (0x50) stores symbol data as { m, data... }; fn = 81 (0x51) prints it.
        if (fn == 80 && operandLength >= 1)
        {
            QrPayloads.Add(Decode(bytes.AsSpan(operandStart + 1, operandLength - 1)));
        }

        return operandStart + operandLength;
    }

    private void FlushLine()
    {
        _lines.Add(_current.ToString());
        _current.Clear();
    }

    private static void Require(byte[] bytes, int offset, int needed)
    {
        if (offset + needed > bytes.Length)
        {
            throw new InvalidOperationException(
                $"Truncated command at offset {offset}: needed {needed} bytes, stream ends at {bytes.Length}.");
        }
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            builder.Append(DecodeByte(b));
        }

        return builder.ToString();
    }

    /// <summary>Decodes a CP437 byte to its character, using ASCII for the lower half.</summary>
    private static char DecodeByte(byte b) => b < 0x80 ? (char)b : '?';
}
