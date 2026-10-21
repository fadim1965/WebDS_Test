using Microsoft.EntityFrameworkCore;

namespace WebDS_Test.Services;

/// <summary>
/// Outcome of presenting a coupon code: the message to show the shopper when it is rejected, or
/// the computed discount when it is accepted.
/// </summary>
public sealed record CouponResult(bool Success, string Message, decimal Discount, int? CouponId, string? CouponCode)
{
    public static CouponResult Ok(decimal discount, Coupon coupon, string message) =>
        new(true, message, discount, coupon.Id, coupon.Code);

    public static CouponResult Fail(string message) => new(false, message, 0m, null, null);
}

/// <summary>
/// Validates discount codes and computes what they are worth against a cart. Computing is
/// deliberately separate from redeeming: a cart is re-priced every time it is rendered, whereas a
/// redemption is recorded once, when the order is placed.
/// </summary>
public interface ICouponService
{
    /// <summary>
    /// Checks <paramref name="code"/> against the coupon rules and returns the discount it is worth
    /// on <paramref name="cart"/>. <paramref name="userId"/> is required only to enforce a per-user
    /// limit; guests pass null.
    /// </summary>
    Task<CouponResult> ValidateAndComputeAsync(string code, Cart cart, string? userId = null, CancellationToken ct = default);

    /// <summary>Records a redemption and advances the coupon's used count.</summary>
    Task RedeemAsync(int couponId, int orderId, string? userId, CancellationToken ct = default);
}

public sealed class CouponService : ICouponService
{
    private readonly ApplicationDbContext _db;

    public CouponService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<CouponResult> ValidateAndComputeAsync(
        string code, Cart cart, string? userId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return CouponResult.Fail("Enter a coupon code.");
        }

        var normalized = code.Trim().ToUpperInvariant();

        var coupon = await _db.Coupons
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Code == normalized, ct);

        if (coupon is null)
        {
            return CouponResult.Fail("That coupon code was not recognised.");
        }

        if (!coupon.IsActive)
        {
            return CouponResult.Fail("That coupon is no longer available.");
        }

        if (!coupon.IsWithinWindow(DateTime.UtcNow))
        {
            return CouponResult.Fail(coupon.StartsUtc.HasValue && DateTime.UtcNow < coupon.StartsUtc.Value
                ? "That coupon is not valid yet."
                : "That coupon has expired.");
        }

        if (!coupon.HasUsesRemaining)
        {
            return CouponResult.Fail("That coupon has reached its usage limit.");
        }

        if (userId is not null && coupon.MaxUsesPerUser is { } perUser)
        {
            var usedByUser = await _db.CouponUsages
                .CountAsync(u => u.CouponId == coupon.Id && u.UserId == userId, ct);

            if (usedByUser >= perUser)
            {
                return CouponResult.Fail("You have already used that coupon.");
            }
        }

        if (!coupon.AppliesToAll && coupon.ShopId is { } restrictedShop && cart.ShopId != restrictedShop)
        {
            return CouponResult.Fail("That coupon does not apply to the items in your cart.");
        }

        var subtotal = cart.Subtotal;
        if (coupon.MinSubtotal > 0m && subtotal < coupon.MinSubtotal)
        {
            return CouponResult.Fail($"Spend at least {coupon.MinSubtotal:0.00} to use that coupon.");
        }

        if (subtotal <= 0m)
        {
            return CouponResult.Fail("Add something to your cart first.");
        }

        var discount = coupon.Type switch
        {
            DiscountType.Percentage => Math.Round(subtotal * (coupon.Value / 100m), 2, MidpointRounding.AwayFromZero),
            DiscountType.FixedAmount => coupon.Value,
            _ => 0m
        };

        // A discount can never exceed the goods it applies to, nor be negative.
        discount = Math.Clamp(discount, 0m, subtotal);

        return discount <= 0m
            ? CouponResult.Fail("That coupon is worth nothing on this cart.")
            : CouponResult.Ok(discount, coupon, "Coupon applied.");
    }

    public async Task RedeemAsync(int couponId, int orderId, string? userId, CancellationToken ct = default)
    {
        var coupon = await _db.Coupons.FirstOrDefaultAsync(c => c.Id == couponId, ct);
        if (coupon is null)
        {
            return;
        }

        coupon.UsedCount += 1;

        _db.CouponUsages.Add(new CouponUsage
        {
            CouponId = coupon.Id,
            OrderId = orderId,
            UserId = userId,
            RedeemedUtc = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);
    }
}
