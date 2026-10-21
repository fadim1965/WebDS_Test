using WebDS_Test.Domain;
using WebDS_Test.Models;
using WebDS_Test.Services;

namespace WebDS_Test.Controllers;

/// <summary>
/// The coupon a shopper has applied lives in the session as a bare code — never as a computed
/// amount. Every time a cart is rendered it is re-priced from the current basket, so editing the
/// cart after applying a code cannot leave a stale discount on screen, and a code that stops being
/// valid (expired, exhausted, minimum no longer met) quietly drops off.
/// </summary>
internal static class CouponSession
{
    private const string Key = "WebDS.Coupon";

    public static string? Read(ISession session) => session.GetString(Key);

    public static void Set(ISession session, string code) =>
        session.SetString(Key, code.Trim().ToUpperInvariant());

    public static void Clear(ISession session) => session.Remove(Key);

    /// <summary>
    /// Re-prices <paramref name="model"/> against the code held in the session, clearing the session
    /// when the code no longer applies. Returns false when a previously applied code was dropped, so
    /// the caller can tell the shopper why.
    /// </summary>
    public static async Task<bool> ApplyAsync(
        ISession session, ICouponService coupons, Cart? cart, CartViewModel model,
        string? userId, CancellationToken ct)
    {
        var code = Read(session);
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        if (cart is null || cart.IsEmpty)
        {
            Clear(session);
            return false;
        }

        var result = await coupons.ValidateAndComputeAsync(code, cart, userId, ct);
        if (!result.Success)
        {
            Clear(session);
            return true;
        }

        model.AppliedCouponCode = result.CouponCode;
        model.CouponDiscount = result.Discount;
        return false;
    }
}
