using Microsoft.EntityFrameworkCore;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// A discount code is the one place a shopper can name their own price, so the rules are pinned
/// here rather than left to the checkout to discover: what a code is worth, and every reason it can
/// be refused. Validation is deliberately separate from redemption, and both halves are covered.
/// </summary>
public class CouponTests
{
    // ------------------------------------------------------------------
    // Fixtures
    // ------------------------------------------------------------------

    private static Cart CartFor(decimal price, int quantity = 1, int shopId = 1)
    {
        var product = new Product { Id = 1, Name = "EMS Priority Envelope", Slug = "ems", ShopId = shopId };
        var variant = new ProductVariant
        {
            Id = 1, ProductId = 1, Sku = "EMS-10", Name = "Pack of 10", Price = price, IsActive = true
        };
        variant.Product = product;

        var cart = new Cart();
        cart.Items.Add(new CartItem { Id = 1, CartId = 1, ProductVariantId = 1, Quantity = quantity, ProductVariant = variant });
        return cart;
    }

    private static Coupon Coupon(
        string code = "SAVE10",
        DiscountType type = DiscountType.Percentage,
        decimal value = 10m,
        decimal minSubtotal = 0m)
        => new()
        {
            Code = code,
            Type = type,
            Value = value,
            MinSubtotal = minSubtotal,
            IsActive = true,
            AppliesToAll = true
        };

    private static async Task<(ApplicationDbContext Db, CouponService Service)> SetupAsync(Coupon coupon)
    {
        var db = TestSupport.NewDb();
        db.Coupons.Add(coupon);
        await db.SaveChangesAsync();
        return (db, new CouponService(db));
    }

    // ------------------------------------------------------------------
    // What a code is worth
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_percentage_code_takes_its_share_of_the_subtotal()
    {
        var (db, service) = await SetupAsync(Coupon(value: 10m));
        using var _ = db;

        var result = await service.ValidateAndComputeAsync("SAVE10", CartFor(54.00m));

        Assert.True(result.Success);
        Assert.Equal(5.40m, result.Discount);
        Assert.Equal("SAVE10", result.CouponCode);
    }

    [Fact]
    public async Task A_percentage_is_rounded_to_the_nearest_cent_away_from_zero()
    {
        var (db, service) = await SetupAsync(Coupon(value: 33m));
        using var _ = db;

        // 24.50 * 33% = 8.085, which must land on 8.09 rather than banker's 8.08.
        var result = await service.ValidateAndComputeAsync("SAVE10", CartFor(24.50m));

        Assert.True(result.Success);
        Assert.Equal(8.09m, result.Discount);
    }

    [Fact]
    public async Task A_fixed_code_takes_its_face_value()
    {
        var (db, service) = await SetupAsync(Coupon(type: DiscountType.FixedAmount, value: 5m, minSubtotal: 30m));
        using var _ = db;

        var result = await service.ValidateAndComputeAsync("SAVE10", CartFor(54.00m));

        Assert.True(result.Success);
        Assert.Equal(5.00m, result.Discount);
    }

    [Fact]
    public async Task A_fixed_code_is_capped_at_the_subtotal()
    {
        var (db, service) = await SetupAsync(Coupon(value: 50m, type: DiscountType.FixedAmount));
        using var _ = db;

        var result = await service.ValidateAndComputeAsync("SAVE10", CartFor(12.00m));

        Assert.True(result.Success);
        Assert.Equal(12.00m, result.Discount);      // never more than the goods
    }

    [Fact]
    public async Task A_percentage_above_the_subtotal_cannot_go_negative()
    {
        var (db, service) = await SetupAsync(Coupon(value: 100m));
        using var _ = db;

        var result = await service.ValidateAndComputeAsync("SAVE10", CartFor(19.99m));

        Assert.True(result.Success);
        Assert.Equal(19.99m, result.Discount);
    }

