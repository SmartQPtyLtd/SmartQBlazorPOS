// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Devices.Zpl;

namespace Pos.Devices.Tests;

/// <summary>
/// ZPL label tests.
/// </summary>
/// <remarks>
/// ZPL is a text protocol, so these assert on the emitted command string rather than on
/// bytes. The properties worth pinning are geometric: label output is physically sized, and
/// a wrong dots-per-millimetre constant silently scales every element so the label no longer
/// fits the product or scans reliably.
/// </remarks>
public sealed class ZplBuilderTests
{
    [Fact]
    public void A_label_opens_and_closes_with_the_format_delimiters()
    {
        var zpl = new ZplBuilder().Start().End().ToString();

        Assert.StartsWith("^XA", zpl, StringComparison.Ordinal);
        Assert.EndsWith("^XZ\n", zpl, StringComparison.Ordinal);
    }

    [Fact]
    public void The_print_width_is_the_label_width_in_dots()
    {
        // 50mm at 203 dpi is 50 * 7.9921 = 399.6, which rounds to 400 dots.
        var zpl = new ZplBuilder(widthMm: 50, dpi: 203).Start().End().ToString();

        Assert.Contains("^PW400", zpl, StringComparison.Ordinal);
    }

    [Fact]
    public void Dots_per_millimetre_follows_the_print_head_resolution()
    {
        // Exactly dpi / 25.4 — 203 dpi is 7.992 dots/mm, not a round 8. Treating it as 8
        // would accumulate a 0.1% scale error across a label, which is enough to push a
        // barcode off its intended position on a small shelf label.
        Assert.Equal(7.992d, new ZplBuilder(dpi: 203).DotsPerMillimetre, precision: 3);
        Assert.Equal(11.811d, new ZplBuilder(dpi: 300).DotsPerMillimetre, precision: 3);
        Assert.Equal(23.622d, new ZplBuilder(dpi: 600).DotsPerMillimetre, precision: 3);
    }

    [Fact]
    public void A_higher_resolution_scales_the_same_physical_label()
    {
        // The same 50mm label must be physically identical at both resolutions, so the dot
        // count scales with the head.
        var standard = new ZplBuilder(widthMm: 50, dpi: 203).Start().End().ToString();
        var highRes = new ZplBuilder(widthMm: 50, dpi: 300).Start().End().ToString();

        Assert.Contains("^PW400", standard, StringComparison.Ordinal);
        Assert.Contains("^PW591", highRes, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(72)]
    [InlineData(150)]
    [InlineData(1200)]
    public void An_unsupported_print_resolution_is_rejected(int dpi)
    {
        // Silently accepting one would scale every element and misprint every label.
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZplBuilder(dpi: dpi));
    }

