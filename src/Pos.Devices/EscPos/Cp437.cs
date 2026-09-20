// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

namespace Pos.Devices.EscPos;

/// <summary>
/// Translates text into the CP437 byte values a thermal printer expects.
/// </summary>
/// <remarks>
/// <para>
/// Implemented by hand rather than via <c>Encoding.GetEncoding(437)</c> for three
/// reasons that all matter here:
/// </para>
/// <list type="number">
/// <item>Code page 437 is not available in .NET (Core) out of the box; the
/// <c>System.Text.Encoding.CodePages</c> package is needed to add it.</item>
/// <item>That package carries native dependencies and is a poor fit for a
/// <b>Blazor WebAssembly</b> client, where the whole point is that receipt
/// generation runs in the browser, offline.</item>
/// <item>A hand-written table is deterministic and directly assertable byte for byte,
/// which is exactly what the golden-byte tests need.</item>
/// </list>
/// <para>
/// Unmappable characters become <c>'?'</c> (0x3F). A receipt with a stray question
/// mark is recoverable mid-sale; an exception or a printer left mid-job is not.
/// </para>
/// </remarks>
public static class Cp437
{
    /// <summary>Substitute emitted for any character CP437 cannot represent.</summary>
    public const byte Substitute = 0x3F; // '?'

