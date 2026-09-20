// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Devices.EscPos;

namespace Pos.Devices.Tests;

/// <summary>
/// Diagnostic probe for CP437 mapping and command layout. Kept because the CP437
/// table is hand-maintained data that can silently rot, and because byte-level
/// mistakes in ESC/POS do not throw — they corrupt a receipt.
/// </summary>
public sealed class Cp437Tests
{
    [Fact]
    public void Ascii_round_trips_unchanged()
    {
        Assert.Equal([(byte)'A', (byte)'B', (byte)'C'], Cp437.Encode("ABC").ToArray());
        Assert.Equal([(byte)'1', (byte)'2', (byte)'3'], Cp437.Encode("123").ToArray());
        Assert.Equal([(byte)' '], Cp437.Encode(" ").ToArray());
    }

    [Fact]
    public void Common_accented_characters_map_to_their_upper_half_bytes()
    {
        // These are the characters that actually appear in store and product names.
        Assert.Equal([(byte)0x82], Cp437.Encode("é").ToArray());
        Assert.Equal([(byte)0x81], Cp437.Encode("ü").ToArray());
        Assert.Equal([(byte)0x84], Cp437.Encode("ä").ToArray());
        Assert.Equal([(byte)0x94], Cp437.Encode("ö").ToArray());
        Assert.Equal([(byte)0xA4], Cp437.Encode("ñ").ToArray());
        Assert.Equal([(byte)0x9C], Cp437.Encode("£").ToArray());
        Assert.Equal([(byte)0xE1], Cp437.Encode("ß").ToArray());
        Assert.Equal([(byte)0xF8], Cp437.Encode("°").ToArray());
    }

    [Fact]
    public void Currency_symbols_commonly_needed_are_mapped()
    {
        // £, ¥, ¢ and the peseta sign are all present; the euro sign is not in CP437
        // and must degrade rather than silently produce a wrong glyph.
        Assert.True(Cp437.CanEncode(0x00A3));
        Assert.True(Cp437.CanEncode(0x00A5));
        Assert.True(Cp437.CanEncode(0x00A2));

        Assert.False(Cp437.CanEncode(0x20AC)); // €
        Assert.Equal(Cp437.Substitute, Cp437.EncodeRune(0x20AC));
    }

    [Fact]
    public void Unmappable_scripts_degrade_to_a_placeholder()
    {
        Assert.Equal([(byte)'?', (byte)'?', (byte)'?'], Cp437.Encode("日本語").ToArray());
    }

    [Fact]
    public void Control_characters_are_replaced_rather_than_emitted_raw()
    {
        // A raw control byte such as 0x0A inside product text would silently inject a
        // line feed and break the receipt layout. Substituting keeps layout intact.
        Assert.Equal(Cp437.Substitute, Cp437.EncodeRune(0x0A));
        Assert.Equal(Cp437.Substitute, Cp437.EncodeRune(0x1B)); // ESC — would start a command!
        Assert.Equal(Cp437.Substitute, Cp437.EncodeRune(0x00));
    }

    [Fact]
    public void An_escape_character_in_product_data_cannot_inject_a_command()
    {
        // Defensive check: 0x1B is the ESC introducer. If it survived into text the
        // printer would interpret the following bytes as a command instead of text.
        var bytes = new EscPosBuilder().Line("a\u001Bb").ToArray();

        Assert.Equal([(byte)'a', (byte)'?', (byte)'b', 0x0A], bytes);
    }

    [Fact]
    public void Empty_text_encodes_to_nothing()
    {
        Assert.Empty(Cp437.Encode(string.Empty));
    }

    [Fact]
    public void The_upper_half_table_covers_every_cp437_slot()
    {
        // Every CP437 code point must be reachable, otherwise part of the table was
        // truncated by a bad edit. Counting mapped code points in the 0x80-0xFF range
        // is not possible directly because CP437's upper half is a scattered subset of
        // Unicode, so assert a sane lower bound plus spot-check the extremes instead.
        var upperHalfMappings = 0;
        for (var cp = 0x80; cp <= 0xFFFF; cp++)
        {
            if (Cp437.CanEncode(cp))
            {
                upperHalfMappings++;
            }
        }

        Assert.True(
            upperHalfMappings >= 128,
            $"Expected at least 128 non-ASCII mappings, found {upperHalfMappings}.");

        // Spans of Unicode must not leak through unencoded: these are characters that
        // belong to other code pages entirely.
        Assert.False(Cp437.CanEncode(0x0416)); // Cyrillic Zhe
        Assert.False(Cp437.CanEncode(0x05D0)); // Hebrew Alef
        Assert.False(Cp437.CanEncode(0x0E01)); // Thai Ko Kai
        Assert.False(Cp437.CanEncode(0x65E5)); // CJK 日
    }
}
