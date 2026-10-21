using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Models;
using WebDS_Test.Services;

namespace WebDS_Test.Controllers;

/// <summary>
/// Checkout: review the cart, capture contact and shipping details, then place the order.
/// </summary>
public class CheckoutController : StoreControllerBase
{
    /// <summary>Session slot holding the idempotency key issued with the checkout form.</summary>
    private const string IdempotencySessionKey = "WebDS.CheckoutKey";

    private readonly IOrderService _orders;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<CheckoutController> _logger;
    private readonly ICouponService _coupons;
    private readonly IReadOnlyList<IPaymentGateway> _gateways;

    public CheckoutController(
        ICatalogService catalog,
        IOptions<StoreSettings> settings,
        ICartService cart,
        IOrderService orders,
        ApplicationDbContext db,
        ILogger<CheckoutController> logger,
        ICouponService coupons,
        IEnumerable<IPaymentGateway> gateways)
        : base(catalog, settings, cart)
    {
        _orders = orders;
        _db = db;
        _logger = logger;
        _coupons = coupons;
        _gateways = gateways.ToList();
    }

    /// <summary>
    /// True when a hosted gateway is registered, which is what makes the card choice available.
    /// The default (sandbox) provider has no hosted page, so the store stays cash-on-delivery.
    /// </summary>
    private bool CardAvailable => _gateways.OfType<IHostedPaymentGateway>().Any();

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        await LoadCartBadgeAsync(ct);

        var cart = await CartService.GetCartAsync(ct);
        var cartModel = new CartViewModel { Cart = cart, Settings = Settings, Shop = await LoadCartShopAsync(cart, ct) };

        if (cart is null || cart.IsEmpty)
        {
            Flash("Your cart is empty — add something before checking out.", success: false);
            return RedirectToAction("Index", "Cart");
        }

        if (!cartModel.CanCheckout)
        {
            Flash("Some items in your cart need attention before you can check out.", success: false);
            return RedirectToAction("Index", "Cart");
        }

        var userId = CurrentUserId;
        var addresses = userId is null
            ? new List<Address>()
            : await _db.Addresses.AsNoTracking()
                .Where(a => a.UserId == userId)
                .OrderByDescending(a => a.IsDefault)
                .ThenBy(a => a.Id)
                .ToListAsync(ct);

        // A fresh key per render: a resubmitted form cannot create a second order.
        var idempotencyKey = Guid.NewGuid().ToString("N");
        HttpContext.Session.SetString(IdempotencySessionKey, idempotencyKey);

        var model = new CheckoutViewModel
        {
            IdempotencyKey = idempotencyKey,
            Cart = cartModel,
            SavedAddresses = addresses,
            CardAvailable = CardAvailable
        };
        await CouponSession.ApplyAsync(HttpContext.Session, _coupons, cart, cartModel, userId, ct);

        var defaultAddress = addresses.FirstOrDefault(a => a.IsDefault) ?? addresses.FirstOrDefault();
        if (defaultAddress is not null)
        {
            model.SelectedAddressId = defaultAddress.Id;
            ApplyAddress(model, defaultAddress);
        }

