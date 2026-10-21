using System.ComponentModel.DataAnnotations;

namespace WebDS_Test.Domain;

/// <summary>
/// A discount code a shopper can present at the cart or checkout. Codes are matched
/// case-insensitively (they are stored upper-cased) and every constraint is re-checked at the
/// moment an order is placed, so a code that expires — or runs out of uses — between "apply" and
/// "place order" simply stops discounting rather than discounting twice.
/// </summary>
public class Coupon
{
    public int Id { get; set; }

    /// <summary>The code the shopper types, stored upper-cased and unique.</summary>
    [Required, StringLength(64)]
    public string Code { get; set; } = string.Empty;

    /// <summary>Optional note for staff; never shown to the shopper.</summary>
    [StringLength(256)]
    public string? Description { get; set; }

    public DiscountType Type { get; set; } = DiscountType.Percentage;

    /// <summary>Percent (0–100) for <see cref="DiscountType.Percentage"/>, or an amount for fixed.</summary>
    public decimal Value { get; set; }

    /// <summary>Orders below this subtotal cannot use the code. Zero means no minimum.</summary>
    public decimal MinSubtotal { get; set; }

    /// <summary>Total redemptions allowed across all shoppers; null means unlimited.</summary>
    public int? MaxUses { get; set; }

    /// <summary>Redemptions allowed per signed-in shopper; null means unlimited.</summary>
    public int? MaxUsesPerUser { get; set; }

    /// <summary>Incremented on every redemption; kept denormalised so the admin list stays cheap.</summary>
    public int UsedCount { get; set; }

    /// <summary>Optional validity window; either bound may be left open.</summary>
    public DateTime? StartsUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>When false the code is restricted to <see cref="ShopId"/>.</summary>
    public bool AppliesToAll { get; set; } = true;

    /// <summary>The single shop this code discounts when <see cref="AppliesToAll"/> is false.</summary>
    public int? ShopId { get; set; }
    public Shop? Shop { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>One row per redemption, used to enforce <see cref="MaxUsesPerUser"/>.</summary>
    public ICollection<CouponUsage> Usages { get; set; } = new List<CouponUsage>();

    /// <summary>True when the code is inside its validity window right now.</summary>
    public bool IsWithinWindow(DateTime utcNow) =>
        (!StartsUtc.HasValue || utcNow >= StartsUtc.Value) &&
        (!ExpiresUtc.HasValue || utcNow <= ExpiresUtc.Value);

    /// <summary>True when the code still has redemptions left.</summary>
    public bool HasUsesRemaining => !MaxUses.HasValue || UsedCount < MaxUses.Value;
}

/// <summary>
/// One coupon redemption. A row is written when an order is placed, which is what lets a
/// per-shopper cap be enforced without storing usage on the order itself. The user and order are
/// loose references so the ledger survives account or order deletion.
/// </summary>
public class CouponUsage
{
    public int Id { get; set; }

    public int CouponId { get; set; }
    public Coupon? Coupon { get; set; }

    public string? UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public int? OrderId { get; set; }
    public Order? Order { get; set; }

    public DateTime RedeemedUtc { get; set; } = DateTime.UtcNow;
}
