// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using System.Globalization;
using System.Text;
using Pos.Core.Domain;

namespace Pos.Devices.Zpl;

/// <summary>
/// Builds ZPL II label documents for Zebra-compatible label printers.
/// </summary>
/// <remarks>
/// <para>
/// ZPL is a completely different command language from ESC/POS: a receipt is a stream of
/// line-oriented text commands, whereas a label is an absolutely-positioned canvas where
/// every field carries its own origin. That is why labels get their own builder rather than
/// a mode on <c>EscPosBuilder</c>.
/// </para>
/// <para>
/// Coordinates are in dots at the printer's resolution, so <see cref="DotsPerMillimetre"/>
/// is how physical sizes are converted. Everything here is expressed in millimetres at the
/// call site and converted internally, because a label that is 2mm out will not fit the
/// product it is meant for.
/// </para>
/// </remarks>
public sealed class ZplBuilder
{
    private readonly List<string> _commands = [];
    private bool _started;

    /// <param name="widthMm">Label media width, across the web.</param>
    /// <param name="heightMm">Label media height, along the feed direction.</param>
    /// <param name="dpi">Print head resolution. 203 dpi is standard; 300 dpi is high-resolution.</param>
    public ZplBuilder(double widthMm = 50, double heightMm = 25, int dpi = 203)
    {
        if (widthMm <= 0 || heightMm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(widthMm), "Label dimensions must be positive.");
        }

        if (dpi is not (203 or 300 or 600))
        {
            throw new ArgumentOutOfRangeException(
                nameof(dpi), dpi, "Print resolution must be 203, 300, or 600 dpi.");
        }

