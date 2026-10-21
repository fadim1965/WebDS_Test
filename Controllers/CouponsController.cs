using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebDS_Test.Domain;
using WebDS_Test.Models;
using WebDS_Test.Services;

namespace WebDS_Test.Controllers;

/// <summary>Applies and removes discount codes on the cart. Both actions are POSTs with tokens.</summary>
public class CouponsController : StoreControllerBase
{
    private readonly ICouponService _coupons;

    public CouponsController(ICatalogService catalog, IOptions<StoreSettings> settings, ICartService cart, ICouponService coupons)
        : base(catalog, settings, cart)
    {
        _coupons = coupons;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Apply(ApplyCouponModel model, CancellationToken ct)
    {
        var returnUrl = Url.IsLocalUrl(model.ReturnUrl) ? model.ReturnUrl! : Url.Action(nameof(CartController.Index), "Cart")!;

        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(model.Code))
        {
            CouponSession.Clear(HttpContext.Session);
            Flash("Enter a coupon code.", success: false);
            return Redirect(returnUrl);
        }

        var cart = await CartService.GetCartAsync(ct);
        if (cart is null || cart.IsEmpty)
        {
            Flash("Your cart is empty.", success: false);
            return Redirect(returnUrl);
        }

        var userId = User.Identity?.IsAuthenticated == true
            ? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            : null;

        var result = await _coupons.ValidateAndComputeAsync(model.Code, cart, userId, ct);
        if (!result.Success)
        {
            CouponSession.Clear(HttpContext.Session);
            Flash(result.Message, success: false);
            return Redirect(returnUrl);
        }

        CouponSession.Set(HttpContext.Session, result.CouponCode!);
        Flash($"Coupon {result.CouponCode} applied.", success: true);
        return Redirect(returnUrl);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Remove(string? returnUrl)
    {
        CouponSession.Clear(HttpContext.Session);
        Flash("Coupon removed.", success: true);
        return Redirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Action(nameof(CartController.Index), "Cart")!);
    }
}
