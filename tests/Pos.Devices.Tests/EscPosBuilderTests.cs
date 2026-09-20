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
/// Golden-byte tests for ESC/POS command generation.
/// </summary>
/// <remarks>
/// These assert the exact byte sequences sent to a thermal printer. That matters
/// because a wrong byte does not throw — the printer either ignores it silently or
/// prints garbage. Real hardware cannot be inspected from a test suite, so pinning
/// the protocol bytes here is the only way to catch a regression without a printer
/// on the desk.
/// </remarks>
public sealed class EscPosBuilderTests
{
    private const int Columns80mm = 48;
    private const int Columns58mm = 32;

    [Fact]
    public void Initialise_emits_ESC_at()
    {
        var bytes = new EscPosBuilder().Initialise().ToArray();

        Assert.Equal([0x1B, 0x40], bytes);
    }

    [Fact]
    public void Align_emits_ESC_a_with_the_right_mode_byte()
    {
        Assert.Equal([0x1B, 0x61, 0x00], new EscPosBuilder().Align(TextAlignment.Left).ToArray());
        Assert.Equal([0x1B, 0x61, 0x01], new EscPosBuilder().Align(TextAlignment.Centre).ToArray());
        Assert.Equal([0x1B, 0x61, 0x02], new EscPosBuilder().Align(TextAlignment.Right).ToArray());
    }

    [Fact]
    public void Bold_sets_and_clears_ESC_E()
    {
        Assert.Equal([0x1B, 0x45, 0x01], new EscPosBuilder().Bold(true).ToArray());
        Assert.Equal([0x1B, 0x45, 0x00], new EscPosBuilder().Bold(false).ToArray());
    }