    // ------------------------------------------------------------------
    // The code itself
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_code_is_matched_without_regard_to_case_or_padding()
    {
        var (db, service) = await SetupAsync(Coupon(code: "SAVE10"));
        using var _ = db;

        var result = await service.ValidateAndComputeAsync("  save10 ", CartFor(20.00m));

        Assert.True(result.Success);
        Assert.Equal("SAVE10", result.CouponCode);
    }

    [Fact]
    public async Task A_blank_code_is_refused()
    {
        var (db, service) = await SetupAsync(Coupon());
        using var _ = db;

        Assert.False((await service.ValidateAndComputeAsync("   ", CartFor(20.00m))).Success);
    }

    [Fact]
    public async Task An_unknown_code_is_refused()
    {
        var (db, service) = await SetupAsync(Coupon(code: "SAVE10"));
        using var _ = db;

        var result = await service.ValidateAndComputeAsync("NOPE", CartFor(20.00m));

        Assert.False(result.Success);
        Assert.Contains("not recognised", result.Message);
    }

    // ------------------------------------------------------------------
    // The reasons a code stops working
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_inactive_code_is_refused()
    {
        var coupon = Coupon();
        coupon.IsActive = false;
        var (db, service) = await SetupAsync(coupon);
        using var _ = db;

        var result = await service.ValidateAndComputeAsync("SAVE10", CartFor(20.00m));

        Assert.False(result.Success);
        Assert.Contains("no longer available", result.Message);
    }

    [Fact]
    public async Task A_code_that_has_not_started_is_refused()
    {
        var coupon = Coupon();
        coupon.StartsUtc = DateTime.UtcNow.AddDays(1);
        var (db, service) = await SetupAsync(coupon);
        using var _ = db;

        var result = await service.ValidateAndComputeAsync("SAVE10", CartFor(20.00m));

        Assert.False(result.Success);
        Assert.Contains("not valid yet", result.Message);
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        var coupon = Coupon();
        coupon.ExpiresUtc = DateTime.UtcNow.AddDays(-1);
        var (db, service) = await SetupAsync(coupon);
        using var _ = db;

        var result = await service.ValidateAndComputeAsync("SAVE10", CartFor(20.00m));

        Assert.False(result.Success);
        Assert.Contains("expired", result.Message);
    }

    [Fact]
    public async Task A_code_inside_its_window_is_accepted()
    {
        var coupon = Coupon();
        coupon.StartsUtc = DateTime.UtcNow.AddDays(-1);
        coupon.ExpiresUtc = DateTime.UtcNow.AddDays(1);
        var (db, service) = await SetupAsync(coupon);
        using var _ = db;

        Assert.True((await service.ValidateAndComputeAsync("SAVE10", CartFor(20.00m))).Success);
    }

    [Fact]
    public async Task An_exhausted_code_is_refused()
    {
        var coupon = Coupon();
        coupon.MaxUses = 3;
        coupon.UsedCount = 3;
        var (db, service) = await SetupAsync(coupon);
        using var _ = db;

        var result = await service.ValidateAndComputeAsync("SAVE10", CartFor(20.00m));

        Assert.False(result.Success);
        Assert.Contains("usage limit", result.Message);
    }

    [Fact]
    public async Task A_basket_below_the_minimum_is_refused()
    {
        var (db, service) = await SetupAsync(Coupon(minSubtotal: 30m));
        using var _ = db;

        var result = await service.ValidateAndComputeAsync("SAVE10", CartFor(12.00m));

        Assert.False(result.Success);
        Assert.Contains("at least", result.Message);
    }

    [Fact]
    public async Task A_basket_exactly_at_the_minimum_is_accepted()
    {
        var (db, service) = await SetupAsync(Coupon(minSubtotal: 30m));
        using var _ = db;

        Assert.True((await service.ValidateAndComputeAsync("SAVE10", CartFor(30.00m))).Success);
    }

    [Fact]
    public async Task An_empty_basket_is_refused()
    {
        var (db, service) = await SetupAsync(Coupon());
        using var _ = db;

        var result = await service.ValidateAndComputeAsync("SAVE10", new Cart());

        Assert.False(result.Success);
    }