        if (User.Identity?.IsAuthenticated == true)
        {
            var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is not null)
            {
                model.Email = user.Email;
                model.CustomerName = user.DisplayName;
                model.Phone ??= user.Phone;
            }
        }

        ViewData["Title"] = "Checkout";
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitPolicies.Checkout)]
    public async Task<IActionResult> Place(CheckoutViewModel model, CancellationToken ct)
    {
        var cart = await CartService.GetCartAsync(ct);
        var cartModel = new CartViewModel { Cart = cart, Settings = Settings, Shop = await LoadCartShopAsync(cart, ct) };

        if (cart is null || cart.IsEmpty)
        {
            Flash("Your cart is empty.", success: false);
            return RedirectToAction("Index", "Cart");
        }

        if (!cartModel.CanCheckout)
        {
            Flash("Some items in your cart need attention before you can check out.", success: false);
            return RedirectToAction("Index", "Cart");
        }

        // The key must match the one issued with the form the shopper actually loaded.
        var issuedKey = HttpContext.Session.GetString(IdempotencySessionKey);
        if (string.IsNullOrEmpty(issuedKey) || issuedKey != model.IdempotencyKey)
        {
            _logger.LogWarning("Rejected checkout with a stale or missing idempotency key");
            Flash("That checkout session has expired. Please review your details and submit again.", success: false);
            return RedirectToAction(nameof(Index));
        }

        if (!ModelState.IsValid)
        {
            model.Cart = cartModel;
            model.SavedAddresses = await LoadAddressesAsync(ct);
            await LoadCartBadgeAsync(ct);
            await CouponSession.ApplyAsync(HttpContext.Session, _coupons, cart, cartModel, CurrentUserId, ct);

            ViewData["Title"] = "Checkout";
            return View(model);
        }

        var request = new CheckoutRequest
        {
            Email = model.Email,
            CustomerName = model.CustomerName,
            Phone = model.Phone,
            ShippingName = model.ShippingName,
            ShippingLine1 = model.ShippingLine1,
            ShippingLine2 = model.ShippingLine2,
            ShippingCity = model.ShippingCity,
            ShippingState = model.ShippingState,
            ShippingPostalCode = model.ShippingPostalCode,
            ShippingCountry = model.ShippingCountry,
            CustomerNote = model.CustomerNote,
            IdempotencyKey = model.IdempotencyKey,
            // A posted payment choice is only honoured when a hosted gateway can actually take it;
            // otherwise the store falls back to the cash-on-delivery default it was built around.
            PaymentMethod = CardAvailable ? model.PaymentMethod : PaymentMethod.CashOnDelivery
        };

        var couponCode = CouponSession.Read(HttpContext.Session);
        if (!string.IsNullOrWhiteSpace(couponCode))
        {
            request.CouponCode = couponCode;
        }

        var result = await _orders.PlaceOrderAsync(request, cart, ct);

        if (!result.Success || result.Order is null)
        {
            Flash(result.Message, success: false);
            return RedirectToAction(nameof(Index));
        }

        var order = result.Order;

        // The cart has been converted into the order; empty it and retire the key.
        await CartService.ClearAsync(ct);
        HttpContext.Session.Remove(IdempotencySessionKey);
        CouponSession.Clear(HttpContext.Session);

        // Let this browser view the confirmation even when it checked out as a guest.
        var viewable = HttpContext.Session.GetString(OrdersController.ViewableOrdersSessionKey);
        var ids = string.IsNullOrEmpty(viewable) ? new List<int>() : viewable.Split(',').Select(int.Parse).ToList();
        ids.Add(order.Id);
        HttpContext.Session.SetString(OrdersController.ViewableOrdersSessionKey, string.Join(',', ids.Distinct().TakeLast(20)));

        if (model.SaveAddressToBook && User.Identity?.IsAuthenticated == true)
        {
            await SaveAddressAsync(model, ct);
        }

        _logger.LogInformation("Checkout completed for order {OrderNumber}", order.OrderNumber);

        // A card order is not finished until the provider says so. Hand the shopper to the provider;
        // the order flips to Paid when its signed webhook lands, and the return page waits for it.
        if (order.PaymentMethod != PaymentMethod.CashOnDelivery && CardAvailable)
        {
            return RedirectToAction("Pay", "Payments", new { id = order.Id });
        }

        return RedirectToAction(nameof(Confirmation), new { id = order.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Confirmation(int id, CancellationToken ct)
    {
        await LoadCartBadgeAsync(ct);

        var userId = CurrentUserId;
        var order = await _orders.GetOrderAsync(id, userId, ct);

        if (order is null && IsViewableInSession(id))
        {
            order = await _orders.GetOrderAsync(id, null, ct);
        }

        if (order is null)
        {
            return NotFound();
        }

        ViewData["Title"] = $"Order {order.OrderNumber}";
        return View(order);
    }

    private bool IsViewableInSession(int orderId)
    {
        var raw = HttpContext.Session.GetString(OrdersController.ViewableOrdersSessionKey);
        return !string.IsNullOrEmpty(raw) &&
               raw.Split(',').Select(int.Parse).Contains(orderId);
    }

    private string? CurrentUserId =>
        User.Identity?.IsAuthenticated == true
            ? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            : null;

    /// <summary>
    /// The shop the cart's items belong to, so the preview quotes that shop's shipping rate
    /// rather than the store-wide default. Null for an empty or shopless cart.
    /// </summary>
    private async Task<Shop?> LoadCartShopAsync(Cart? cart, CancellationToken ct)
    {
        if (cart?.ShopId is not { } shopId) return null;
        return await _db.Shops.AsNoTracking().FirstOrDefaultAsync(s => s.Id == shopId, ct);
    }

    private async Task<IReadOnlyList<Address>> LoadAddressesAsync(CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (userId is null)
        {
            return Array.Empty<Address>();
        }

        return await _db.Addresses.AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.IsDefault)
            .ThenBy(a => a.Id)
            .ToListAsync(ct);
    }

    private static void ApplyAddress(CheckoutViewModel model, Address address)
    {
        model.ShippingName = address.FullName;
        model.ShippingLine1 = address.Line1;
        model.ShippingLine2 = address.Line2;
        model.ShippingCity = address.City;
        model.ShippingState = address.State;
        model.ShippingPostalCode = address.PostalCode;
        model.ShippingCountry = address.Country;
        model.Phone ??= address.Phone;
    }

    /// <summary>Optionally stores the checkout address in the customer's address book.</summary>
    private async Task SaveAddressAsync(CheckoutViewModel model, CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (userId is null)
        {
            return;
        }

        var duplicate = await _db.Addresses.AnyAsync(a =>
            a.UserId == userId &&
            a.Line1 == model.ShippingLine1 &&
            a.PostalCode == model.ShippingPostalCode, ct);

        if (duplicate)
        {
            return;
        }

        var hasDefault = await _db.Addresses.AnyAsync(a => a.UserId == userId && a.IsDefault, ct);

        _db.Addresses.Add(new Address
        {
            UserId = userId,
            FullName = model.ShippingName,
            Line1 = model.ShippingLine1,
            Line2 = model.ShippingLine2,
            City = model.ShippingCity,
            State = model.ShippingState,
            PostalCode = model.ShippingPostalCode,
            Country = model.ShippingCountry,
            Phone = model.Phone,
            IsDefault = !hasDefault
        });

        await _db.SaveChangesAsync(ct);
    }
}
