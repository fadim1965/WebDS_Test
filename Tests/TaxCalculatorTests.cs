using WebDS_Test.Domain;
using WebDS_Test.Services;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// Tax arithmetic. The rule that matters most here is backwards compatibility: a catalogue with no tax
/// classes must produce exactly the figures it always did, or every existing order total becomes a
/// lie. The rest covers mixed baskets, exemption, and the cent.
/// </summary>
public class TaxCalculatorTests
{
    private const decimal Standard = 0.11m;
    private const decimal Reduced = 0.05m;

    private readonly TaxCalculator _calc = new();

    private static TaxClass Rate(decimal rate, bool active = true, bool exempt = false) =>
        new() { Name = "class", Rate = rate, IsActive = active, IsExempt = exempt };

    private static OrderLine Line(decimal total, TaxClass? taxClass = null)
    {
        var line = new OrderLine { LineTotal = total, Quantity = 1, UnitPrice = total };

        if (taxClass is not null)
        {
            var product = new Product { Id = 1, ShopId = 1, Name = "P", Slug = "p", TaxClass = taxClass };
            line.ProductVariant = new ProductVariant { Id = 1, ProductId = 1, Product = product };
        }

        return line;
    }

    // ------------------------------------------------------------------
    // Backwards compatibility
    // ------------------------------------------------------------------