        WidthMm = widthMm;
        HeightMm = heightMm;
        Dpi = dpi;
    }

    public double WidthMm { get; }

    public double HeightMm { get; }

    public int Dpi { get; }

    /// <summary>
    /// Dots per millimetre at this resolution: 203 dpi is 7.992 dots/mm, 300 dpi is ~11.81.
    /// </summary>
    /// <remarks>
    /// This is the constant that makes label output physically correct. Getting it wrong
    /// scales every element and the label silently misprints. 203 dpi is deliberately not
    /// rounded to 8: treated as 8, a 50mm label comes out 50.05mm wide, and the error grows
    /// with the label until it no longer fits the product it was printed for.
    /// </remarks>
    public double DotsPerMillimetre => Dpi / 25.4d;

    /// <summary>Label width in dots.</summary>
    public int WidthDots => ToDots(WidthMm);

    /// <summary>Label height in dots.</summary>
    public int HeightDots => ToDots(HeightMm);

    /// <summary>Converts millimetres to printer dots at this resolution.</summary>
    public int ToDots(double millimetres) => (int)Math.Round(millimetres * DotsPerMillimetre);

    /// <summary>
    /// Starts the label: sets the print width and clears the image buffer.
    /// </summary>
    /// <remarks>
    /// The label length is deliberately left to <c>^XA</c> defaults rather than being forced
    /// to the media length, so a shorter label does not feed a long blank tail.
    /// </remarks>
    public ZplBuilder Start()
    {
        _commands.Add("^XA");
        _commands.Add($"^PW{WidthDots}");
        _commands.Add("^CI28"); // UTF-8 input, so accented product names survive
        _started = true;

        return this;
    }

    /// <summary>Ends the label and prints it.</summary>
    public ZplBuilder End()
    {
        _commands.Add("^XZ");
        _started = false;

        return this;
    }

    /// <summary>
    /// Writes a text field.
    /// </summary>
    /// <param name="xMm">Left origin, from the label edge.</param>
    /// <param name="yMm">Baseline origin, from the top of the label.</param>
    /// <param name="text">Text to print.</param>
    /// <param name="fontHeightDots">Character height in dots.</param>
    /// <param name="fontWidthDots">Character width in dots. Defaults to the height.</param>
    /// <param name="font">ZPL font designator, A to H. A is the scalable outline font.</param>
    public ZplBuilder Text(
        double xMm,
        double yMm,
        string text,
        int fontHeightDots = 30,
        int? fontWidthDots = null,
        char font = 'A')
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(text);

        var width = fontWidthDots ?? fontHeightDots;

        // Field origin, then font selection, then the data. The text is escaped because a
        // stray ^ or ~ would otherwise be read as the start of another command.
        _commands.Add($"^FO{ToDots(xMm)},{ToDots(yMm)}");
        _commands.Add($"^{font}N,{fontHeightDots},{width}");
        _commands.Add($"^FD{Escape(text)}^FS");

        return this;
    }

    /// <summary>Writes text rotated 90 degrees, for a vertical spine label.</summary>
    public ZplBuilder TextRotated(
        double xMm,
        double yMm,
        string text,
        int fontHeightDots = 30,
        char font = 'A')
    {
        EnsureStarted();

        _commands.Add($"^FO{ToDots(xMm)},{ToDots(yMm)}");
        _commands.Add("^FWR"); // field rotation, clockwise
        _commands.Add($"^{font}N,{fontHeightDots},{fontHeightDots}");
        _commands.Add($"^FD{Escape(text)}^FS");
        _commands.Add("^FW N"); // restore normal orientation for later fields

        return this;
    }

    /// <summary>
    /// Writes a Code 128 barcode.
    /// </summary>
    /// <param name="xMm">Left origin.</param>
    /// <param name="yMm">Top origin.</param>
    /// <param name="data">Payload. Code 128 subset B handles alphanumerics.</param>
    /// <param name="heightDots">Barcode height in dots.</param>
    /// <param name="printInterpretationLine">Whether to print the value beneath the bars.</param>
    public ZplBuilder Barcode128(
        double xMm,
        double yMm,
        string data,
        int heightDots = 60,
        bool printInterpretationLine = true,
        int moduleWidth = 2)
    {
        EnsureStarted();
        ArgumentException.ThrowIfNullOrEmpty(data);

        if (moduleWidth is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(moduleWidth), "Module width must be 1-10 dots; narrower may not scan.");
        }

        _commands.Add($"^FO{ToDots(xMm)},{ToDots(yMm)}");
        _commands.Add($"^BY{moduleWidth}");
        _commands.Add($"^BCN,{heightDots},{(printInterpretationLine ? 'Y' : 'N')},N,N");
        _commands.Add($"^FD{Escape(data)}^FS");

        return this;
    }

    /// <summary>
    /// Writes an EAN-13 barcode.
    /// </summary>
    /// <remarks>
    /// EAN-13 is fixed at 13 digits including the check digit, and a malformed value
    /// produces a label no scanner will read — so it is rejected here rather than printed.
    /// </remarks>
    public ZplBuilder BarcodeEan13(double xMm, double yMm, string data, int heightDots = 60)
    {
        EnsureStarted();

        if (data.Length != 13 || !data.All(char.IsAsciiDigit))
        {
            throw new ArgumentException(
                $"EAN-13 requires exactly 13 digits, got '{data}'.", nameof(data));
        }

        _commands.Add($"^FO{ToDots(xMm)},{ToDots(yMm)}");
        _commands.Add($"^BEN,{heightDots},Y,N");
        _commands.Add($"^FD{Escape(data)}^FS");

        return this;
    }

    /// <summary>Draws a filled rectangle, for a separator or a colour block.</summary>
    public ZplBuilder Box(
        double xMm,
        double yMm,
        double widthMm,
        double thicknessMm = 0.5,
        string colour = "B")
    {
        EnsureStarted();

        if (colour is not ("B" or "W"))
        {
            throw new ArgumentOutOfRangeException(nameof(colour), colour, "Colour must be B (black) or W (white).");
        }

        _commands.Add($"^FO{ToDots(xMm)},{ToDots(yMm)}");
        _commands.Add($"^GB{ToDots(widthMm)},{ToDots(thicknessMm)},{ToDots(thicknessMm)},{colour},0^FS");

        return this;
    }

    /// <summary>
    /// Prints a rasterised image from a 1-bit monochrome bitmap.
    /// </summary>
    /// <remarks>
    /// Uses <c>^GFA</c>, which takes the image as raw hex rather than a stored graphic. That
    /// avoids depending on a graphic having been downloaded to the printer beforehand, which
    /// is what makes a label printer work on a terminal that has never seen it before.
    /// </remarks>
    /// <param name="xMm">Left origin.</param>
    /// <param name="yMm">Top origin.</param>
    /// <param name="widthBytes">Bytes per row.</param>
    /// <param name="heightDots">Rows.</param>
    /// <param name="monochrome">Packed 1-bit data, most significant bit first.</param>
    public ZplBuilder Image(
        double xMm,
        double yMm,
        int widthBytes,
        int heightDots,
        byte[] monochrome)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(monochrome);

        var expected = widthBytes * heightDots;
        if (monochrome.Length != expected)
        {
            throw new ArgumentException(
                $"Expected {expected} bytes for a {widthBytes}x{heightDots} image, got {monochrome.Length}.",
                nameof(monochrome));
        }

        var totalBytes = expected;
        var bytesPerRow = widthBytes;

        _commands.Add($"^FO{ToDots(xMm)},{ToDots(yMm)}");
        _commands.Add($"^GFA,{totalBytes},{totalBytes},{bytesPerRow},{Convert.ToHexString(monochrome)}");

        return this;
    }

    /// <summary>Feeds the label to the tear-off position.</summary>
    public ZplBuilder Feed(int dots = 0)
    {
        EnsureStarted();
        _commands.Add(dots > 0 ? $"^FD{dots}" : "^FD");

        return this;
    }

    /// <summary>Returns the assembled ZPL document.</summary>
    public override string ToString()
    {
        if (_commands.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("\n", _commands) + "\n";
    }

    /// <summary>
    /// Encodes the document as bytes.
    /// </summary>
    /// <remarks>
    /// UTF-8 because <c>^CI28</c> is emitted in <see cref="Start"/>, so the printer expects
    /// it. A label with an accented product name would otherwise arrive as mojibake.
    /// </remarks>
    public byte[] ToArray() => Encoding.UTF8.GetBytes(ToString());

    private void EnsureStarted()
    {
        if (!_started)
        {
            throw new InvalidOperationException(
                "Call Start() before adding label fields, so the print width is established.");
        }
    }

    /// <summary>
    /// Escapes the characters ZPL would otherwise read as control codes.
    /// </summary>
    /// <remarks>
    /// A product name containing a caret or tilde is ordinary retail data — "2~3 kg" is a
    /// realistic example — and unescaped it would truncate the field or start a new command.
    /// </remarks>
    public static string Escape(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            switch (character)
            {
                case '^':
                case '~':
                case '\\':
                    builder.Append('\\');
                    builder.Append(character);
                    break;

                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// Renders shelf and price labels onto a ZPL printer.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the receipt renderer because a label answers a different question: it is
/// read from a metre away in a shop aisle, so the price dominates and the barcode must scan
/// first time from a hand-held reader at an awkward angle.
/// </para>
/// <para>
/// Stateless, and therefore static: label geometry is supplied per call rather than held,
/// because one terminal may drive both a shelf-edge printer and a larger case label printer.
/// </para>
/// </remarks>
public static class LabelRenderer
{
    /// <summary>
    /// Renders a shelf-edge label for a product.
    /// </summary>
    /// <param name="label">What to print.</param>
    /// <param name="widthMm">Label width.</param>
    /// <param name="heightMm">Label height.</param>
    /// <param name="dpi">Printer resolution.</param>
    public static byte[] RenderShelfLabel(ShelfLabel label, double widthMm = 50, double heightMm = 25, int dpi = 203)
    {
        ArgumentNullException.ThrowIfNull(label);

        var zpl = new ZplBuilder(widthMm, heightMm, dpi).Start();

        // Price is the largest element: it is what the customer reads from the aisle.
        zpl.Text(2, 1, label.PriceDisplay, fontHeightDots: 60);

        // Name wraps to a second line when long, because a truncated product name on a
        // shelf label is worse than no label.
        var nameLines = WrapByWidth(label.Name, Math.Max(8, (int)(widthMm - 4) / 2));
        for (var i = 0; i < Math.Min(2, nameLines.Count); i++)
        {
            zpl.Text(2, 11 + (i * 3.2), nameLines[i], fontHeightDots: 22);
        }

        if (!string.IsNullOrWhiteSpace(label.Sku))
        {
            zpl.Text(widthMm - 16, 1, label.Sku, fontHeightDots: 20);
        }

        // The barcode sits at the bottom, away from the price, so a scanner aimed at the
        // label does not have to compete with other ink for contrast.
        zpl.Barcode128(2, heightMm - 9, label.Barcode, heightDots: 55, printInterpretationLine: false);

        zpl.End();

        return zpl.ToArray();
    }

    /// <summary>Renders a barcode-only label, for tagging stock or shelving.</summary>
    public static byte[] RenderBarcodeLabel(
        string barcode,
        string? caption = null,
        double widthMm = 50,
        double heightMm = 25,
        int dpi = 203)
    {
        ArgumentException.ThrowIfNullOrEmpty(barcode);

        var zpl = new ZplBuilder(widthMm, heightMm, dpi).Start();

        zpl.Barcode128(2, 2, barcode, heightDots: 120);

        if (!string.IsNullOrWhiteSpace(caption))
        {
            zpl.Text(2, heightMm - 6, caption, fontHeightDots: 20);
        }

        zpl.End();

        return zpl.ToArray();
    }

    /// <summary>
    /// Splits text into lines no longer than the given character budget.
    /// </summary>
    /// <remarks>
    /// Breaks on spaces where possible so a product name does not split mid-word, falling
    /// back to a hard break for a single long token.
    /// </remarks>
    public static List<string> WrapByWidth(string text, int maxChars)
    {
        var lines = new List<string>();

        if (string.IsNullOrWhiteSpace(text) || maxChars <= 0)
        {
            return lines;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = new StringBuilder();

        foreach (var word in words)
        {
            if (current.Length == 0)
            {
                // A single word longer than the line is hard-broken rather than dropped.
                if (word.Length > maxChars)
                {
                    for (var offset = 0; offset < word.Length; offset += maxChars)
                    {
                        var chunk = word.Substring(offset, Math.Min(maxChars, word.Length - offset));
                        if (chunk.Length == maxChars)
                        {
                            lines.Add(chunk);
                        }
                        else
                        {
                            current.Append(chunk);
                        }
                    }
                }
                else
                {
                    current.Append(word);
                }

                continue;
            }

            if (current.Length + 1 + word.Length <= maxChars)
            {
                current.Append(' ').Append(word);
            }
            else
            {
                lines.Add(current.ToString());
                current.Clear();
                current.Append(word);
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }
}

/// <summary>What a shelf-edge label should show.</summary>
/// <param name="Barcode">Scannable product code.</param>
/// <param name="Name">Product name as the customer knows it.</param>
/// <param name="PriceDisplay">
/// Price already formatted for display, including any currency symbol, so the label renderer
/// does not need to know the store's currency conventions.
/// </param>
/// <param name="Sku">Optional stock-keeping unit.</param>
public sealed record ShelfLabel(string Barcode, string Name, string PriceDisplay, string? Sku = null);

/// <summary>
/// The label media loaded in a label printer.
/// </summary>
/// <remarks>
/// <para>
/// Geometry has to be right rather than approximate: a label that is 2mm out does not fit the
/// product it was printed for, and the printer does not warn — it prints a label that looks
/// correct and is the wrong size.
/// </para>
/// <para>
/// Lives beside the builder rather than in the settings screen that writes it, so the rules that
/// keep a printer safe are testable without a browser, and so the same validation guards every
/// caller rather than only the one that remembered to check.
/// </para>
/// </remarks>
/// <param name="WidthMm">Media width across the web.</param>
/// <param name="HeightMm">Media height along the feed direction.</param>
/// <param name="Dpi">Print head resolution: 203 for standard, 300 for high-resolution.</param>
public readonly record struct LabelStock(double WidthMm, double HeightMm, int Dpi)
{
    /// <summary>A 50×25mm shelf-edge label on a standard 203 dpi head.</summary>
    public static LabelStock Default => new(50, 25, 203);

    /// <summary>Resolutions the builder can express.</summary>
    public static IReadOnlyList<int> SupportedResolutions => [203, 300, 600];

    /// <summary>Narrowest media worth allowing, below which nothing legible fits.</summary>
    public const double MinimumWidthMm = 20;

    /// <summary>Widest media worth allowing, above which it is not a label printer.</summary>
    public const double MaximumWidthMm = 200;

    /// <summary>Shortest media worth allowing.</summary>
    public const double MinimumHeightMm = 10;

    /// <summary>Longest media worth allowing.</summary>
    public const double MaximumHeightMm = 300;

    /// <summary>Describes the label as a shop would write it on the box.</summary>
    public string Describe() => $"{WidthMm:0.#} × {HeightMm:0.#} mm at {Dpi} dpi";

    /// <summary>
    /// Checks the geometry before it reaches the printer.
    /// </summary>
    /// <remarks>
    /// The builder rejects bad values too, but by then the operator is mid-print and gets an
    /// exception instead of an explanation. Checking here is what turns a mistyped roll size into
    /// a message on the settings screen.
    /// </remarks>
    public bool IsValid(out string reason)
    {
        if (WidthMm is < MinimumWidthMm or > MaximumWidthMm)
        {
            reason = $"Label width must be between {MinimumWidthMm:0.#} and {MaximumWidthMm:0.#} mm.";
            return false;
        }

        if (HeightMm is < MinimumHeightMm or > MaximumHeightMm)
        {
            reason = $"Label height must be between {MinimumHeightMm:0.#} and {MaximumHeightMm:0.#} mm.";
            return false;
        }

        if (!SupportedResolutions.Contains(Dpi))
        {
            reason = $"Print resolution must be one of {string.Join(", ", SupportedResolutions)} dpi.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
