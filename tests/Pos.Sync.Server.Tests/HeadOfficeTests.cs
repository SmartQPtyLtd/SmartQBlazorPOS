// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Reporting;
using Pos.Sync.Server.Auth;
using Pos.Sync.Server.Endpoints;

namespace Pos.Sync.Server.Tests;

/// <summary>
/// Tests for the head-office credential.
/// </summary>
/// <remarks>
/// The whole device-credential design rests on this. An enrolment code is the bootstrap for a
/// device credential, so a hub that hands one out to any caller has no trust boundary at all — a
/// terminal in one shop could enrol devices and write into the estate indefinitely. Failing closed
/// is the property that makes the rest of it mean anything.
/// </remarks>
public sealed class HeadOfficeCredentialTests
{
    private const string GoodToken = "a-sufficiently-long-head-office-token-01";

    [Fact]
    public void A_configured_token_verifies()
    {
        var credential = new HeadOfficeCredential(GoodToken);

        Assert.True(credential.IsConfigured);
        Assert.True(credential.Verify(GoodToken));
    }

    [Fact]
    public void A_wrong_token_does_not_verify()
    {
        var credential = new HeadOfficeCredential(GoodToken);

        Assert.False(credential.Verify("a-sufficiently-long-head-office-token-02"));
        Assert.False(credential.Verify(""));
        Assert.False(credential.Verify(null));
        Assert.False(credential.Verify(GoodToken + "x"));
        Assert.False(credential.Verify(GoodToken[..^1]));
    }

    [Fact]
    public void An_unconfigured_hub_refuses_every_token()
    {
        // Failing open would be the worst outcome: a hub that looks configured, runs happily, and
        // hands out device credentials to anyone who asks.
        foreach (var token in new[] { null, "", "   " })
        {
            var credential = new HeadOfficeCredential(token);

            Assert.False(credential.IsConfigured);
            Assert.False(credential.Verify(GoodToken));
        }
    }

    [Fact]
    public void A_short_token_is_treated_as_no_token_at_all()
    {
        // Accepting a short token is worse than having none: the operator believes the estate is
        // closed while an eight-character secret is guessable in seconds.
        var credential = new HeadOfficeCredential("short");

        Assert.False(credential.IsConfigured);
        Assert.False(credential.Verify("short"));
    }

    [Fact]
    public void The_minimum_length_is_a_real_floor()
    {
        var exactly = new string('x', HeadOfficeCredential.MinimumLength);
        var oneShort = new string('x', HeadOfficeCredential.MinimumLength - 1);

        Assert.True(new HeadOfficeCredential(exactly).IsConfigured);
        Assert.False(new HeadOfficeCredential(oneShort).IsConfigured);
    }

    [Fact]
    public void A_generated_token_clears_the_minimum_and_is_unique()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => HeadOfficeCredential.NewToken()).ToArray();

        Assert.All(tokens, t => Assert.True(t.Length >= HeadOfficeCredential.MinimumLength));
        Assert.All(tokens, t => Assert.True(new HeadOfficeCredential(t).IsConfigured));
        Assert.Equal(tokens.Length, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_generated_token_survives_a_url_so_it_can_be_pasted_into_configuration()
    {
        // The token is copied between an operator's password manager and an appsettings file, and
        // characters needing escaping are a reliable source of "it worked in the terminal" bugs.
        for (var i = 0; i < 50; i++)
        {
            var token = HeadOfficeCredential.NewToken();

            Assert.DoesNotContain('+', token);
            Assert.DoesNotContain('/', token);
            Assert.DoesNotContain('=', token);
        }
    }

    [Fact]
    public void The_refusal_for_an_unconfigured_hub_says_so_rather_than_denying_access()
    {
        // Unlike device enrolment, where the refusal is deliberately vague so codes cannot be
        // probed, here the caller is an operator standing up a hub. "Not authorised" would send
        // them hunting for a typo in a token that was never configured.
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var refusal = HeadOfficeAuthentication.Authorise(context, new HeadOfficeCredential(null));

        Assert.NotNull(refusal);

        var result = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(refusal);
        Assert.Equal(503, result.StatusCode);
    }

    [Fact]
    public void A_wrong_token_is_refused_with_401()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Headers[HeadOfficeAuthentication.HeaderName] = "the-wrong-token-entirely-here";

        var refusal = HeadOfficeAuthentication.Authorise(context, new HeadOfficeCredential(GoodToken));

        Assert.NotNull(refusal);

        var result = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(refusal);
        Assert.Equal(401, result.StatusCode);
    }

    [Fact]
    public void A_correct_token_is_allowed_through()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Headers[HeadOfficeAuthentication.HeaderName] = GoodToken;

        Assert.Null(HeadOfficeAuthentication.Authorise(context, new HeadOfficeCredential(GoodToken)));
    }
}

