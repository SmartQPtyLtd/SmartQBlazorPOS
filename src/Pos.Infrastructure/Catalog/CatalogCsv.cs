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
using Pos.Infrastructure.Storage;

namespace Pos.Infrastructure.Catalog;

/// <summary>
/// One catalogue row read from a CSV file.
/// </summary>
/// <param name="LineNumber">Line in the source file, so an error can name where to look.</param>
/// <param name="Product">The product the row describes.</param>
public readonly record struct CatalogImportRow(int LineNumber, StoredProduct Product);

/// <summary>A row that could not be imported, and why.</summary>
/// <param name="LineNumber">Line in the source file.</param>
/// <param name="Reason">What was wrong, in terms an operator can act on.</param>
public readonly record struct CatalogImportError(int LineNumber, string Reason);

/// <summary>The outcome of reading a catalogue file.</summary>
/// <param name="Rows">Rows that parsed.</param>
/// <param name="Errors">Rows that did not, each with a reason.</param>
public sealed record CatalogImportResult(
    IReadOnlyList<CatalogImportRow> Rows,
    IReadOnlyList<CatalogImportError> Errors)
{
    /// <summary>True when nothing at all could be read.</summary>
    public bool IsEmpty => Rows.Count == 0;
}

/// <summary>
/// Reads and writes the catalogue as CSV.
/// </summary>
/// <remarks>
/// <para>
/// The format a shop already has: a price list exported from a spreadsheet, or one somebody maintains
/// by hand. Deliberately the lowest common denominator rather than a richer format — the point is to
/// meet the shop's existing file where it is, and a file that Excel can open without a warning is worth
/// more here than one that can carry every field losslessly.
/// </para>
/// <para>
/// Written by hand rather than through a CSV library, because the rules are small and the two that
/// matter are worth being explicit about: a field containing a comma, a quote, or a line break is
/// wrapped in quotes, and a quote inside such a field is doubled. Getting those wrong is how a product
/// called <c>Beans, "baked"</c> silently becomes two columns and a corrupted price.
/// </para>
/// <para>
/// Reading matches columns <b>by header name</b> rather than by position, so a shop can reorder or
/// delete columns in Excel without the importer quietly reading prices as quantities.
/// </para>
/// </remarks>
public static class CatalogCsv
{
    /// <summary>Header written by the exporter, and the names the importer looks for.</summary>
    public static readonly string[] Columns =
    [
        "sku",
        "barcode",
        "name",
        "category",
        "price",
        "taxName",
        "taxRate",
        "tracksStock",
        "reorderLevel",
        "stationId",
        "active",
    ];

