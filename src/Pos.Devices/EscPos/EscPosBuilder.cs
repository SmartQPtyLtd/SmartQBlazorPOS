// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Text;

namespace Pos.Devices.EscPos;

/// <summary>
/// Builds ESC/POS command streams for thermal receipt printers.
/// </summary>
/// <remarks>
/// <para>
/// ESC/POS output is produced here in <b>pure C#</b> and only the finished byte
/// array is handed to the transport. That split is deliberate: command generation
/// carries all the receiving-printer-specific risk, so keeping it browser-free means
/// every byte can be asserted against a golden vector in a unit test. The transport
/// then has nothing left to get wrong except moving bytes.
/// </para>
/// <para>
/// All multi-byte integers follow the ESC/POS little-endian convention
/// (<c>nL + nH * 256</c>).
/// </para>
/// </remarks>
public sealed class EscPosBuilder
{
    private readonly List<byte> _buffer = [];

    /// <param name="columns">Paper width in characters: 32 for 58mm, 48 for 80mm.</param>
    /// <param name="codePage">
    /// Single-byte code page the printer is configured for. Most ESC/POS printers
    /// default to CP437; a printer switched to another page will mojibake unless
    /// this matches.
    /// </param>
    public EscPosBuilder(int columns = 48, int codePage = 437)
    {
        if (columns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), "Column count must be positive.");
        }

        if (codePage != 437)
        {
            throw new NotSupportedException(
                $"Code page {codePage} is not supported yet. Only CP437 is implemented; " +
                "add a translation table before enabling others.");
        }

        Columns = columns;
    }

    /// <summary>Paper width in characters.</summary>
    public int Columns { get; }

    /// <summary>The accumulated command stream.</summary>
    public IReadOnlyList<byte> Buffer => _buffer;

    /// <summary>Number of bytes written so far. Useful for asserting layout in tests.</summary>
    public int Length => _buffer.Count;

    /// <summary>Returns the built stream as a byte array.</summary>
    public byte[] ToArray() => [.. _buffer];

    // ---------------------------------------------------------------- Initialisation

    /// <summary>ESC @ — resets the printer to its power-on defaults.</summary>
    /// <remarks>
    /// Always sent at the start of a document. Without it, a previous job that left
    /// the printer in double-height or emphasised mode corrupts the next receipt.
    /// </remarks>
    public EscPosBuilder Initialise()
    {
        _buffer.Add(0x1B);
        _buffer.Add(0x40);
        return this;
    }

    /// <summary>ESC a n — sets justification.</summary>
    public EscPosBuilder Align(TextAlignment alignment)
    {
        _buffer.Add(0x1B);
        _buffer.Add(0x61);
        _buffer.Add((byte)alignment);
        return this;
    }

    /// <summary>ESC E n — turns emphasised (bold) mode on or off.</summary>
    public EscPosBuilder Bold(bool enabled = true)
    {
        _buffer.Add(0x1B);
        _buffer.Add(0x45);
        _buffer.Add((byte)(enabled ? 1 : 0));
        return this;
    }

    /// <summary>ESC - n — turns underline on or off.</summary>
    public EscPosBuilder Underline(bool enabled = true)
    {
        _buffer.Add(0x1B);
        _buffer.Add(0x2D);
        _buffer.Add((byte)(enabled ? 1 : 0));
        return this;
    }

    /// <summary>GS ! n — sets character size by combining width and height multipliers.</summary>
    /// <param name="width">Horizontal multiplier, 1 to 8.</param>
    /// <param name="height">Vertical multiplier, 1 to 8.</param>
    public EscPosBuilder TextSize(int width = 1, int height = 1)
    {
        if (width is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width multiplier must be 1-8.");
        }

        if (height is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height multiplier must be 1-8.");
        }

        // High nibble is width-1, low nibble is height-1.
        var n = (byte)(((width - 1) << 4) | (height - 1));
        _buffer.Add(0x1D);
        _buffer.Add(0x21);
        _buffer.Add(n);
        return this;
    }

    /// <summary>ESC t n — selects the character code table.</summary>
    public EscPosBuilder CodePage(int page = 0)
    {
        _buffer.Add(0x1B);
        _buffer.Add(0x74);
        _buffer.Add((byte)page);
        return this;
    }

    // ------------------------------------------------------------------------ Text

    /// <summary>Writes text followed by a line feed.</summary>
    public EscPosBuilder Line(string text = "")
    {
        Text(text);
        return NewLine();
    }

    /// <summary>Writes text with no trailing line feed.</summary>
    public EscPosBuilder Text(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return this;
        }

        // CP437 cannot represent every Unicode code point. Unmappable characters
        // become '?' rather than throwing, because a receipt with a stray '?' is
        // recoverable and a printer that stops mid-job is not.
        _buffer.AddRange(Cp437.Encode(text));
        return this;
    }

    /// <summary>LF — advances one line.</summary>
    public EscPosBuilder NewLine()
    {
        _buffer.Add(0x0A);
        return this;
    }

    /// <summary>Feeds <paramref name="lines"/> blank lines.</summary>
    public EscPosBuilder Feed(int lines = 1)
    {
        if (lines <= 0)
        {
            return this;
        }

        _buffer.Add(0x1B);
        _buffer.Add(0x64);
        _buffer.Add((byte)Math.Min(lines, 255));
        return this;
    }

    // ---------------------------------------------------------------------- Layout

    /// <summary>
    /// Writes a line with a left-aligned label and a right-aligned value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The workhorse for item lines and totals. If the two sides cannot fit, the
    /// value is preserved and the label is truncated — the amount is the part that
    /// must never be lost or ambiguous on a receipt.
    /// </para>
    /// <para>
    /// Padding is computed from the <b>CP437-encoded</b> byte count, not the string
    /// length, because that is what the printer actually advances. A label of
    /// <c>日本語</c> is three bytes wide on paper (printed as <c>???</c>) even though
    /// it is six cells wide in Unicode. Measuring the Unicode width here would
    /// misalign every amount on the receipt.
    /// </para>
    /// </remarks>
    public EscPosBuilder TwoColumns(string left, string right, char fill = ' ')
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var rightWidth = Cp437.Encode(right).Length;

        if (rightWidth >= Columns)
        {
            // Value alone fills the line; drop the label rather than split the amount.
            return Line(right);
        }

        var available = Columns - rightWidth;
        var padded = PadToWidth(left, available, fill);

        return Line(padded + right);
    }

    /// <summary>
    /// Truncates <paramref name="text"/> to at most <paramref name="width"/> printed
    /// cells, then right-pads it with <paramref name="fill"/> to exactly that width.
    /// </summary>
    private static string PadToWidth(string text, int width, char fill)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        var encoded = Cp437.Encode(text);

        if (encoded.Length <= width)
        {
            // Everything mapped one-to-one, which is the normal case for product
            // names and barcodes. Padding here keeps the amount hard right.
            return text + new string(fill, width - encoded.Length);
        }

        // The label is too long. Accumulate runes until one more would overflow the
        // available cells, re-encoding as we go so that multi-byte characters are
        // never split across the boundary.
        var builder = new StringBuilder(width);
        var used = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            var runeWidth = Cp437.Encode(rune.ToString()).Length;
            if (used + runeWidth > width)
            {
                break;
            }

            builder.Append(rune);
            used += runeWidth;
        }

        if (used < width)
        {
            builder.Append(fill, width - used);
        }

        return builder.ToString();
    }

    /// <summary>Writes a horizontal rule across the full paper width.</summary>
    public EscPosBuilder Rule(char character = '-') => Line(new string(character, Columns));

    /// <summary>Writes a bold, centred heading.</summary>
    public EscPosBuilder Heading(string text, int size = 2)
    {
        Align(TextAlignment.Centre);
        Bold();
        TextSize(size, size);
        Line(text);
        TextSize();
        Bold(false);
        return Align(TextAlignment.Left);
    }

    // ------------------------------------------------------------------- Graphics

    /// <summary>
    /// GS ( k — prints a QR code.
    /// </summary>
    /// <param name="data">Content to encode.</param>
    /// <param name="moduleSize">Module size in dots, 1 to 16.</param>
    /// <param name="correction">Error correction level.</param>
    /// <remarks>
    /// Uses the newer two-dimensional symbol command set, which virtually all
    /// printers sold this decade support.
    /// </remarks>
    public EscPosBuilder QrCode(string data, int moduleSize = 6, QrErrorCorrection correction = QrErrorCorrection.Medium)
    {
        ArgumentException.ThrowIfNullOrEmpty(data);

        if (moduleSize is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(moduleSize), "Module size must be 1-16.");
        }

        var payload = Cp437.Encode(data);

        // Model 2 is the standard QR model; model 1 is legacy and rarely wanted.
        WriteQrCommand(65, [(byte)50]);
        WriteQrCommand(67, [(byte)moduleSize]);
        WriteQrCommand(69, [(byte)correction]);

        // Store the symbol data. The operand is the m byte followed by the data, and
        // the declared length covers cn, fn, m and the data together.
        var storeLength = 3 + payload.Length;
        WriteQrCommand(
            80,
            [(byte)48, .. payload],
            explicitLength: storeLength);

        // Print the stored symbol.
        WriteQrCommand(81, [(byte)48]);

        return this;
    }

    /// <summary>
    /// GS k — prints a 1D barcode.
    /// </summary>
    /// <remarks>
    /// Uses the explicit-length form of the command for symbologies whose payload
    /// length varies, and the fixed-length form for EAN/UPC, because not all
    /// printers accept the general form for the fixed-length symbologies.
    /// </remarks>
    public EscPosBuilder Barcode(string data, BarcodeSymbology symbology, int height = 80, BarcodeTextPosition textPosition = BarcodeTextPosition.Below)
    {
        ArgumentException.ThrowIfNullOrEmpty(data);

        if (height is < 1 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Barcode height must be 1-255 dots.");
        }

        // GS h n — barcode height in dots.
        WriteCommand(new byte[] { 0x1D, 0x68, (byte)height });

        // GS H n — whether the human-readable text prints above, below, or not at all.
        WriteCommand(new byte[] { 0x1D, 0x48, (byte)textPosition });

        // GS w n — module width, 2 to 6 dots.
        WriteCommand(new byte[] { 0x1D, 0x77, 0x02 });

        var payload = Cp437.Encode(data);

        switch (symbology)
        {
            case BarcodeSymbology.Code128:
                // CODE B is the safe general-purpose set for alphanumerics.
                // Prefix {B selects it, and a NUL terminates the data.
                var codeB = new List<byte> { 0x7B, 0x42 };
                codeB.AddRange(payload);
                codeB.Add(0x00);
                WriteVariableLengthBarcode(73, [.. codeB]);

                break;

            case BarcodeSymbology.Code39:
                // Asterisks are the Code39 start/stop delimiters; add them if absent.
                var code39 = data.StartsWith('*') && data.EndsWith('*') ? data : $"*{data}*";
                var code39Command = new byte[] { 0x1D, 0x6B, 0x45 }; // GS k 69
                WriteCommand(code39Command, Cp437.Encode(code39), [(byte)0x00]);

                break;

            case BarcodeSymbology.Ean13:
                RequireExactDigits(data, 13, "EAN-13");
                WriteCommand(new byte[] { 0x1D, 0x6B, 0x43, 13 }, payload);

                break;

            case BarcodeSymbology.Ean8:
                RequireExactDigits(data, 8, "EAN-8");
                WriteCommand(new byte[] { 0x1D, 0x6B, 0x44, 8 }, payload);

                break;

            case BarcodeSymbology.UpcA:
                RequireExactDigits(data, 12, "UPC-A");
                WriteCommand(new byte[] { 0x1D, 0x6B, 0x41, 12 }, payload);

                break;

            case BarcodeSymbology.Itf:
                WriteVariableLengthBarcode(70, payload);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(symbology), symbology, "Unsupported symbology.");
        }

        return this;
    }

    // -------------------------------------------------------------- Cash drawer

    /// <summary>
    /// ESC p m t1 t2 — fires the cash drawer kick-out solenoid.
    /// </summary>
    /// <remarks>
    /// The drawer is wired to the printer, not to the host, so this pulse is the
    /// only way to open it. <paramref name="onTime"/> and <paramref name="offTime"/>
    /// are in 2ms units; the defaults are the near-universal values. Values that are
    /// too low fail to fire the solenoid and values that are too high can burn it out,
    /// so these are not arbitrary knobs.
    /// </remarks>
    /// <param name="pin">Which drawer connector to pulse. Pin 2 is the common default.</param>
    public EscPosBuilder OpenDrawer(DrawerPin pin = DrawerPin.Pin2, byte onTime = 25, byte offTime = 250)
    {
        if (onTime == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(onTime), "On-time must be non-zero or the drawer will not fire.");
        }

        _buffer.Add(0x1B);
        _buffer.Add(0x70);
        _buffer.Add((byte)pin);
        _buffer.Add(onTime);
        _buffer.Add(offTime);
        return this;
    }

    // ------------------------------------------------------------------ Cut/close

    /// <summary>GS V m — cuts the paper.</summary>
    public EscPosBuilder Cut(PaperCut cut = PaperCut.Full)
    {
        // Feed clear of the cutter head first, otherwise the last printed line is
        // sliced through the middle of the text.
        Feed(3);

        _buffer.Add(0x1D);
        _buffer.Add(0x56);
        _buffer.Add((byte)cut);
        return this;
    }

    // -------------------------------------------------------------------- Helpers

    /// <summary>
    /// Printable width of a string, treating East Asian wide characters as two columns.
    /// </summary>
    /// <remarks>
    /// A single CJK ideograph occupies two character cells on a thermal printer, so
    /// counting <c>string.Length</c> would misalign any receipt containing one.
    /// </remarks>
    public static int DisplayWidth(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += IsWide(rune.Value) ? 2 : 1;
        }

        return width;
    }

    /// <summary>Truncates a string so its display width does not exceed <paramref name="maxWidth"/>.</summary>
    public static string TruncateToWidth(string text, int maxWidth)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (maxWidth <= 0)
        {
            return string.Empty;
        }

        var width = 0;
        var builder = new StringBuilder(text.Length);

        foreach (var rune in text.EnumerateRunes())
        {
            var runeWidth = IsWide(rune.Value) ? 2 : 1;
            if (width + runeWidth > maxWidth)
            {
                break;
            }

            builder.Append(rune);
            width += runeWidth;
        }

        return builder.ToString();
    }

    /// <summary>
    /// True for code points that occupy two cells in a monospaced East Asian font.
    /// </summary>
    private static bool IsWide(int codePoint) =>
        codePoint is >= 0x1100 and <= 0x115F        // Hangul Jamo initial consonants
            or >= 0x2E80 and <= 0xA4CF               // CJK radicals through Yi
            or >= 0xAC00 and <= 0xD7A3               // Hangul syllables
            or >= 0xF900 and <= 0xFAFF               // CJK compatibility ideographs
            or >= 0xFE30 and <= 0xFE6F               // CJK compatibility forms
            or >= 0xFF00 and <= 0xFF60               // Fullwidth forms
            or >= 0xFFE0 and <= 0xFFE6               // Fullwidth signs
            or >= 0x1F300 and <= 0x1F64F             // Emoji (printers render as substitutes)
            or >= 0x20000 and <= 0x3FFFD;            // CJK extension planes

    /// <summary>
    /// Writes one GS ( k sub-command.
    /// </summary>
    /// <remarks>
    /// The byte order is critical and easy to get wrong:
    /// <c>GS ( k | cn | fn | pL | pH | operands</c>. The length field counts
    /// <b>cn, fn and every operand byte</b> that follows it, so it is always the
    /// operand count plus two. Getting this wrong produces a symbol that a real
    /// printer silently truncates or ignores.
    /// </remarks>
    /// <param name="fn">The function selector for this sub-command.</param>
    /// <param name="operands">The operand bytes, including the m selector where the spec defines one.</param>
    /// <param name="explicitLength">
    /// Overrides the computed length. Used for the store command, where the length
    /// must also cover the symbol data that follows the operands.
    /// </param>
    private void WriteQrCommand(byte fn, byte[] operands, int? explicitLength = null)
    {
        var length = explicitLength ?? operands.Length + 2;
        var lengthBytes = new byte[] { (byte)(length & 0xFF), (byte)((length >> 8) & 0xFF) };

        // Written with an explicit array literal rather than a collection expression.
        // A collection expression mixing a cast byte and a byte parameter binds to
        // int[] and silently produces the wrong values here, which corrupts the
        // command in a way no test would notice without a byte-level assertion.
        var selector = new byte[2];
        selector[0] = 0x31; // cn — the 2D symbol family
        selector[1] = fn;

        var introducer = new byte[3];
        introducer[0] = 0x1D;
        introducer[1] = 0x28;
        introducer[2] = 0x6B;

        WriteCommand(introducer, selector, lengthBytes, operands);
    }

    private void WriteVariableLengthBarcode(byte barcodeType, byte[] payload)
    {
        // GS k m n d1...dn where n is the payload length. Built as an explicit array
        // so the byte values cannot be widened into an int[] by a collection expression.
        var header = new byte[4];
        header[0] = 0x1D;
        header[1] = 0x6B;
        header[2] = barcodeType;
        header[3] = (byte)payload.Length;

        WriteCommand(header, payload);
    }

    /// <summary>
    /// Appends each segment to the buffer.
    /// </summary>
    /// <remarks>
    /// Takes <c>params byte[][]</c> so a call site reads as the literal byte sequence
    /// being sent. Every segment must be an explicit <c>byte[]</c>: a collection
    /// expression that mixes a cast byte with a byte parameter binds to <c>int[]</c>
    /// and silently emits the wrong values.
    /// </remarks>
    private void WriteCommand(params byte[][] segments)
    {
        foreach (var segment in segments)
        {
            _buffer.AddRange(segment);
        }
    }

    private static void RequireExactDigits(string data, int expected, string symbology)
    {
        if (data.Length != expected || !data.All(char.IsAsciiDigit))
        {
            throw new ArgumentException(
                $"{symbology} requires exactly {expected} digits, got '{data}'.", nameof(data));
        }
    }
}