/// <summary>
/// Tests for reading synced payloads into trading facts.
/// </summary>
/// <remarks>
/// The hub stores records it does not own the schema of, so this reader is the seam between "we
/// accepted bytes from a terminal" and "we can report on them". A record it cannot read must never
/// throw: it is already stored and already syncing, and failing the report would turn one odd
/// payload into a report nobody can produce.
/// </remarks>
public sealed class TradingFactReaderTests
{
    private const string StoreId = "store-a";

    private static string SaleJson(
        decimal total = 115.00m,
        decimal tax = 15.00m,
        string status = "Completed",
        string businessDate = "2026-03-25") =>
        $$"""
        {
          "id": "01a0ac06000070008000000000000001",
          "storeId": "{{StoreId}}",
          "businessDate": "{{businessDate}}",
          "status": "{{status}}",
          "total": {{total}},
          "taxTotal": {{tax}}
        }
        """;

    private static string ReturnJson(
        decimal totalRefund = 50.00m,
        decimal taxReversed = 6.52m,
        string businessDate = "2026-03-25") =>
        $$"""
        {
          "id": "01a0ac06000070008000000000000002",
          "storeId": "{{StoreId}}",
          "businessDate": "{{businessDate}}",
          "totalRefund": {{totalRefund}},
          "taxReversed": {{taxReversed}}
        }
        """;

    [Fact]
    public void A_sale_is_read_as_a_sale()
    {
        var fact = TradingFactReader.Read(TradingFactReader.SaleEntityType, StoreId, SaleJson());

        Assert.NotNull(fact);
        Assert.Equal(TradingFactKind.Sale, fact.Value.Kind);
        Assert.Equal(115.00m, fact.Value.Total);
        Assert.Equal(15.00m, fact.Value.Tax);
        Assert.Equal(new DateOnly(2026, 3, 25), fact.Value.BusinessDate);
        Assert.False(fact.Value.WasVoided);
    }

    [Fact]
    public void A_voided_sale_is_marked_as_voided()
    {
        var fact = TradingFactReader.Read(
            TradingFactReader.SaleEntityType, StoreId, SaleJson(status: "Voided"));

        Assert.NotNull(fact);
        Assert.True(fact.Value.WasVoided);
    }

    [Fact]
    public void A_refund_is_read_as_a_refund_from_its_own_fields()
    {
        // A refund carries totalRefund and taxReversed, not total and taxTotal. Reading the wrong
        // pair would silently report every refund as zero.
        var fact = TradingFactReader.Read(TradingFactReader.ReturnEntityType, StoreId, ReturnJson());

        Assert.NotNull(fact);
        Assert.Equal(TradingFactKind.Refund, fact.Value.Kind);
        Assert.Equal(50.00m, fact.Value.Total);
        Assert.Equal(6.52m, fact.Value.Tax);
    }

    [Fact]
    public void The_store_comes_from_the_hub_row_not_the_payload()
    {
        // The payload's own storeId is ignored. A terminal must not be able to attribute its
        // takings to another shop by editing a sale body.
        var fact = TradingFactReader.Read(TradingFactReader.SaleEntityType, "store-b", SaleJson());

        Assert.NotNull(fact);
        Assert.Equal("store-b", fact.Value.StoreId);
    }

    [Fact]
    public void Amounts_arriving_as_strings_are_still_read()
    {
        // The same tolerance the indexer applies, so the two cannot disagree about what a record
        // is worth.
        var json = """{ "businessDate": "2026-03-25", "total": "115.00", "taxTotal": "15.00" }""";

        var fact = TradingFactReader.Read(TradingFactReader.SaleEntityType, StoreId, json);

        Assert.NotNull(fact);
        Assert.Equal(115.00m, fact.Value.Total);
        Assert.Equal(15.00m, fact.Value.Tax);
    }

    [Fact]
    public void A_missing_tax_figure_reads_as_zero_rather_than_failing()
    {
        var json = """{ "businessDate": "2026-03-25", "total": 115.00 }""";

        var fact = TradingFactReader.Read(TradingFactReader.SaleEntityType, StoreId, json);

        Assert.NotNull(fact);
        Assert.Equal(0m, fact.Value.Tax);
    }

