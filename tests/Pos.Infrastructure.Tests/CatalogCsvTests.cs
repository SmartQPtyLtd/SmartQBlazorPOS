// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Infrastructure.Catalog;
using Pos.Infrastructure.Storage;

namespace Pos.Infrastructure.Tests;

/// <summary>
/// Tests for reading and writing the catalogue as CSV.
/// </summary>
/// <remarks>
/// The format is the one a shop already has — a price list out of a spreadsheet — so the failures worth
/// guarding against are the quiet ones: a comma inside a product name shifting every later column, a
/// tax rate arriving as a percentage instead of a fraction, and a re-import creating a second copy of
/// every article.
/// </remarks>
public sealed class CatalogCsvTests
{
    private const string Store = "01a0ac06000070008000000000000001";

    [Fact]
    public void A_catalogue_survives_a_round_trip()
    {
        var original = new[]
        {
            Product(id: "p1", barcode: "6001000000017", name: "Cola 500ml", sku: "BEV-01", category: "Beverages"),
            Product(id: "p2", barcode: "6001000000024", name: "White Bread", reorderLevel: 6m, active: false),
        };

        var result = CatalogCsv.Read(CatalogCsv.Write(original), Store);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Rows.Count);

        // Everything but the id survives. The id does not, deliberately: a shop's spreadsheet has no id
        // column, so every row in a file read without the existing catalogue is a new article.
        var first = result.Rows[0].Product;

        Assert.Equal(original[0], first with { Id = original[0].Id });
        Assert.NotEqual(original[0].Id, first.Id);

        var second = result.Rows[1].Product;