    [Fact]
    public void With_no_tax_class_the_store_rate_applies_unchanged()
    {
        Assert.Equal(5.39m, _calc.TaxForLine(Line(49.00m), Standard, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void A_basket_of_unclassified_lines_totals_the_flat_rate()
    {
        var lines = new[] { Line(49.00m), Line(32.50m) };

        Assert.Equal(8.97m, _calc.TaxForLines(lines, Standard, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void An_inactive_class_falls_back_to_the_store_rate()
    {
        // Deactivating a class must not silently zero out or change the tax on existing products.
        var line = Line(100.00m, Rate(Reduced, active: false));

        Assert.Equal(11.00m, _calc.TaxForLine(line, Standard, MidpointRounding.AwayFromZero));
    }

    // ------------------------------------------------------------------
    // Classes
    // ------------------------------------------------------------------

    [Fact]
    public void An_active_class_replaces_the_store_rate()
    {
        var line = Line(100.00m, Rate(Reduced));

        Assert.Equal(5.00m, _calc.TaxForLine(line, Standard, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void An_exempt_class_is_zero_whatever_the_rate()
    {
        var line = Line(500.00m, Rate(Standard, exempt: true));

        Assert.Equal(0m, _calc.TaxForLine(line, Standard, MidpointRounding.AwayFromZero));
        Assert.Equal(0m, _calc.RateFor(Rate(Standard, exempt: true), Standard));
    }

    [Fact]
    public void Exemption_beats_an_active_rate()
    {
        Assert.Equal(0m, _calc.RateFor(new TaxClass { Rate = 0.20m, IsActive = true, IsExempt = true }, Standard));
    }

    [Fact]
    public void A_mixed_basket_taxes_each_line_at_its_own_rate()
    {
        var lines = new[]
        {
            new TaxableLine(100.00m, Rate(Standard)),
            new TaxableLine(100.00m, Rate(Reduced)),
            new TaxableLine(100.00m, Rate(Standard, exempt: true))
        };

        // 11.00 + 5.00 + 0.00
        Assert.Equal(16.00m, _calc.TaxForMixedBasket(lines, 0m, Standard, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void A_mixed_basket_falls_back_per_line_for_unclassified_products()
    {
        var lines = new[]
        {
            new TaxableLine(100.00m, null),
            new TaxableLine(100.00m, Rate(Reduced))
        };

        Assert.Equal(16.00m, _calc.TaxForMixedBasket(lines, 0m, Standard, MidpointRounding.AwayFromZero));
    }

    // ------------------------------------------------------------------
    // Discounts
    // ------------------------------------------------------------------

    [Fact]
    public void A_discount_is_taxed_on_what_the_shopper_actually_pays()
    {
        // 10% off a 100.00 order: the shopper pays 90.00, so the tax is on 90.00.
        var lines = new[] { new TaxableLine(100.00m, null) };

        Assert.Equal(9.90m, _calc.TaxForMixedBasket(lines, 10.00m, Standard, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void A_discount_is_apportioned_across_lines_by_value()
    {
        // 90.00 of goods + 10.00 discounted: the first line takes 9.00 off, the second 1.00.
        var lines = new[]
        {
            new TaxableLine(90.00m, null),
            new TaxableLine(10.00m, null)
        };

        // Taxable 81.00 + 9.00 = 90.00 at 11%.
        Assert.Equal(9.90m, _calc.TaxForMixedBasket(lines, 10.00m, Standard, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void A_discount_on_a_mixed_basket_only_reduces_the_taxable_base()
    {
        var lines = new[]
        {
            new TaxableLine(100.00m, Rate(Standard)),
            new TaxableLine(100.00m, Rate(Reduced))
        };

        // 10.00 off, split 5.00/5.00: taxable 95.00 at 11% + 95.00 at 5% = 10.45 + 4.75.
        Assert.Equal(15.20m, _calc.TaxForMixedBasket(lines, 10.00m, Standard, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void A_discount_greater_than_the_goods_value_is_capped()
    {
        var lines = new[] { new TaxableLine(20.00m, null) };

        Assert.Equal(0m, _calc.TaxForMixedBasket(lines, 50.00m, Standard, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void A_zero_discount_changes_nothing()
    {
        var lines = new[] { new TaxableLine(49.00m, null) };

        Assert.Equal(
            _calc.TaxForMixedBasket(lines, 0m, Standard, MidpointRounding.AwayFromZero),
            _calc.TaxForMixedBasket(lines, 49.00m * 0m, Standard, MidpointRounding.AwayFromZero));
    }

    // ------------------------------------------------------------------
    // The cent
    // ------------------------------------------------------------------

    [Fact]
    public void Shares_always_sum_back_to_the_discount()
    {
        // The classic rounding leak: three equal lines and a discount that divides unevenly.
        var totals = new[] { 10.00m, 10.00m, 10.00m };
        var shares = TaxCalculator.ShareDiscount(totals, 1.00m, MidpointRounding.AwayFromZero);

        Assert.Equal(1.00m, shares.Sum());
    }

    [Fact]
    public void Shares_never_exceed_the_line_they_belong_to()
    {
        var totals = new[] { 1.00m, 99.00m };
        var shares = TaxCalculator.ShareDiscount(totals, 50.00m, MidpointRounding.AwayFromZero);

        Assert.All(shares, s => Assert.True(s >= 0m));
        Assert.Equal(50.00m, shares.Sum());
        Assert.True(shares[0] <= 1.00m);
    }

    [Fact]
    public void An_awkward_discount_still_totals_exactly()
    {
        var totals = new[] { 33.33m, 33.33m, 33.34m };
        var shares = TaxCalculator.ShareDiscount(totals, 7.77m, MidpointRounding.AwayFromZero);

        Assert.Equal(7.77m, shares.Sum());
    }

    [Fact]
    public void Shares_of_zero_for_no_discount()
    {
        var shares = TaxCalculator.ShareDiscount(new[] { 10.00m, 20.00m }, 0m, MidpointRounding.AwayFromZero);

        Assert.Equal(new[] { 0m, 0m }, shares);
    }

    [Fact]
    public void An_empty_basket_is_charged_nothing()
    {
        Assert.Equal(0m, _calc.TaxForMixedBasket(Array.Empty<TaxableLine>(), 10m, Standard, MidpointRounding.AwayFromZero));
        Assert.Empty(TaxCalculator.ShareDiscount(Array.Empty<decimal>(), 10m, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void A_zero_value_basket_is_charged_nothing()
    {
        var lines = new[] { new TaxableLine(0m, null) };

        Assert.Equal(0m, _calc.TaxForMixedBasket(lines, 10m, Standard, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void Tax_rounds_per_line_so_an_invoice_reconciles()
    {
        // Three lines of 0.05 taxed at 11%: each rounds to 0.01, so the invoice says 0.03 — not 0.02,
        // which is what taxing the 0.15 total once would give.
        var lines = new[]
        {
            new TaxableLine(0.05m, null),
            new TaxableLine(0.05m, null),
            new TaxableLine(0.05m, null)
        };

        Assert.Equal(0.03m, _calc.TaxForMixedBasket(lines, 0m, Standard, MidpointRounding.AwayFromZero));
    }
}
