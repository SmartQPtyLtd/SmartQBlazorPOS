// SmartQ Blazor POS
// Copyright (C) 2026 SmartQ (Pty) Ltd
// SPDX-License-Identifier: AGPL-3.0-only
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, version 3. Commercial licensing is available from
// SmartQ (Pty) Ltd — see LICENSING.md.

using Pos.Core.Domain;

namespace Pos.Core.Tests;

/// <summary>
/// The cart is where a POS quietly loses money: a rounding remainder that vanishes,
/// a discount that overshoots its line into a negative total, or a receipt whose
/// lines do not add up to its own total. Each of those is covered here.
/// </summary>
public sealed class CartTests
{
    private const string Zar = "ZAR";
    private static readonly TaxRate Vat15 = new("VAT", 0.15m);
    private static readonly TaxRate ZeroRated = TaxRate.Zero("VAT-Zero");

    private static Cart NewCart(TaxMode mode = TaxMode.Inclusive) =>
        new(StoreId.New(), Zar, mode);

    private static Product Product(string name, decimal price, TaxRate? rate = null) => new()
    {
        Id = ProductId.New(),
        StoreId = StoreId.New(),
        Barcode = "1234567890123",
        Name = name,
        UnitPrice = new Money(price, Zar),
        TaxRate = rate ?? Vat15,
    };

    [Fact]
    public void An_empty_cart_totals_zero()
    {
        var totals = NewCart().CalculateTotals();

        Assert.Equal(0m, totals.Total);
        Assert.Equal(0m, totals.NetBeforeOrderDiscount);
        Assert.Empty(totals.LineTotals);
    }

    [Fact]
    public void Scanning_the_same_product_twice_merges_into_one_line()
    {
        var cart = NewCart();
        var product = Product("Cola", 15.00m);

        cart.AddProduct(product);
        cart.AddProduct(product);

        Assert.Single(cart.Lines);
        Assert.Equal(2m, cart.Lines[0].Quantity);
    }

    [Fact]
    public void The_same_product_at_a_different_price_becomes_a_separate_line()
    {
        // Merging these would misstate what was actually charged and make the sale
        // impossible to reconcile against the shelf price at the time.
        var cart = NewCart();
        var product = Product("Cola", 15.00m);

        cart.AddProduct(product);
        cart.Add(product.Id, product.Barcode, product.Name, product.TaxRate, new Money(18.00m, Zar));

        Assert.Equal(2, cart.Lines.Count);
    }

    [Fact]
    public void A_discounted_line_is_not_merged_into_an_undiscounted_one()
    {
        var cart = NewCart();
        var product = Product("Cola", 15.00m);

        cart.AddProduct(product);
        cart.Lines[0].LineDiscount = new Discount(DiscountKind.Percentage, 0.10m);
        cart.AddProduct(product);

        Assert.Equal(2, cart.Lines.Count);
    }

    [Fact]
    public void Quantity_must_be_positive_and_voiding_is_the_only_way_to_remove()
    {
        var cart = NewCart();
        var line = cart.AddProduct(Product("Cola", 15.00m));

        Assert.Throws<ArgumentOutOfRangeException>(() => line.Quantity = 0m);
        Assert.Throws<ArgumentOutOfRangeException>(() => line.Quantity = -1m);

        cart.Remove(line);
        Assert.True(cart.IsEmpty);
    }

    [Fact]
    public void Weighted_goods_are_supported_with_fractional_quantities()
    {
        // 0.734 kg at 129.99/kg = 95.41266, which must round to 95.41.
        var cart = NewCart();
        var product = Product("Bananas", 129.99m);

        cart.AddProduct(product, 0.734m);

        var totals = cart.CalculateTotals();
        Assert.Equal(95.41m, totals.NetBeforeOrderDiscount);
        Assert.Equal(95.41m, totals.Total);
    }

    [Fact]
    public void A_line_discount_reduces_the_taxable_amount()
    {
        var cart = NewCart();
        var line = cart.AddProduct(Product("Cola", 100.00m));
        line.LineDiscount = new Discount(DiscountKind.Percentage, 0.10m);

        var totals = cart.CalculateTotals();

        // GrossBeforeDiscounts is the pre-discount shelf total; NetBeforeOrderDiscount
        // has the 10% line discount already taken off. Both are 100/90 respectively
        // here because there is no order-level discount in play.
        Assert.Equal(100.00m, totals.GrossBeforeDiscounts);
        Assert.Equal(90.00m, totals.NetBeforeOrderDiscount);
        Assert.Equal(90.00m, totals.LineTotals[0].TaxableAmount);
        Assert.Equal(90.00m, totals.Total);
        Assert.Equal(10.00m, totals.TotalDiscounts);
    }

    [Fact]
    public void A_fixed_line_discount_cannot_exceed_its_line()
    {
        // Without clamping this would produce a negative line, i.e. an accidental
        // refund hidden inside a sale.
        var cart = NewCart();
        var line = cart.AddProduct(Product("Cola", 10.00m));
        line.LineDiscount = new Discount(DiscountKind.FixedAmount, 999.00m);

        var totals = cart.CalculateTotals();

        Assert.Equal(0m, totals.LineTotals[0].TaxableAmount);
        Assert.Equal(0m, totals.Total);
    }