    // ------------------------------------------------------------------
    // Per-shopper limits
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_shopper_who_has_used_their_allowance_is_refused()
    {
        var coupon = Coupon();
        coupon.MaxUsesPerUser = 1;
        var (db, service) = await SetupAsync(coupon);

        db.CouponUsages.Add(new CouponUsage { CouponId = coupon.Id, UserId = "user-1", RedeemedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        using var _ = db;

        var result = await service.ValidateAndComputeAsync("SAVE10", CartFor(20.00m), userId: "user-1");

        Assert.False(result.Success);
        Assert.Contains("already used", result.Message);
    }

    [Fact]
    public async Task A_per_user_limit_only_counts_that_shopper()
    {
        var coupon = Coupon();
        coupon.MaxUsesPerUser = 1;
        var (db, service) = await SetupAsync(coupon);
        db.CouponUsages.Add(new CouponUsage { CouponId = coupon.Id, UserId = "someone-else" });
        await db.SaveChangesAsync();
        using var _ = db;

        Assert.True((await service.ValidateAndComputeAsync("SAVE10", CartFor(20.00m), "me")).Success);
    }

    [Fact]
    public async Task A_guest_is_not_held_to_a_per_shopper_limit()
    {
        var coupon = Coupon();
        coupon.MaxUsesPerUser = 1;
        var (db, service) = await SetupAsync(coupon);
        using var _ = db;

        // No user id: there is no one to count against, so the global limit is the only ceiling.
        Assert.True((await service.ValidateAndComputeAsync("SAVE10", CartFor(20.00m), userId: null)).Success);
    }

    // ------------------------------------------------------------------
    // Shop scoping
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_shop_specific_code_applies_only_to_its_shop()
    {
        var coupon = Coupon();
        coupon.AppliesToAll = false;
        coupon.ShopId = 2;
        var (db, service) = await SetupAsync(coupon);
        using var _ = db;

        var result = await service.ValidateAndComputeAsync("SAVE10", CartFor(20.00m, shopId: 1));

        Assert.False(result.Success);
        Assert.Contains("does not apply", result.Message);
    }

    [Fact]
    public async Task A_shop_specific_code_applies_to_the_matching_shop()
    {
        var coupon = Coupon();
        coupon.AppliesToAll = false;
        coupon.ShopId = 1;
        var (db, service) = await SetupAsync(coupon);
        using var _ = db;

        Assert.True((await service.ValidateAndComputeAsync("SAVE10", CartFor(20.00m, shopId: 1))).Success);
    }

    // ------------------------------------------------------------------
    // Redemption
    // ------------------------------------------------------------------

    [Fact]
    public async Task Redeeming_counts_the_use_and_records_who_and_which_order()
    {
        var (db, service) = await SetupAsync(Coupon());
        using var _ = db;

        await service.RedeemAsync(couponId: 1, orderId: 42, userId: "user-1");

        var stored = await db.Coupons.SingleAsync();
        Assert.Equal(1, stored.UsedCount);

        var usage = await db.CouponUsages.SingleAsync();
        Assert.Equal(1, usage.CouponId);
        Assert.Equal(42, usage.OrderId);
        Assert.Equal("user-1", usage.UserId);
    }

    [Fact]
    public async Task Redeeming_a_guest_order_records_no_user()
    {
        var (db, service) = await SetupAsync(Coupon());
        using var _ = db;

        await service.RedeemAsync(couponId: 1, orderId: 7, userId: null);

        var usage = await db.CouponUsages.SingleAsync();
        Assert.Null(usage.UserId);
    }

    [Fact]
    public async Task Redeeming_an_unknown_coupon_does_nothing()
    {
        var (db, service) = await SetupAsync(Coupon());
        using var _ = db;

        await service.RedeemAsync(couponId: 999, orderId: 1, userId: null);

        Assert.Equal(0, await db.CouponUsages.CountAsync());
        Assert.Equal(0, (await db.Coupons.SingleAsync()).UsedCount);
    }
}