        Assert.Equal("6001000000024", second.Barcode);
        Assert.Equal("White Bread", second.Name);
        Assert.Equal(6m, second.ReorderLevel);
        Assert.False(second.IsActive);
    }

    [Fact]
    public void A_name_containing_a_comma_a_quote_or_a_line_break_stays_one_field()
    {
        // The failure this guards against is silent and total: one stray comma shifts every column on
        // that line, so the price is read from the tax column and the shop sells at the wrong price.
        var original = new[]
        {
            Product(id: "p1", name: "Beans, \"baked\"\nin tomato sauce", barcode: "6001000000017"),
        };

        var text = CatalogCsv.Write(original);
        var result = CatalogCsv.Read(text, Store);

        Assert.Empty(result.Errors);

        var read = Assert.Single(result.Rows);

        Assert.Equal("Beans, \"baked\"\nin tomato sauce", read.Product.Name);
        Assert.Equal(original[0].UnitPrice, read.Product.UnitPrice);
        Assert.Equal(original[0].Barcode, read.Product.Barcode);
    }

    [Fact]
    public void Columns_are_matched_by_name_rather_than_by_position()
    {
        // A shop reorders its columns in Excel. An importer that read by position would take the price
        // column for the barcode, and every product would arrive with a barcode of "15.00".
        var csv =
            "price,name,barcode\n" +
            "15.00,Cola 500ml,6001000000017\n";

        var result = CatalogCsv.Read(csv, Store);

        var row = Assert.Single(result.Rows);

        Assert.Equal("Cola 500ml", row.Product.Name);
        Assert.Equal("6001000000017", row.Product.Barcode);
        Assert.Equal(15.00m, row.Product.UnitPrice);
    }

    [Fact]
    public void Columns_the_importer_does_not_know_are_ignored()
    {
        // A shop's own spreadsheet has its supplier's columns in it. Refusing the file over an extra
        // column would make the export useless as a template.
        var csv =
            "barcode,name,price,supplier,lastOrdered\n" +
            "6001000000017,Cola 500ml,15.00,Acme,2026-01-01\n";

        var result = CatalogCsv.Read(csv, Store);

        Assert.Empty(result.Errors);
        Assert.Single(result.Rows);
    }

    [Fact]
    public void A_file_with_no_header_is_refused_once_rather_than_row_by_row()
    {
        var result = CatalogCsv.Read("6001000000017,Cola 500ml,15.00\n", Store);

        Assert.Empty(result.Rows);

        var error = Assert.Single(result.Errors);

        Assert.Equal(1, error.LineNumber);
        Assert.Contains("barcode", error.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_bad_row_is_reported_with_its_line_and_the_rest_still_import()
    {
        // Four hundred products should not be held up by one bad line — but neither should the bad line
        // pass unnoticed, which is why every skip names where to look.
        var csv =
            "barcode,name,price\n" +
            "6001000000017,Cola 500ml,15.00\n" +
            "6001000000024,White Bread,not a price\n" +
            "6001000000031,Milk 2L,22.50\n";

        var result = CatalogCsv.Read(csv, Store);

        Assert.Equal(2, result.Rows.Count);

        var error = Assert.Single(result.Errors);

        Assert.Equal(3, error.LineNumber);
        Assert.Contains("not a price", error.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_barcode_or_name_is_reported_rather_than_imported()
    {
        var csv =
            "barcode,name,price\n" +
            ",Cola 500ml,15.00\n" +
            "6001000000024,,12.00\n";

        var result = CatalogCsv.Read(csv, Store);

        Assert.Empty(result.Rows);
        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public void A_tax_rate_written_as_a_percentage_is_read_as_one()
    {
        // A spreadsheet column headed "tax rate" is as likely to hold 15 as 0.15, and the difference is
        // a factor of a hundred on every receipt in the shop.
        var csv =
            "barcode,name,price,taxRate\n" +
            "6001000000017,Cola 500ml,15.00,15\n" +
            "6001000000024,Bread,12.00,0.15\n";

        var result = CatalogCsv.Read(csv, Store);

        Assert.Equal(0.15m, result.Rows[0].Product.TaxRate);
        Assert.Equal(0.15m, result.Rows[1].Product.TaxRate);
    }

    [Fact]
    public void Re_importing_the_export_updates_rather_than_duplicating()
    {
        // The whole point of an export is that it comes back. An importer that matched on nothing would
        // leave a shop with two of every article, both of which the till would happily sell.
        var existing = new[]
        {
            Product(id: "p1", barcode: "6001000000017", name: "Cola 500ml"),
            Product(id: "p2", barcode: "6001000000024", name: "White Bread"),
        };

        var byId = existing.ToDictionary(p => p.Id, StringComparer.Ordinal);

        var edited = CatalogCsv
            .Write(existing)
            .Replace("Cola 500ml", "Cola 500ml (new recipe)", StringComparison.Ordinal);

        var result = CatalogCsv.Read(edited, Store, byId);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Rows.Count);

        var cola = result.Rows.Single(r => r.Product.Barcode == "6001000000017").Product;

        Assert.Equal("p1", cola.Id);
        Assert.Equal("Cola 500ml (new recipe)", cola.Name);
    }

    [Fact]
    public void A_new_barcode_in_a_re_import_is_a_new_article()
    {
        var existing = new[] { Product(id: "p1", barcode: "6001000000017", name: "Cola 500ml") };

        var byId = existing.ToDictionary(p => p.Id, StringComparer.Ordinal);

        var csv =
            "barcode,name,price\n" +
            "6001000000017,Cola 500ml,15.00\n" +
            "6001000000999,Cola Zero,16.00\n";

        var result = CatalogCsv.Read(csv, Store, byId);

        Assert.Equal("p1", result.Rows[0].Product.Id);
        Assert.NotEqual("p1", result.Rows[1].Product.Id);
        Assert.Equal("6001000000999", result.Rows[1].Product.Barcode);
    }

    [Fact]
    public void Blank_optional_fields_are_read_as_absent_rather_than_empty_text()
    {
        // "" and null are different things in a product record: an empty category would group every
        // ungrouped article together under a blank heading, and an empty sku would match a search for it.
        var csv =
            "barcode,name,price,sku,category,reorderLevel,stationId\n" +
            "6001000000017,Cola 500ml,15.00,,,,,\n";

        var row = Assert.Single(CatalogCsv.Read(csv, Store).Rows).Product;

        Assert.Null(row.Sku);
        Assert.Null(row.Category);
        Assert.Null(row.ReorderLevel);
        Assert.Null(row.StationId);
    }

    [Fact]
    public void The_yes_and_no_columns_accept_what_a_spreadsheet_actually_contains()
    {
        var csv =
            "barcode,name,price,tracksStock,active\n" +
            "6001000000017,Cola 500ml,15.00,no,yes\n" +
            "6001000000024,Bread,12.00,TRUE,0\n" +
            "6001000000031,Milk,22.50,FALSE,1\n";

        var rows = CatalogCsv.Read(csv, Store).Rows;

        Assert.False(rows[0].Product.TracksStock);
        Assert.True(rows[0].Product.IsActive);
        Assert.True(rows[1].Product.TracksStock);
        Assert.False(rows[1].Product.IsActive);
        Assert.False(rows[2].Product.TracksStock);
        Assert.True(rows[2].Product.IsActive);
    }

    [Fact]
    public void A_byte_order_mark_on_the_first_header_does_not_hide_the_first_column()
    {
        // Excel writes one. Without stripping it the first header is "\uFEFFsku" and does not match,
        // which loses a column silently rather than failing.
        var csv = "\uFEFFbarcode,name,price\n6001000000017,Cola 500ml,15.00\n";

        var row = Assert.Single(CatalogCsv.Read(csv, Store).Rows);

        Assert.Equal("6001000000017", row.Product.Barcode);
    }

    [Fact]
    public void An_empty_file_produces_nothing_and_no_complaint()
    {
        // A shop that exports an empty catalogue and imports it back has done nothing wrong.
        Assert.Empty(CatalogCsv.Read(string.Empty, Store).Rows);
        Assert.Empty(CatalogCsv.Read("   \n", Store).Rows);
        Assert.Empty(CatalogCsv.Read(CatalogCsv.Write([]), Store).Rows);
        Assert.Empty(CatalogCsv.Read(CatalogCsv.Write([]), Store).Errors);
    }

    [Fact]
    public void Windows_line_endings_parse_the_same_as_unix_ones()
    {
        var csv = "barcode,name,price\r\n6001000000017,Cola 500ml,15.00\r\n6001000000024,Bread,12.00\r\n";

        var result = CatalogCsv.Read(csv, Store);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Bread", result.Rows[1].Product.Name);
    }

    [Fact]
    public void What_the_exporter_writes_is_what_a_spreadsheet_can_open()
    {
        var text = CatalogCsv.Write([Product(id: "p1", barcode: "6001000000017", name: "Cola 500ml")]);

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(string.Join(',', CatalogCsv.Columns), lines[0]);

        // Read back by name rather than by position, so this stays about the header being usable and
        // the row being complete rather than about the order the columns happen to be in.
        var result = CatalogCsv.Read(text, Store);
        var row = Assert.Single(result.Rows).Product;

        Assert.Equal("6001000000017", row.Barcode);
        Assert.Equal("Cola 500ml", row.Name);
        Assert.Equal(15.00m, row.UnitPrice);
        Assert.Equal(0.15m, row.TaxRate);
    }

    private static StoredProduct Product(
        string id,
        string barcode = "6001000000017",
        string name = "Cola 500ml",
        string? sku = null,
        string? category = null,
        decimal? reorderLevel = null,
        bool active = true,
        bool tracksStock = true) => new()
        {
            Id = id,
            StoreId = Store,
            Barcode = barcode,
            Name = name,
            UnitPrice = 15.00m,
            TaxName = "VAT",
            TaxRate = 0.15m,
            Sku = sku,
            Category = category,
            ReorderLevel = reorderLevel,
            IsActive = active,
            TracksStock = tracksStock,
        };
}