    /// <summary>Writes the catalogue as CSV, with a header row.</summary>
    /// <param name="products">Products to write.</param>
    public static string Write(IEnumerable<StoredProduct> products)
    {
        ArgumentNullException.ThrowIfNull(products);

        var builder = new StringBuilder();

        builder.Append(string.Join(',', Columns)).Append('\n');

        foreach (var product in products)
        {
            builder
                .Append(Field(product.Sku))
                .Append(',').Append(Field(product.Barcode))
                .Append(',').Append(Field(product.Name))
                .Append(',').Append(Field(product.Category))
                .Append(',').Append(Money(product.UnitPrice))
                .Append(',').Append(Field(product.TaxName))
                .Append(',').Append(Money(product.TaxRate))
                .Append(',').Append(product.TracksStock ? "yes" : "no")
                .Append(',').Append(product.ReorderLevel is { } level ? Money(level) : string.Empty)
                .Append(',').Append(Field(product.StationId))
                .Append(',').Append(product.IsActive ? "yes" : "no")
                .Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reads a catalogue from CSV.
    /// </summary>
    /// <param name="text">The file's contents.</param>
    /// <param name="storeId">Store the rows belong to. A file never carries this: it is the till's.</param>
    /// <param name="existing">
    /// Products already known, keyed by id, so a row that names one updates it rather than creating a
    /// duplicate article.
    /// </param>
    /// <remarks>
    /// A malformed row is reported and skipped rather than failing the file. A shop importing four
    /// hundred products should not have to find the one bad line before any of it lands — but neither
    /// should a bad line pass unnoticed, which is why every skip is named with its line number.
    /// </remarks>
    public static CatalogImportResult Read(
        string text,
        string storeId,
        IReadOnlyDictionary<string, StoredProduct>? existing = null)
    {
        ArgumentNullException.ThrowIfNull(storeId);

        var rows = new List<CatalogImportRow>();
        var errors = new List<CatalogImportError>();

        if (string.IsNullOrWhiteSpace(text))
        {
            return new CatalogImportResult(rows, errors);
        }

        var records = Parse(text);

        if (records.Count == 0)
        {
            return new CatalogImportResult(rows, errors);
        }

        var header = records[0].Fields
            .Select((name, index) => (Name: name.Trim().ToLowerInvariant(), Index: index))
            .GroupBy(c => c.Name.TrimStart('\uFEFF'))
            .ToDictionary(g => g.Key, g => g.First().Index, StringComparer.OrdinalIgnoreCase);

        if (!header.ContainsKey("barcode") || !header.ContainsKey("name") || !header.ContainsKey("price"))
        {
            errors.Add(new CatalogImportError(
                records[0].LineNumber,
                "The header must name at least barcode, name and price."));

            return new CatalogImportResult(rows, errors);
        }

        foreach (var record in records.Skip(1))
        {
            // A trailing newline is ordinary; a line of only commas is not.
            if (record.Fields.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            try
            {
                rows.Add(ReadRow(record, header, storeId, existing));
            }
            catch (FormatException ex)
            {
                errors.Add(new CatalogImportError(record.LineNumber, ex.Message));
            }
        }

        return new CatalogImportResult(rows, errors);
    }

    private static CatalogImportRow ReadRow(
        (int LineNumber, List<string> Fields) record,
        Dictionary<string, int> header,
        string storeId,
        IReadOnlyDictionary<string, StoredProduct>? existing)
    {
        string Get(string column) =>
            header.TryGetValue(column, out var index) && index < record.Fields.Count
                ? record.Fields[index].Trim()
                : string.Empty;

        var barcode = Get("barcode");
        var name = Get("name");
        var priceText = Get("price");

        if (barcode.Length == 0)
        {
            throw new FormatException("The barcode is empty.");
        }

        if (name.Length == 0)
        {
            throw new FormatException("The name is empty.");
        }

        if (!TryMoney(priceText, out var price))
        {
            throw new FormatException($"'{priceText}' is not a price.");
        }

        if (price < 0m)
        {
            throw new FormatException("A price cannot be negative.");
        }

        decimal? reorderLevel = null;
        var reorderText = Get("reorderLevel");

        if (reorderText.Length > 0)
        {
            if (!TryMoney(reorderText, out var level))
            {
                throw new FormatException($"'{reorderText}' is not a reorder level.");
            }

            if (level < 0m)
            {
                throw new FormatException("A reorder level cannot be negative.");
            }

            reorderLevel = level;
        }

        var taxRate = 0m;
        var taxRateText = Get("taxRate");

        if (taxRateText.Length > 0 && !TryMoney(taxRateText, out taxRate))
        {
            // Accepts both "0.15" and "15", because a spreadsheet column headed tax rate is as likely
            // to hold percent as a fraction and the difference is a factor of a hundred on every
            // receipt in the shop.
            throw new FormatException($"'{taxRateText}' is not a tax rate.");
        }

        if (taxRate > 1m)
        {
            taxRate /= 100m;
        }

        var id = existing is not null
            ? FindId(existing, barcode)
            : Guid.CreateVersion7().ToString("N");

        var product = existing is not null && existing.TryGetValue(id, out var match)
            ? match with
            {
                Barcode = barcode,
                Name = name,
                Category = Blank(Get("category")),
                UnitPrice = price,
                TaxName = Blank(Get("taxName")) ?? match.TaxName,
                TaxRate = taxRate,
                TracksStock = Yes(Get("tracksStock")),
                ReorderLevel = reorderLevel,
                Sku = Blank(Get("sku")),
                StationId = Blank(Get("stationId")),
                IsActive = Yes(Get("active")),
            }
            : new StoredProduct
            {
                Id = id,
                StoreId = storeId,
                Barcode = barcode,
                Name = name,
                Category = Blank(Get("category")),
                UnitPrice = price,
                TaxName = Blank(Get("taxName")) ?? "Tax",
                TaxRate = taxRate,
                TracksStock = Yes(Get("tracksStock")),
                ReorderLevel = reorderLevel,
                Sku = Blank(Get("sku")),
                StationId = Blank(Get("stationId")),
                IsActive = Yes(Get("active")),
            };

        return new CatalogImportRow(record.LineNumber, product);
    }

    /// <summary>
    /// Finds the id of an existing product by barcode, so an import updates rather than duplicates.
    /// </summary>
    /// <remarks>
    /// Matched on barcode rather than on the id column, because the id is not something a shop writes
    /// in a spreadsheet. It is the barcode that identifies an article to the person maintaining the
    /// file, and it is what the till scans.
    /// </remarks>
    private static string FindId(IReadOnlyDictionary<string, StoredProduct> existing, string barcode) =>
        existing.Values
            .FirstOrDefault(p => string.Equals(p.Barcode, barcode, StringComparison.OrdinalIgnoreCase))
            ?.Id
        ?? Guid.CreateVersion7().ToString("N");

    /// <summary>True for the ways a spreadsheet writes yes.</summary>
    private static bool Yes(string text) =>
        text.Length == 0
        || text.Equals("yes", StringComparison.OrdinalIgnoreCase)
        || text.Equals("true", StringComparison.OrdinalIgnoreCase)
        || text.Equals("y", StringComparison.OrdinalIgnoreCase)
        || text.Equals("1", StringComparison.Ordinal)
        || text.Equals("x", StringComparison.OrdinalIgnoreCase);

    private static string? Blank(string text) => text.Length == 0 ? null : text;

    private static string Money(decimal value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    private static bool TryMoney(string text, out decimal value) =>
        decimal.TryParse(
            text.Replace(" ", string.Empty, StringComparison.Ordinal),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out value);

    /// <summary>Quotes a field when it has to be, doubling any quotes inside it.</summary>
    private static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var needsQuotes = value.Contains(',', StringComparison.Ordinal)
            || value.Contains('"', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal)
            || value.Contains('\r', StringComparison.Ordinal);

        return needsQuotes
            ? '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"'
            : value;
    }

    /// <summary>
    /// Splits CSV into records, honouring quoted fields.
    /// </summary>
    /// <remarks>
    /// A character walk rather than a split on comma, because a quoted field may contain the delimiter
    /// and a line break — which is exactly the case a naive implementation gets wrong, and gets wrong
    /// silently, by shifting every later column on that line.
    /// </remarks>
    private static List<(int LineNumber, List<string> Fields)> Parse(string text)
    {
        var records = new List<(int LineNumber, List<string> Fields)>();
        var fields = new List<string>();
        var field = new StringBuilder();

        var line = 1;
        var lineOfRecord = 1;
        var inQuotes = false;
        var hasContent = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // A doubled quote inside a quoted field is a literal quote.
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    hasContent = true;
                    break;

                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    hasContent = true;
                    break;

                case '\r':
                    // Ignored: the line break is the \n that follows, so a file written on Windows
                    // parses the same as one written anywhere else.
                    break;

                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();

                    if (hasContent || fields.Count > 1)
                    {
                        records.Add((lineOfRecord, fields));
                    }

                    fields = [];
                    hasContent = false;
                    line++;
                    lineOfRecord = line;
                    break;

                default:
                    field.Append(c);
                    hasContent = true;
                    break;
            }
        }

        if (hasContent || field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add((lineOfRecord, fields));
        }

        return records;
    }
}