    [Fact]
    public void An_order_discount_is_spread_proportionally_across_lines()
    {
        // 30 discount over lines of 20 (x2) and 60. Proportional shares are
        // 30 * (40/100) = 12 and 30 * (60/100) = 18.
        var cart = NewCart();
        cart.AddProduct(Product("Widget", 20.00m), 2m);
        cart.AddProduct(Product("Gadget", 60.00m));
        cart.OrderDiscount = new Discount(DiscountKind.FixedAmount, 30.00m);

        var totals = cart.CalculateTotals();

        Assert.Equal(100.00m, totals.NetBeforeOrderDiscount);
        Assert.Equal(30.00m, totals.OrderDiscount);
        Assert.Equal(70.00m, totals.Total);
        Assert.Equal(12.00m, totals.LineTotals[0].OrderDiscountAllocated);
        Assert.Equal(18.00m, totals.LineTotals[1].OrderDiscountAllocated);
    }

    [Fact]
    public void Discount_allocation_never_loses_or_invents_a_cent()
    {
        // Three equal lines with a discount that cannot divide evenly. Naive
        // per-line rounding loses a cent here, and the receipt stops adding up.
        var cart = NewCart();
        cart.AddProduct(Product("A", 10.00m));
        cart.AddProduct(Product("B", 10.00m));
        cart.AddProduct(Product("C", 10.00m));
        cart.OrderDiscount = new Discount(DiscountKind.FixedAmount, 10.00m);

        var totals = cart.CalculateTotals();

        Assert.Equal(10.00m, totals.LineTotals.Sum(lt => lt.OrderDiscountAllocated));
        Assert.Equal(20.00m, totals.Total);
    }

    [Fact]
    public void An_order_discount_larger_than_the_basket_is_clamped_to_the_subtotal()
    {
        var cart = NewCart();
        cart.AddProduct(Product("Widget", 25.00m));
        cart.OrderDiscount = new Discount(DiscountKind.FixedAmount, 500.00m);

        var totals = cart.CalculateTotals();

        Assert.Equal(25.00m, totals.OrderDiscount);
        Assert.Equal(0m, totals.Total);
        Assert.All(totals.LineTotals, lt => Assert.Equal(0m, lt.TaxableAmount));
    }

    [Fact]
    public void Per_line_tax_always_sums_exactly_to_the_basket_tax()
    {
        // This is the invariant that makes a receipt internally consistent. If it
        // breaks, the printed lines will not reconcile to the printed tax total and
        // the sale cannot be audited.
        var cart = NewCart();
        cart.AddProduct(Product("A", 19.99m));
        cart.AddProduct(Product("B", 0.01m));
        cart.AddProduct(Product("C", 123.45m), 3m);
        cart.AddProduct(Product("Zero", 50.00m, ZeroRated));
        cart.OrderDiscount = new Discount(DiscountKind.Percentage, 0.07m);

        var totals = cart.CalculateTotals();

        Assert.Equal(totals.Tax.Tax, totals.LineTotals.Sum(lt => lt.LineTax));
    }

    [Fact]
    public void Inclusive_totals_charge_exactly_the_sum_of_the_shelf_prices()
    {
        var cart = NewCart(TaxMode.Inclusive);
        cart.AddProduct(Product("A", 19.99m));
        cart.AddProduct(Product("B", 4.50m), 3m);
        cart.AddProduct(Product("Zero", 12.00m, ZeroRated));

        var totals = cart.CalculateTotals();

        // 19.99 + 13.50 + 12.00 = 45.49, which is what the customer pays.
        Assert.Equal(45.49m, totals.Total);
        Assert.Equal(45.49m, totals.Tax.Net + totals.Tax.Tax);
    }

    [Fact]
    public void Exclusive_totals_add_tax_on_top_of_the_shelf_prices()
    {
        var cart = NewCart(TaxMode.Exclusive);
        cart.AddProduct(Product("A", 100.00m));
        cart.AddProduct(Product("Zero", 50.00m, ZeroRated));

        var totals = cart.CalculateTotals();

        Assert.Equal(150.00m, totals.NetBeforeOrderDiscount);
        Assert.Equal(15.00m, totals.Tax.Tax);
        Assert.Equal(165.00m, totals.Total);
    }

    [Fact]
    public void Mixing_currencies_in_one_cart_is_rejected()
    {
        var cart = NewCart();

        Assert.Throws<InvalidOperationException>(() =>
            cart.Add(ProductId.New(), "123", "Imported", Vat15, new Money(10m, "USD")));
    }

    [Fact]
    public void Clearing_the_cart_also_clears_the_order_discount()
    {
        // A discount surviving an emptied cart would silently apply to the next
        // customer's basket.
        var cart = NewCart();
        cart.AddProduct(Product("Widget", 25.00m));
        cart.OrderDiscount = new Discount(DiscountKind.Percentage, 0.10m);

        cart.Clear();

        Assert.True(cart.IsEmpty);
        Assert.Null(cart.OrderDiscount);
        Assert.Equal(0m, cart.CalculateTotals().Total);
    }

    [Fact]
    public void Totals_recompute_after_every_edit_without_staleness()
    {
        var cart = NewCart();
        var line = cart.AddProduct(Product("Widget", 10.00m));
        Assert.Equal(10.00m, cart.CalculateTotals().Total);

        line.Quantity = 5m;
        Assert.Equal(50.00m, cart.CalculateTotals().Total);

        cart.Remove(line);
        Assert.Equal(0m, cart.CalculateTotals().Total);
    }
}
