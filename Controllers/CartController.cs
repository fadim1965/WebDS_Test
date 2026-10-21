using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebDS_Test.Domain;
using WebDS_Test.Models;
using WebDS_Test.Services;

namespace WebDS_Test.Controllers;

/// <summary>Cart management. Every mutation is a POST with an antiforgery token.</summary>
public class CartController : StoreControllerBase
{
    private readonly ICouponService _coupons;
    private readonly IShopService _shops;

    public CartController(ICatalogService catalog, IOptions<StoreSettings> settings, ICartService cart, ICouponService coupons, IShopService shops)
        : base(catalog, settings, cart)
    {
        _coupons = coupons;
        _shops = shops;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        await LoadCartBadgeAsync(ct);

        var cart = await CartService.GetCartAsync(ct);

        // One cart, one shop: show that shop's shipping rate rather than the store default.
        var shop = cart?.ShopId is { } shopId ? await _shops.GetByIdAsync(shopId, ct) : null;
        var model = new CartViewModel { Cart = cart, Settings = Settings, Shop = shop };

        if (await CouponSession.ApplyAsync(HttpContext.Session, _coupons, cart, model, CurrentUserId, ct))
        {
            Flash("The coupon on your cart no longer applies and has been removed.", success: false);
        }

        ViewData["Title"] = "Your cart";
        return View(model);
    }

    /// <summary>Adds a variant, optionally straight from a product page, then returns there.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(
        [FromForm] int variantId,
        [FromForm] int quantity = 1,
        [FromForm] string? returnUrl = null,
        CancellationToken ct = default)
    {
        var result = await CartService.AddItemAsync(variantId, quantity, ct);
        Flash(result.Message, result.Success);

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return result.Success
            ? RedirectToAction(nameof(Index))
            : RedirectToAction("Index", "Catalog");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(
        [FromForm] int cartItemId,
        [FromForm] int quantity,
        CancellationToken ct = default)
    {
        var result = await CartService.UpdateQuantityAsync(cartItemId, quantity, ct);
        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove([FromForm] int cartItemId, CancellationToken ct = default)
    {
        var result = await CartService.RemoveItemAsync(cartItemId, ct);
        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Clear(CancellationToken ct = default)
    {
        await CartService.ClearAsync(ct);
        CouponSession.Clear(HttpContext.Session);
        Flash("Your cart is now empty.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Live cart totals for the header badge, polled after an AJAX add-to-cart.</summary>
    [HttpGet]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var cart = await CartService.GetCartAsync(ct);

        return Json(new
        {
            count = cart?.TotalQuantity ?? 0,
            subtotal = cart?.Subtotal ?? 0m,
            formatted = $"{Settings.CurrencySymbol}{cart?.Subtotal ?? 0m:N2}",
            isEmpty = cart is null || cart.IsEmpty
        });
    }

    private string? CurrentUserId =>
        User.Identity?.IsAuthenticated == true
            ? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            : null;
}