    [Fact]
    public void Label_dimensions_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZplBuilder(widthMm: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZplBuilder(heightMm: -5));
    }

    [Fact]
    public void A_text_field_emits_origin_font_and_data()
    {
        var zpl = new ZplBuilder().Start().Text(10, 5, "COLA 500ML").End().ToString();

        Assert.Contains("^FO80,40", zpl, StringComparison.Ordinal); // 10mm,5mm at 8 dots/mm
        Assert.Contains("^FDCOLA 500ML^FS", zpl, StringComparison.Ordinal);
    }

    [Fact]
    public void Utf8_is_selected_so_accented_names_survive()
    {
        var zpl = new ZplBuilder().Start().Text(1, 1, "Café Crème").End().ToString();

        Assert.Contains("^CI28", zpl, StringComparison.Ordinal);
        Assert.Contains("Café Crème", zpl, StringComparison.Ordinal);
    }

    [Fact]
    public void A_caret_in_a_product_name_cannot_start_a_new_command()
    {
        // "2~3 kg" and "A^B" are ordinary retail data. Unescaped they would truncate the
        // field or inject a command, which is both a printing defect and an injection risk.
        var zpl = new ZplBuilder().Start().Text(1, 1, "A^B~C").End().ToString();

        Assert.Contains(@"^FDA\^B\~C^FS", zpl, StringComparison.Ordinal);
    }

    [Fact]
    public void A_backslash_is_escaped_because_it_is_the_escape_character()
    {
        var zpl = new ZplBuilder().Start().Text(1, 1, @"A\B").End().ToString();

        Assert.Contains(@"^FDA\\B^FS", zpl, StringComparison.Ordinal);
    }

    [Fact]
    public void Rotated_text_restores_normal_orientation_for_later_fields()
    {
        // Without the restore, every subsequent field on the label would come out sideways.
        var zpl = new ZplBuilder().Start().TextRotated(1, 1, "SPINE").Text(1, 20, "AFTER").End().ToString();

        Assert.Contains("^FWR", zpl, StringComparison.Ordinal);
        Assert.Contains("^FW N", zpl, StringComparison.Ordinal);

        var rotatedAt = zpl.IndexOf("^FWR", StringComparison.Ordinal);
        var restoreAt = zpl.IndexOf("^FW N", StringComparison.Ordinal);
        var afterAt = zpl.IndexOf("^FDAFTER", StringComparison.Ordinal);

        Assert.True(rotatedAt < restoreAt, "Rotation must be restored after the rotated field.");
        Assert.True(restoreAt < afterAt, "The restore must precede the following field.");
    }

    [Fact]
    public void A_code128_barcode_sets_module_width_and_height()
    {
        var zpl = new ZplBuilder().Start().Barcode128(2, 10, "ABC123", heightDots: 70).End().ToString();

        Assert.Contains("^BY2", zpl, StringComparison.Ordinal);
        Assert.Contains("^BCN,70,Y,N,N", zpl, StringComparison.Ordinal);
        Assert.Contains("^FDABC123^FS", zpl, StringComparison.Ordinal);
    }

    [Fact]
    public void An_out_of_range_module_width_is_rejected()
    {
        // A module narrower than the print head can resolve produces bars that will not scan.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ZplBuilder().Start().Barcode128(1, 1, "ABC", moduleWidth: 0));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ZplBuilder().Start().Barcode128(1, 1, "ABC", moduleWidth: 11));
    }

    [Fact]
    public void Ean13_accepts_exactly_thirteen_digits()
    {
        var zpl = new ZplBuilder().Start().BarcodeEan13(1, 1, "5901234123457").End().ToString();

        Assert.Contains("^BEN", zpl, StringComparison.Ordinal);
        Assert.Contains("^FD5901234123457^FS", zpl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("590123412345")]
    [InlineData("59012341234578")]
    [InlineData("59012341234AB")]
    public void A_malformed_ean13_is_rejected_rather_than_printed(string barcode)
    {
        // Printing it would produce a label no scanner reads, which is worse than failing
        // loudly when the label is requested.
        Assert.Throws<ArgumentException>(() =>
            new ZplBuilder().Start().BarcodeEan13(1, 1, barcode));
    }

    [Fact]
    public void A_box_emits_the_graphic_field_command()
    {
        var zpl = new ZplBuilder().Start().Box(2, 3, widthMm: 40).End().ToString();

        Assert.Contains("^GB320,4,4,B,0^FS", zpl, StringComparison.Ordinal);
    }

    [Fact]
    public void A_box_colour_must_be_black_or_white()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ZplBuilder().Start().Box(1, 1, 10, colour: "R"));
    }

    [Fact]
    public void An_image_is_emitted_as_hex_with_its_geometry()
    {
        // 4 bytes per row, 2 rows.
        var data = new byte[] { 0xFF, 0x00, 0xFF, 0x00, 0x0F, 0xF0, 0x0F, 0xF0 };

        var zpl = new ZplBuilder().Start().Image(1, 1, widthBytes: 4, heightDots: 2, data).End().ToString();

        Assert.Contains("^GFA,8,8,4,FF00FF000FF00FF0", zpl, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_with_the_wrong_byte_count_is_rejected()
    {
        // A mismatch would print a skewed or truncated graphic rather than failing.
        var exception = Assert.Throws<ArgumentException>(() =>
            new ZplBuilder().Start().Image(1, 1, widthBytes: 4, heightDots: 2, new byte[5]));

        Assert.Contains("Expected 8 bytes", exception.Message);
    }

    [Fact]
    public void Fields_require_an_open_label()
    {
        // Emitting fields outside ^XA/^XZ would print stray commands instead of a label.
        var builder = new ZplBuilder();

        Assert.Throws<InvalidOperationException>(() => builder.Text(1, 1, "X"));
        Assert.Throws<InvalidOperationException>(() => builder.Barcode128(1, 1, "X"));
    }

    [Fact]
    public void An_unused_builder_produces_nothing()
    {
        Assert.Equal(string.Empty, new ZplBuilder().ToString());
    }

    [Fact]
    public void Output_is_encoded_as_utf8_matching_the_declared_code_page()
    {
        var bytes = new ZplBuilder().Start().Text(1, 1, "Café").End().ToArray();
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.Contains("Café", text, StringComparison.Ordinal);
    }
}

/// <summary>Shelf and barcode label rendering.</summary>
public sealed class LabelRendererTests
{
    [Fact]
    public void A_shelf_label_prints_the_price_and_the_barcode()
    {
        var label = new ShelfLabel("6001000000017", "Cola 500ml", "R 15.00", "SKU-1");

        var text = System.Text.Encoding.UTF8.GetString(LabelRenderer.RenderShelfLabel(label));

        Assert.Contains("R 15.00", text, StringComparison.Ordinal);
        Assert.Contains("Cola 500ml", text, StringComparison.Ordinal);
        Assert.Contains("6001000000017", text, StringComparison.Ordinal);
        Assert.Contains("SKU-1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_price_is_rendered_larger_than_the_product_name()
    {
        // The price is what a customer reads from across the aisle, so it must dominate.
        var label = new ShelfLabel("6001000000017", "Cola 500ml", "R 15.00");
        var text = System.Text.Encoding.UTF8.GetString(LabelRenderer.RenderShelfLabel(label));

        Assert.Contains("^AN,60,60", text, StringComparison.Ordinal);
        Assert.Contains("^AN,22,22", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_product_name_wraps_rather_than_being_truncated()
    {
        var label = new ShelfLabel(
            "6001000000017",
            "Extra Special Reserve Ground Coffee Beans 500g",
            "R 89.99");

        var text = System.Text.Encoding.UTF8.GetString(LabelRenderer.RenderShelfLabel(label));

        Assert.Contains("Extra Special", text, StringComparison.Ordinal);
        Assert.Contains("Reserve", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_barcode_label_can_be_rendered_without_a_caption()
    {
        var text = System.Text.Encoding.UTF8.GetString(LabelRenderer.RenderBarcodeLabel("6001000000017"));

        Assert.Contains("6001000000017", text, StringComparison.Ordinal);
        Assert.Contains("^BCN", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_barcode_label_rejects_an_empty_barcode()
    {
        Assert.Throws<ArgumentException>(() => LabelRenderer.RenderBarcodeLabel(string.Empty));
    }

    [Fact]
    public void Wrapping_breaks_on_spaces_where_it_can()
    {
        var lines = LabelRenderer.WrapByWidth("alpha beta gamma", 10);

        Assert.Equal(["alpha beta", "gamma"], lines);
    }

    [Fact]
    public void Wrapping_hard_breaks_a_single_token_longer_than_the_line()
    {
        var lines = LabelRenderer.WrapByWidth("ABCDEFGHIJKLM", 5);

        Assert.Equal(["ABCDE", "FGHIJ", "KLM"], lines);
    }

    [Fact]
    public void Wrapping_an_empty_string_yields_no_lines()
    {
        Assert.Empty(LabelRenderer.WrapByWidth("   ", 10));
        Assert.Empty(LabelRenderer.WrapByWidth("text", 0));
    }
}
