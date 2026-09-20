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
/// Tests for the label media geometry a shop configures.
/// </summary>
/// <remarks>
/// Geometry has to be right rather than approximate. A label that is 2mm out does not fit the
/// product it was printed for, and the printer does not warn — it prints a label that looks
/// correct and is the wrong size. So the validation is checked here, before it can reach a printer.
/// </remarks>
public sealed class LabelStockTests
{
    [Fact]
    public void The_default_is_a_shelf_edge_label_on_a_standard_head()
    {
        var stock = LabelStock.Default;

        Assert.Equal(50, stock.WidthMm);
        Assert.Equal(25, stock.HeightMm);
        Assert.Equal(203, stock.Dpi);
        Assert.True(stock.IsValid(out _));
    }

    [Theory]
    [InlineData(50, 25, 203)]
    [InlineData(100, 50, 300)]
    [InlineData(20, 10, 203)]
    [InlineData(200, 300, 600)]
    public void Usable_geometry_is_accepted(double width, double height, int dpi)
    {
        var stock = new LabelStock(width, height, dpi);

        Assert.True(stock.IsValid(out var reason));
        Assert.Empty(reason);
    }

    [Theory]
    [InlineData(0, 25, 203, "width")]
    [InlineData(10, 25, 203, "width")]
    [InlineData(500, 25, 203, "width")]
    [InlineData(50, 0, 203, "height")]
    [InlineData(50, 5, 203, "height")]
    [InlineData(50, 999, 203, "height")]
    public void Geometry_outside_the_usable_range_is_refused_with_a_reason(
        double width, double height, int dpi, string expectedInReason)
    {
        var stock = new LabelStock(width, height, dpi);

        Assert.False(stock.IsValid(out var reason));
        Assert.Contains(expectedInReason, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(150)]
    [InlineData(96)]
    [InlineData(0)]
    [InlineData(-203)]
    public void An_unsupported_resolution_is_refused(int dpi)
    {
        // The builder can only express 203, 300, and 600. Accepting anything else would scale
        // every element on the label, because dots-per-millimetre is derived from it.
        var stock = new LabelStock(50, 25, dpi);

        Assert.False(stock.IsValid(out var reason));
        Assert.Contains("resolution", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_geometry_is_described_the_way_a_shop_writes_it_on_the_box()
    {
        var stock = new LabelStock(50, 25, 203);

        Assert.Equal("50 × 25 mm at 203 dpi", stock.Describe());
    }

    [Fact]
    public void Every_supported_resolution_is_accepted_by_the_builder()
    {
        // The stock's list and the builder's own guard must not drift apart, or a setting the
        // screen accepts would throw at print time.
        foreach (var dpi in LabelStock.SupportedResolutions)
        {
            var stock = new LabelStock(50, 25, dpi);

            Assert.True(stock.IsValid(out _));

            // Constructing the builder is what the printer would do with it.
            var zpl = new ZplBuilder(stock.WidthMm, stock.HeightMm, stock.Dpi);

            Assert.Equal(dpi, zpl.Dpi);
        }
    }

    [Fact]
    public void A_50mm_label_is_not_exactly_400_dots()
    {
        // 203 dpi is 7.992 dots/mm, not a round 8. Treated as 8, a 50mm label comes out 50.05mm
        // wide and the error grows with the label until it no longer fits the product.
        var builder = new ZplBuilder(widthMm: 50, heightMm: 25, dpi: 203);

        Assert.Equal(400, builder.WidthDots);   // 50 × 7.9924 = 399.6, which rounds to 400
        Assert.NotEqual(8d, builder.DotsPerMillimetre);
        Assert.Equal(203 / 25.4d, builder.DotsPerMillimetre, precision: 10);
    }
}