    [Fact]
    public void A_record_with_no_business_date_is_skipped_rather_than_guessed_at()
    {
        // A sale taken at 23:58 and pushed at 00:03 belongs to the earlier day. Attributing it to
        // its arrival time would move takings between two stores' Z-reports.
        var json = """{ "total": 115.00, "taxTotal": 15.00 }""";

        Assert.Null(TradingFactReader.Read(TradingFactReader.SaleEntityType, StoreId, json));
    }

    [Fact]
    public void A_malformed_payload_returns_nothing_rather_than_throwing()
    {
        // It is already stored and already syncing. Failing the report would turn one odd payload
        // into a report nobody can produce.
        Assert.Null(TradingFactReader.Read(TradingFactReader.SaleEntityType, StoreId, "{ not valid"));
        Assert.Null(TradingFactReader.Read(TradingFactReader.SaleEntityType, StoreId, "[]"));
        Assert.Null(TradingFactReader.Read(TradingFactReader.SaleEntityType, StoreId, null));
        Assert.Null(TradingFactReader.Read(TradingFactReader.SaleEntityType, StoreId, ""));
    }

    [Fact]
    public void A_record_type_that_is_not_money_is_not_a_trading_fact()
    {
        // Shifts, drawer events, and stock movements all travel in the same stream, and none of
        // them is money taken. Counting one would inflate the estate's takings.
        foreach (var type in new[] { "shift", "drawerEvent", "stockMovement", "product", null })
        {
            Assert.False(TradingFactReader.IsTradingFact(type));
            Assert.Null(TradingFactReader.Read(type, StoreId, SaleJson()));
        }
    }

    [Fact]
    public void The_trading_types_are_exactly_sales_and_refunds()
    {
        Assert.True(TradingFactReader.IsTradingFact("sale"));
        Assert.True(TradingFactReader.IsTradingFact("salesReturn"));
    }
}

/// <summary>
/// Tests for the columns the hub indexes out of a payload.
/// </summary>
/// <remarks>
/// The index is what bounds a head-office report: without a business date on a record, the report
/// cannot place it in a period and skips it. A record type that is indexed wrongly is therefore
/// invisible in a way that looks like "nothing happened" rather than like a fault — which is how
/// every refund in the estate came to be missing from the estate total.
/// </remarks>
public sealed class SyncPayloadIndexerTests
{
    [Fact]
    public void A_sale_is_indexed_from_its_own_fields()
    {
        var (date, total) = SyncPayloadIndexer.Extract(
            SyncPayloadIndexer.SaleEntityType,
            """{ "businessDate": "2026-03-25", "total": 115.00, "taxTotal": 15.00 }""");

        Assert.Equal("2026-03-25", date);
        Assert.Equal(115.00m, total);
    }

    [Fact]
    public void A_refund_is_indexed_from_its_own_fields()
    {
        // A refund carries totalRefund, not total. Reading `total` would index zero and drop every
        // refund out of the estate's figures while the report still looked complete.
        var (date, total) = SyncPayloadIndexer.Extract(
            SyncPayloadIndexer.ReturnEntityType,
            """{ "businessDate": "2026-03-25", "totalRefund": 50.00, "taxReversed": 6.52 }""");

        Assert.Equal("2026-03-25", date);
        Assert.Equal(50.00m, total);
    }

    [Fact]
    public void A_refund_does_not_borrow_a_sale_field()
    {
        // A payload carrying both must be read as what it is. If a refund ever gained a `total`
        // field, reading that instead would report the original sale's value as money given back.
        var (_, total) = SyncPayloadIndexer.Extract(
            SyncPayloadIndexer.ReturnEntityType,
            """{ "businessDate": "2026-03-25", "total": 500.00, "totalRefund": 50.00 }""");

        Assert.Equal(50.00m, total);
    }

    [Fact]
    public void A_record_type_that_is_not_money_is_not_indexed()
    {
        foreach (var type in new[] { "shift", "drawerEvent", "stockMovement", "product" })
        {
            var (date, total) = SyncPayloadIndexer.Extract(
                type, """{ "businessDate": "2026-03-25", "total": 115.00 }""");

            Assert.Null(date);
            Assert.Null(total);
        }
    }

    [Fact]
    public void An_unreadable_payload_indexes_nothing_rather_than_failing()
    {
        var (date, total) = SyncPayloadIndexer.Extract(SyncPayloadIndexer.SaleEntityType, "{ not valid");

        Assert.Null(date);
        Assert.Null(total);
    }
}