    [Fact]
    public void TextSize_packs_width_and_height_into_one_byte()
    {
        // Width multiplier goes in the high nibble, height in the low nibble,
        // both offset by one. Double-double is therefore 0x11.
        Assert.Equal([0x1D, 0x21, 0x00], new EscPosBuilder().TextSize(1, 1).ToArray());
        Assert.Equal([0x1D, 0x21, 0x11], new EscPosBuilder().TextSize(2, 2).ToArray());
        Assert.Equal([0x1D, 0x21, 0x22], new EscPosBuilder().TextSize(3, 3).ToArray());
        Assert.Equal([0x1D, 0x21, 0x70], new EscPosBuilder().TextSize(8, 1).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(-1)]
    public void TextSize_rejects_multipliers_outside_the_protocol_range(int invalid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EscPosBuilder().TextSize(invalid, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EscPosBuilder().TextSize(1, invalid));
    }

    [Fact]
    public void Line_encodes_ascii_and_appends_a_line_feed()
    {
        var bytes = new EscPosBuilder().Line("AB").ToArray();

        Assert.Equal([0x41, 0x42, 0x0A], bytes);
    }

    [Fact]
    public void Line_with_no_argument_emits_a_bare_line_feed()
    {
        Assert.Equal([0x0A], new EscPosBuilder().Line().ToArray());
    }

    [Fact]
    public void Feed_emits_ESC_d_with_a_line_count()
    {
        Assert.Equal([0x1B, 0x64, 0x03], new EscPosBuilder().Feed(3).ToArray());
    }

    [Fact]
    public void Feed_of_zero_lines_emits_nothing()
    {
        Assert.Empty(new EscPosBuilder().Feed(0).ToArray());
    }

    [Fact]
    public void Cut_feeds_clear_of_the_blade_before_cutting()
    {
        // The three-line feed is not cosmetic: cutting immediately slices through
        // the last printed line.
        var bytes = new EscPosBuilder().Cut(PaperCut.Full).ToArray();

        Assert.Equal([0x1B, 0x64, 0x03, 0x1D, 0x56, 0x00], bytes);
    }

    [Fact]
    public void Partial_cut_uses_the_partial_mode_byte()
    {
        var bytes = new EscPosBuilder().Cut(PaperCut.Partial).ToArray();

        Assert.Equal([0x1B, 0x64, 0x03, 0x1D, 0x56, 0x01], bytes);
    }

    [Fact]
    public void OpenDrawer_emits_ESC_p_with_pin_and_timings()
    {
        var bytes = new EscPosBuilder().OpenDrawer().ToArray();

        Assert.Equal([0x1B, 0x70, 0x00, 0x19, 0xFA], bytes);
    }

    [Fact]
    public void OpenDrawer_on_pin_five_uses_mode_byte_one()
    {
        var bytes = new EscPosBuilder().OpenDrawer(DrawerPin.Pin5).ToArray();

        Assert.Equal([0x1B, 0x70, 0x01, 0x19, 0xFA], bytes);
    }

    [Fact]
    public void OpenDrawer_rejects_a_zero_on_time()
    {
        // A zero pulse silently fails to fire the solenoid, which presents as a
        // drawer that "sometimes does not open" — painful to diagnose in the field.
        Assert.Throws<ArgumentOutOfRangeException>(() => new EscPosBuilder().OpenDrawer(onTime: 0));
    }

    [Fact]
    public void TwoColumns_pads_the_label_to_the_full_paper_width()
    {
        var bytes = new EscPosBuilder(Columns80mm).TwoColumns("TOTAL", "115.00").ToArray();
        var text = System.Text.Encoding.ASCII.GetString(bytes);

        Assert.Equal("TOTAL" + new string(' ', 48 - 5 - 6) + "115.00\n", text);
        Assert.Equal(Columns80mm + 1, bytes.Length);
    }

    [Fact]
    public void TwoColumns_respects_a_narrow_paper_width()
    {
        var bytes = new EscPosBuilder(Columns58mm).TwoColumns("TOTAL", "115.00").ToArray();
        var text = System.Text.Encoding.ASCII.GetString(bytes);

        Assert.Equal(Columns58mm + 1, bytes.Length);
        Assert.StartsWith("TOTAL", text, StringComparison.Ordinal);
        Assert.EndsWith("115.00\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoColumns_truncates_a_long_label_but_never_the_amount()
    {
        // The amount is the part that must survive intact; a clipped product name is
        // a cosmetic problem, a clipped total is a financial one.
        var bytes = new EscPosBuilder(Columns58mm)
            .TwoColumns("EXTREMELY LONG PRODUCT NAME THAT CANNOT POSSIBLY FIT", "999.99")
            .ToArray();

        var text = System.Text.Encoding.ASCII.GetString(bytes);

        Assert.Equal(Columns58mm + 1, bytes.Length);
        Assert.EndsWith("999.99\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoColumns_when_the_value_alone_overflows_drops_the_label()
    {
        var bytes = new EscPosBuilder(8).TwoColumns("LABEL", "1234567890").ToArray();
        var text = System.Text.Encoding.ASCII.GetString(bytes);

        Assert.Equal("1234567890\n", text);
    }

    [Fact]
    public void Rule_fills_the_configured_width()
    {
        var text = System.Text.Encoding.ASCII.GetString(new EscPosBuilder(Columns58mm).Rule().ToArray());

        Assert.Equal(new string('-', Columns58mm) + "\n", text);
    }

    [Fact]
    public void Heading_centres_emboldens_and_enlarges_then_restores_defaults()
    {
        var bytes = new EscPosBuilder().Heading("RECEIPT", 2).ToArray();

        Assert.Equal(
            [
                0x1B, 0x61, 0x01,       // centre
                0x1B, 0x45, 0x01,       // bold on
                0x1D, 0x21, 0x11,       // double width and height
                0x52, 0x45, 0x43, 0x45, 0x49, 0x50, 0x54, 0x0A, // "RECEIPT\n"
                0x1D, 0x21, 0x00,       // size back to normal
                0x1B, 0x45, 0x00,       // bold off
                0x1B, 0x61, 0x00,       // back to left
            ],
            bytes);
    }

    [Fact]
    public void QrCode_emits_the_expected_GS_paren_k_sequence()
    {
        var bytes = new EscPosBuilder().QrCode("AB", moduleSize: 6, QrErrorCorrection.Medium).ToArray();

        Assert.Equal(
            [
                // Select model 2. Layout is: GS ( k, cn, fn, pL, pH, operands.
                // The declared length counts cn, fn and the operands.
                0x1D, 0x28, 0x6B, 0x31, 0x41, 0x03, 0x00, 0x32,
                // Module size 6
                0x1D, 0x28, 0x6B, 0x31, 0x43, 0x03, 0x00, 0x06,
                // Error correction level M (0x31)
                0x1D, 0x28, 0x6B, 0x31, 0x45, 0x03, 0x00, 0x31,
                // Store "AB": m=0x30 then the data; length 3 + payload 2 = 5
                0x1D, 0x28, 0x6B, 0x31, 0x50, 0x05, 0x00, 0x30, 0x41, 0x42,
                // Print the stored symbol
                0x1D, 0x28, 0x6B, 0x31, 0x51, 0x03, 0x00, 0x30,
            ],
            bytes);
    }

    [Fact]
    public void QrCode_length_field_is_little_endian_for_long_payloads()
    {
        // A payload long enough that the length exceeds one byte exercises the high
        // byte. Getting the order wrong produces a symbol that prints truncated.
        var data = new string('X', 300);
        var bytes = new EscPosBuilder().QrCode(data).ToArray();

        // Locate the store-data command by its full prefix: GS ( k, cn=0x31, fn=0x50.
        var storeIndex = IndexOf(bytes, [0x1D, 0x28, 0x6B, 0x31, 0x50]);
        Assert.True(storeIndex > 0, "Store-data command not found in the QR stream.");

        // pL and pH follow the six-byte prefix, least significant byte first.
        var lengthIndex = storeIndex + 5;
        var declared = bytes[lengthIndex] | (bytes[lengthIndex + 1] << 8);

        Assert.Equal(300 + 3, declared);
    }

    [Fact]
    public void QrCode_rejects_an_out_of_range_module_size()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EscPosBuilder().QrCode("AB", moduleSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EscPosBuilder().QrCode("AB", moduleSize: 17));
    }

    [Fact]
    public void QrCode_rejects_empty_data()
    {
        Assert.Throws<ArgumentException>(() => new EscPosBuilder().QrCode(string.Empty));
    }

    [Fact]
    public void Code128_barcode_prefixes_code_set_B_and_nul_terminates()
    {
        var bytes = new EscPosBuilder().Barcode("ABC123", BarcodeSymbology.Code128).ToArray();

        Assert.Equal(
            [
                0x1D, 0x68, 0x50,             // height 80
                0x1D, 0x48, 0x02,             // text below
                0x1D, 0x77, 0x02,             // module width 2
                0x1D, 0x6B, 0x49, 0x09,       // GS k, Code128, length 9
                0x7B, 0x42,                   // "{B" selects code set B
                0x41, 0x42, 0x43, 0x31, 0x32, 0x33, // "ABC123"
                0x00,                         // terminator
            ],
            bytes);
    }

    [Fact]
    public void Code39_wraps_the_payload_in_start_and_stop_asterisks()
    {
        var bytes = new EscPosBuilder().Barcode("ABC", BarcodeSymbology.Code39).ToArray();

        // The delimiters are mandatory; omitting them yields a barcode no scanner reads.
        Assert.Contains((byte)'*', bytes);
        Assert.Equal(2, bytes.Count(b => b == (byte)'*'));
    }

    [Fact]
    public void Code39_does_not_double_up_existing_delimiters()
    {
        var bytes = new EscPosBuilder().Barcode("*ABC*", BarcodeSymbology.Code39).ToArray();

        Assert.Equal(2, bytes.Count(b => b == (byte)'*'));
    }

    [Fact]
    public void Ean13_uses_the_fixed_length_command_form()
    {
        var bytes = new EscPosBuilder().Barcode("5901234123457", BarcodeSymbology.Ean13).ToArray();

        Assert.Equal(
            [
                0x1D, 0x68, 0x50,
                0x1D, 0x48, 0x02,
                0x1D, 0x77, 0x02,
                0x1D, 0x6B, 0x43, 0x0D,       // GS k 67 13, with no explicit length field
                0x35, 0x39, 0x30, 0x31, 0x32, 0x33, 0x34, 0x31, 0x32, 0x33, 0x34, 0x35, 0x37,
            ],
            bytes);
    }

    [Theory]
    [InlineData("12345", "EAN-13")]
    [InlineData("590123412345", "EAN-13")]
    [InlineData("59012341234578", "EAN-13")]
    [InlineData("59012341234A", "EAN-13")]
    public void Ean13_rejects_anything_that_is_not_exactly_thirteen_digits(string data, string symbology)
    {
        // A malformed retail barcode would print as an unscannable symbol, which is
        // worse than a loud failure at the till.
        var exception = Assert.Throws<ArgumentException>(() =>
            new EscPosBuilder().Barcode(data, BarcodeSymbology.Ean13));

        Assert.Contains(symbology, exception.Message);
    }

    [Fact]
    public void UpcA_requires_twelve_digits()
    {
        Assert.Throws<ArgumentException>(() => new EscPosBuilder().Barcode("123", BarcodeSymbology.UpcA));
        new EscPosBuilder().Barcode("012345678905", BarcodeSymbology.UpcA);
    }

    [Fact]
    public void Ean8_requires_eight_digits()
    {
        new EscPosBuilder().Barcode("12345670", BarcodeSymbology.Ean8);
        Assert.Throws<ArgumentException>(() => new EscPosBuilder().Barcode("1234567", BarcodeSymbology.Ean8));
    }

    [Fact]
    public void Barcode_height_outside_the_protocol_range_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EscPosBuilder().Barcode("ABC", BarcodeSymbology.Code128, height: 0));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EscPosBuilder().Barcode("ABC", BarcodeSymbology.Code128, height: 256));
    }

    [Fact]
    public void Accented_characters_are_mapped_into_cp437()
    {
        // "Café" — CP437 keeps the accented e at 0x82, so the receipt stays readable
        // instead of degrading to a substitute character.
        var bytes = new EscPosBuilder().Text("Café").ToArray();

        Assert.Equal([0x43, 0x61, 0x66, 0x82], bytes);
    }

    [Fact]
    public void Unmappable_characters_degrade_to_a_placeholder_rather_than_throwing()
    {
        // A receipt with a stray '?' is recoverable mid-sale; an exception is not.
        var bytes = new EscPosBuilder().Text("日本語").ToArray();

        Assert.NotEmpty(bytes);
        Assert.All(bytes, b => Assert.Equal((byte)'?', b));
    }

    [Fact]
    public void DisplayWidth_counts_east_asian_wide_characters_as_two_columns()
    {
        Assert.Equal(3, EscPosBuilder.DisplayWidth("abc"));
        Assert.Equal(6, EscPosBuilder.DisplayWidth("日本語"));
        Assert.Equal(6, EscPosBuilder.DisplayWidth("ab日本"));
    }

    [Fact]
    public void TwoColumns_aligns_by_printed_width_when_the_label_is_not_cp437_representable()
    {
        // 日本語 cannot be represented in CP437, so the printer fills three cells with
        // '?' — not the six cells the Unicode width would suggest. Padding must follow
        // the printed width or every amount on the receipt lands in the wrong column.
        var bytes = new EscPosBuilder(Columns80mm).TwoColumns("日本語", "50.00").ToArray();
        var text = System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\n');

        // 48 cells total: 43 label cells (3 printed + 40 padding) plus 5 for "50.00".
        Assert.Equal(Columns80mm, Cp437.Encode(text).Length);
        Assert.EndsWith("50.00", text, StringComparison.Ordinal);

        // The label degraded to three placeholders.
        Assert.StartsWith("???", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoColumns_aligns_correctly_with_accented_characters()
    {
        // é is a single CP437 byte, so it occupies one cell and must be measured as one.
        var bytes = new EscPosBuilder(Columns80mm).TwoColumns("Café", "12.00").ToArray();
        var text = System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\n');

        Assert.Equal(Columns80mm, text.Length);
        Assert.EndsWith("12.00", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_label_that_exactly_fills_the_available_space_needs_no_padding()
    {
        var text = System.Text.Encoding.ASCII.GetString(
            new EscPosBuilder(10).TwoColumns("ABCDE", "12345").ToArray()).TrimEnd('\n');

        Assert.Equal("ABCDE12345", text);
    }

    [Fact]
    public void TruncateToWidth_never_exceeds_the_budget()
    {
        Assert.Equal("abc", EscPosBuilder.TruncateToWidth("abcdef", 3));
        Assert.Equal("日本", EscPosBuilder.TruncateToWidth("日本語", 4));
        Assert.Equal("日", EscPosBuilder.TruncateToWidth("日本語", 3)); // a wide char will not straddle the edge
        Assert.Equal(string.Empty, EscPosBuilder.TruncateToWidth("abc", 0));
    }

    [Fact]
    public void A_constructor_column_count_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EscPosBuilder(0));
    }

    [Fact]
    public void Only_cp437_is_accepted_until_other_tables_are_implemented()
    {
        // Failing loudly beats silently emitting text the printer will mojibake.
        Assert.Throws<NotSupportedException>(() => new EscPosBuilder(48, codePage: 850));
    }

    [Fact]
    public void The_full_receipt_flow_produces_a_complete_document()
    {
        // End-to-end shape check: initialise, header, items, totals, cut, drawer.
        var bytes = new EscPosBuilder(Columns80mm)
            .Initialise()
            .Align(TextAlignment.Centre)
            .Bold()
            .Line("MY STORE")
            .Bold(false)
            .Align(TextAlignment.Left)
            .Rule('=')
            .TwoColumns("1x Cola", "15.00")
            .TwoColumns("TOTAL", "15.00")
            .Rule('=')
            .OpenDrawer()
            .Cut()
            .ToArray();

        Assert.Equal(0x1B, bytes[0]);
        Assert.Equal(0x40, bytes[1]);
        Assert.Contains((byte)0x70, bytes); // drawer pulse present
        Assert.Equal(0x1D, bytes[^3]);
        Assert.Equal(0x56, bytes[^2]);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (needle.Where((t, j) => haystack[i + j] != t).Any())
            {
                continue;
            }

            return i;
        }

        return -1;
    }
}
