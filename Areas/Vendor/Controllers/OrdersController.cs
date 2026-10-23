using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebDS_Test.Domain;
using WebDS_Test.Services;
using WebDS_Test.Services.Shipping;

namespace WebDS_Test.Areas.Vendor.Controllers;

/// <summary>
/// The shop's own order book. Every query is filtered by the resolved shop, so a vendor can read
/// their own orders and nothing else — an order belonging to another shop is a 404, not a redaction.
/// </summary>
[Area("Vendor")]
[Authorize(Policy = "VendorOnly")]
public class OrdersController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IShopService _shops;
    private readonly IOrderService _orders;
    private readonly IFulfilmentService _fulfilment;
    private readonly ICarrierRegistry _carriers;
    private readonly IReturnService _returns;
    private readonly IPostSaleService _postSales;
    private readonly StoreSettings _settings;

    public OrdersController(
        ApplicationDbContext db,
        IShopService shops,
        IOrderService orders,
        IFulfilmentService fulfilment,
        ICarrierRegistry carriers,
        IReturnService returns,
        IPostSaleService postSales,
        IOptions<StoreSettings> settings)
    {
        _db = db;
        _shops = shops;
        _orders = orders;
        _fulfilment = fulfilment;
        _carriers = carriers;
        _returns = returns;
        _postSales = postSales;
        _settings = settings.Value;
    }

    private void Flash(string message, bool success = true)
    {
        TempData["Message"] = message;
        TempData["MessageType"] = success ? "success" : "danger";
    }

    [HttpGet]
    public async Task<IActionResult> Index(OrderStatus? status, string? q, CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });

        var query = _db.Orders.AsNoTracking()
            .Include(o => o.Lines)
            .Where(o => o.ShopId == shop.Id);

        if (status is { } s)
        {
            query = query.Where(o => o.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = $"%{q.Trim().Replace("%", "\\%").Replace("_", "\\_")}%";
            query = query.Where(o => EF.Functions.Like(o.OrderNumber, term, "\\") ||
                                     EF.Functions.Like(o.CustomerName!, term, "\\") ||
                                     EF.Functions.Like(o.Email, term, "\\"));
        }

        var rows = await query
            .OrderByDescending(o => o.PlacedUtc)
            .Take(200)
            .ToListAsync(ct);

        ViewData["Title"] = "Orders";
        ViewData["ShopName"] = shop.Name;
        ViewData["Status"] = status;
        ViewData["Query"] = q;
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });

        var order = await _orders.GetOrderAsync(id, null, ct);

        // Belonging to another shop is indistinguishable from not existing.
        if (order is null || order.ShopId != shop.Id) return NotFound();

        var shipments = await _fulfilment.GetShipmentsForShopAsync(shop.Id, false, ct);

        ViewData["Title"] = order.OrderNumber;
        ViewData["ShopName"] = shop.Name;
        ViewData["Shipments"] = shipments.Where(s => s.OrderId == order.Id)
            .OrderByDescending(s => s.CreatedUtc)
            .ToList();
        ViewData["Returns"] = await _returns.GetForOrderAsync(order.Id, ct);
        ViewData["SellerFulfilment"] = await _postSales.GetFulfilmentAsync(order.Id, shop.Id, ct);
        ViewData["CarrierName"] = _carriers.Default.DisplayName;
        return View(order);
    }

    /// <summary>
    /// Books a consignment with the carrier and moves the order to Shipped. The fulfilment
    /// service does the heavy lifting — carrier call, tracking timeline, label, COD collection
    /// and the buyer's shipping email — and scopes everything to this shop by id.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Ship(int id, CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });

        var result = await _fulfilment.DispatchAsync(id, shop.Id, _carriers.Default.Key, ct: ct);
        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Pulls the latest carrier tracking state onto the order's open consignment. Scoped through
    /// the shop's own shipments first, so a vendor cannot refresh another shop's consignment by
    /// guessing a shipment id.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefreshTracking(int id, CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });

        var shipments = await _fulfilment.GetShipmentsForShopAsync(shop.Id, false, ct);
        var shipment = shipments.FirstOrDefault(s => s.OrderId == id && s.IsOpen);

        if (shipment is null)
        {
            Flash("This order has no open consignment to refresh.", success: false);
            return RedirectToAction(nameof(Details), new { id });
        }

        var result = await _fulfilment.RefreshTrackingAsync(shipment.Id, ct);
        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<Shop?> ResolveShopAsync(CancellationToken ct)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return null;
        return await _shops.GetPrimaryShopForUserAsync(userId, ct);
    }
}
