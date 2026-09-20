// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;
using Pos.Web.Terminal;

namespace Pos.Web.Tests;

/// <summary>
/// Customer display projection tests.
/// </summary>
/// <remarks>
/// The projection is pure, so it can be tested without a browser channel. What matters is
/// that what the customer sees matches what the till will charge — a display showing one
/// total while the till charges another is worse than having no display at all.
/// </remarks>
public sealed class CustomerDisplayStateTests
{
    private const string Zar = "ZAR";
    private static readonly TaxRate Vat15 = new("VAT", 0.15m);

    private static Cart NewCart() => new(StoreId.New(), Zar, TaxMode.Inclusive);

    private static Product Product(string name, decimal price) => new()
    {
        Id = ProductId.New(),
        StoreId = StoreId.New(),
        Barcode = "6001000000017",
        Name = name,
        UnitPrice = new Money(price, Zar),
        TaxRate = Vat15,
    };

    [Fact]
    public void An_empty_cart_projects_to_the_idle_message()
    {
        var cart = NewCart();

        var state = CustomerDisplayPublisher.FromCart(cart, cart.CalculateTotals());

        Assert.Empty(state.Lines);
        Assert.Equal(0m, state.Total);
        Assert.Equal(0, state.ItemCount);
        Assert.False(state.IsComplete);
    }

    [Fact]
    public void The_displayed_total_matches_what_the_till_charges()
    {
        // The invariant that matters most: the customer must never be shown a different
        // number from the one they are asked to pay.
        var cart = NewCart();
        cart.AddProduct(Product("Cola", 19.99m));
        cart.AddProduct(Product("Bread", 18.99m));

        var totals = cart.CalculateTotals();
        var state = CustomerDisplayPublisher.FromCart(cart, totals);

        Assert.Equal(totals.Total, state.Total);
    }

    [Fact]
    public void The_displayed_lines_sum_to_the_displayed_total()
    {
        // A customer who adds up the lines and gets a different answer to the total has
        // reasonable grounds to distrust the till.
        var cart = NewCart();
        cart.AddProduct(Product("Cola", 19.99m), 3m);
        cart.AddProduct(Product("Bread", 18.99m));
        cart.AddProduct(Product("Milk", 32.99m), 2m);

        var totals = cart.CalculateTotals();
        var state = CustomerDisplayPublisher.FromCart(cart, totals);

        Assert.Equal(totals.Total, state.Lines.Sum(l => l.LineTotal));
    }

    [Fact]
    public void Whole_quantities_read_without_a_decimal_point()
    {
        // "2 x Cola" reads properly; "2.0 x Cola" looks like a fault.
        var cart = NewCart();
        cart.AddProduct(Product("Cola", 15.00m), 2m);

        var state = CustomerDisplayPublisher.FromCart(cart, cart.CalculateTotals());

        Assert.Equal("2", state.Lines[0].Quantity);
    }

    [Fact]
    public void Weighted_goods_keep_their_fractional_quantity()
    {
        // A customer buying 0.734 kg must be able to see that figure.
        var cart = NewCart();
        cart.AddProduct(Product("Bananas", 22.99m), 0.734m);

        var state = CustomerDisplayPublisher.FromCart(cart, cart.CalculateTotals());

        Assert.Equal("0.734", state.Lines[0].Quantity);
    }

    [Fact]
    public void Each_scanned_line_appears_as_a_separate_row()
    {
        var cart = NewCart();
        cart.AddProduct(Product("Cola", 15.00m));
        cart.AddProduct(Product("Bread", 18.99m));
        cart.AddProduct(Product("Milk", 32.99m));

        var state = CustomerDisplayPublisher.FromCart(cart, cart.CalculateTotals());

        Assert.Equal(3, state.ItemCount);
        Assert.Equal(["Cola", "Bread", "Milk"], state.Lines.Select(l => l.Name));
    }

    [Fact]
    public void The_scan_message_names_the_item_just_added()
    {
        // Confirms to the customer that the scan registered, which is the main anxiety a
        // customer-facing display relieves.
        var cart = NewCart();
        cart.AddProduct(Product("Cola", 15.00m));

        var state = CustomerDisplayPublisher.FromCart(cart, cart.CalculateTotals(), "Added Cola");

        Assert.Equal("Added Cola", state.Message);
    }

    [Fact]
    public void The_state_carries_a_timestamp_so_staleness_can_be_detected()
    {
        // A display that has silently stopped updating looks identical to one showing an
        // empty basket, so the timestamp is what makes the difference visible.
        var cart = NewCart();
        cart.AddProduct(Product("Cola", 15.00m));

        var state = CustomerDisplayPublisher.FromCart(cart, cart.CalculateTotals());

        Assert.NotNull(state.UpdatedAt);
        Assert.True(DateTimeOffset.TryParse(state.UpdatedAt, out _));
    }

    [Fact]
    public void A_discount_is_reflected_in_what_the_customer_sees()
    {
        var cart = NewCart();
        var line = cart.AddProduct(Product("Cola", 100.00m));
        line.LineDiscount = new Discount(DiscountKind.Percentage, 0.10m);

        var totals = cart.CalculateTotals();
        var state = CustomerDisplayPublisher.FromCart(cart, totals);

        Assert.Equal(90.00m, state.Total);
        Assert.Equal(totals.Total, state.Lines.Sum(l => l.LineTotal));
    }

    [Fact]
    public void The_thank_you_state_shows_the_amount_paid_and_no_basket()
    {
        // Leaving the basket on screen after payment invites a dispute about what was
        // actually charged.
        var state = CustomerDisplayState.ThankYou(115.00m);

        Assert.True(state.IsComplete);
        Assert.Equal(115.00m, state.Total);
        Assert.Empty(state.Lines);
        Assert.Equal("Thank you!", state.Message);
    }

    [Fact]
    public void The_idle_state_greets_the_customer()
    {
        Assert.Equal("Welcome", CustomerDisplayState.Idle.Message);
        Assert.Empty(CustomerDisplayState.Idle.Lines);
    }

    [Fact]
    public void An_exclusive_tax_store_shows_the_total_including_tax()
    {
        // In exclusive mode the shelf price is not what the customer pays, so the display
        // must show the tax-added figure rather than the subtotal.
        var cart = new Cart(StoreId.New(), Zar, TaxMode.Exclusive);
        cart.AddProduct(Product("Widget", 100.00m));

        var totals = cart.CalculateTotals();
        var state = CustomerDisplayPublisher.FromCart(cart, totals);

        Assert.Equal(115.00m, state.Total);
    }

    [Fact]
    public void Projecting_a_null_cart_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            CustomerDisplayPublisher.FromCart(null!, CartTotals.Empty(Zar, TaxMode.Inclusive)));
    }
}