    /// <summary>
    /// Maps a Unicode code point to its CP437 byte, or -1 when unmappable.
    /// </summary>
    /// <remarks>
    /// Bytes 0x00-0x7F are ASCII-identical, so they are handled arithmetically rather
    /// than being listed. Only the 0x80-0xFF upper half needs a table.
    /// </remarks>
    private static readonly Dictionary<int, byte> UpperHalf = new()
    {
        // 0x80-0x8F
        [0x00C7] = 0x80, // Ç
        [0x00FC] = 0x81, // ü
        [0x00E9] = 0x82, // é
        [0x00E2] = 0x83, // â
        [0x00E4] = 0x84, // ä
        [0x00E0] = 0x85, // à
        [0x00E5] = 0x86, // å
        [0x00E7] = 0x87, // ç
        [0x00EA] = 0x88, // ê
        [0x00EB] = 0x89, // ë
        [0x00E8] = 0x8A, // è
        [0x00EF] = 0x8B, // ï
        [0x00EE] = 0x8C, // î
        [0x00EC] = 0x8D, // ì
        [0x00C4] = 0x8E, // Ä
        [0x00C5] = 0x8F, // Å

        // 0x90-0x9F
        [0x00C9] = 0x90, // É
        [0x00E6] = 0x91, // æ
        [0x00C6] = 0x92, // Æ
        [0x00F4] = 0x93, // ô
        [0x00F6] = 0x94, // ö
        [0x00F2] = 0x95, // ò
        [0x00FB] = 0x96, // û
        [0x00F9] = 0x97, // ù
        [0x00FF] = 0x98, // ÿ
        [0x00D6] = 0x99, // Ö
        [0x00DC] = 0x9A, // Ü
        [0x00A2] = 0x9B, // ¢
        [0x00A3] = 0x9C, // £
        [0x00A5] = 0x9D, // ¥
        [0x20A7] = 0x9E, // ₧
        [0x0192] = 0x9F, // ƒ

        // 0xA0-0xAF
        [0x00E1] = 0xA0, // á
        [0x00ED] = 0xA1, // í
        [0x00F3] = 0xA2, // ó
        [0x00FA] = 0xA3, // ú
        [0x00F1] = 0xA4, // ñ
        [0x00D1] = 0xA5, // Ñ
        [0x00AA] = 0xA6, // ª
        [0x00BA] = 0xA7, // º
        [0x00BF] = 0xA8, // ¿
        [0x2310] = 0xA9, // ⌐
        [0x00AC] = 0xAA, // ¬
        [0x00BD] = 0xAB, // ½
        [0x00BC] = 0xAC, // ¼
        [0x00A1] = 0xAD, // ¡
        [0x00AB] = 0xAE, // «
        [0x00BB] = 0xAF, // »

        // 0xB0-0xBF
        [0x2591] = 0xB0, // ░
        [0x2592] = 0xB1, // ▒
        [0x2593] = 0xB2, // ▓
        [0x2502] = 0xB3, // │
        [0x2524] = 0xB4, // ┤
        [0x2561] = 0xB5, // ╡
        [0x2562] = 0xB6, // ╢
        [0x2556] = 0xB7, // ╖
        [0x2555] = 0xB8, // ╕
        [0x2563] = 0xB9, // ╣
        [0x2551] = 0xBA, // ║
        [0x2557] = 0xBB, // ╗
        [0x255D] = 0xBC, // ╝
        [0x255C] = 0xBD, // ╜
        [0x255B] = 0xBE, // ╛
        [0x2510] = 0xBF, // ┐

        // 0xC0-0xCF
        [0x2514] = 0xC0, // └
        [0x2534] = 0xC1, // ┴
        [0x252C] = 0xC2, // ┬
        [0x251C] = 0xC3, // ├
        [0x2500] = 0xC4, // ─
        [0x253C] = 0xC5, // ┼
        [0x255E] = 0xC6, // ╞
        [0x255F] = 0xC7, // ╟
        [0x255A] = 0xC8, // ╚
        [0x2554] = 0xC9, // ╔
        [0x2569] = 0xCA, // ╩
        [0x2566] = 0xCB, // ╦
        [0x2560] = 0xCC, // ╠
        [0x2550] = 0xCD, // ═
        [0x256C] = 0xCE, // ╬
        [0x2567] = 0xCF, // ╧

        // 0xD0-0xDF
        [0x2568] = 0xD0, // ╨
        [0x2564] = 0xD1, // ╤
        [0x2565] = 0xD2, // ╥
        [0x2559] = 0xD3, // ╙
        [0x2558] = 0xD4, // ╘
        [0x2552] = 0xD5, // ╒
        [0x2553] = 0xD6, // ╓
        [0x256B] = 0xD7, // ╫
        [0x256A] = 0xD8, // ╪
        [0x2518] = 0xD9, // ┘
        [0x250C] = 0xDA, // ┌
        [0x2588] = 0xDB, // █
        [0x2584] = 0xDC, // ▄
        [0x258C] = 0xDD, // ▌
        [0x2590] = 0xDE, // ▐
        [0x2580] = 0xDF, // ▀

        // 0xE0-0xEF
        [0x03B1] = 0xE0, // α
        [0x00DF] = 0xE1, // ß
        [0x0393] = 0xE2, // Γ
        [0x03C0] = 0xE3, // π
        [0x03A3] = 0xE4, // Σ
        [0x03C3] = 0xE5, // σ
        [0x00B5] = 0xE6, // µ
        [0x03C4] = 0xE7, // τ
        [0x03A6] = 0xE8, // Φ
        [0x0398] = 0xE9, // Θ
        [0x03A9] = 0xEA, // Ω
        [0x03B4] = 0xEB, // δ
        [0x221E] = 0xEC, // ∞
        [0x03C6] = 0xED, // φ
        [0x03B5] = 0xEE, // ε
        [0x2229] = 0xEF, // ∩

        // 0xF0-0xFF
        [0x2261] = 0xF0, // ≡
        [0x00B1] = 0xF1, // ±
        [0x2265] = 0xF2, // ≥
        [0x2264] = 0xF3, // ≤
        [0x2320] = 0xF4, // ⌠
        [0x2321] = 0xF5, // ⌡
        [0x00F7] = 0xF6, // ÷
        [0x2248] = 0xF7, // ≈
        [0x00B0] = 0xF8, // °
        [0x2219] = 0xF9, // ∙
        [0x00B7] = 0xFA, // ·
        [0x221A] = 0xFB, // √
        [0x207F] = 0xFC, // ⁿ
        [0x00B2] = 0xFD, // ²
        [0x25A0] = 0xFE, // ■
        [0x00A0] = 0xFF, // non-breaking space
    };

    /// <summary>
    /// Encodes text to CP437 bytes, substituting <c>'?'</c> for unmappable characters.
    /// </summary>
    public static byte[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return [];
        }

        var result = new List<byte>(text.Length);

        foreach (var rune in text.EnumerateRunes())
        {
            result.Add(EncodeRune(rune.Value));
        }

        return [.. result];
    }

    /// <summary>
    /// Encodes a single Unicode code point to its CP437 byte.
    /// </summary>
    public static byte EncodeRune(int codePoint)
    {
        // Printable ASCII maps straight through and is by far the common case.
        if (codePoint is >= 0x20 and <= 0x7E)
        {
            return (byte)codePoint;
        }

        if (UpperHalf.TryGetValue(codePoint, out var mapped))
        {
            return mapped;
        }

        // Everything else, including control characters and any non-CP437 script,
        // degrades to a visible placeholder rather than corrupting the stream.
        return Substitute;
    }

    /// <summary>True when CP437 can represent the given code point.</summary>
    public static bool CanEncode(int codePoint) =>
        codePoint is >= 0x20 and <= 0x7E || UpperHalf.ContainsKey(codePoint);
}
