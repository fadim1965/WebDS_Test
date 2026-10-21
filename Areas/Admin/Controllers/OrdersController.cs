using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebDS_Test.Areas.Admin.Models;
using WebDS_Test.Domain;
using WebDS_Test.Services;
using WebDS_Test.Services.Export;

namespace WebDS_Test.Areas.Admin.Controllers;

/// <summary>Fulfilment queue: order list, detail and status transitions.</summary>
public class OrdersController : AdminControllerBase
{
    private readonly IOrderService _orders;
    private readonly IReturnService _returns;
    private readonly IAuditTrail _audit;

    public OrdersController(
        ApplicationDbContext db,
        IOptions<StoreSettings> settings,
        IOrderService orders,
        IReturnService returns,
        IAuditTrail audit)
        : base(db, settings)
    {
        _orders = orders;
        _returns = returns;
        _audit = audit;
    }

    [HttpGet]
    public async Task<IActionResult> Index(OrderStatus? status, string? q, CancellationToken ct)
    {
        var orders = await _orders.GetAllAsync(status, q, 200, ct);

        ViewData["Title"] = "Orders";

        return View(new AdminOrderListViewModel
        {
            Orders = orders,
            Status = status,
            Search = q,
            PendingCount = (await _orders.GetAllAsync(OrderStatus.Pending, null, 500, ct)).Count
        });
    }

    /// <summary>
    /// Downloads the same filtered list the page shows. <c>GetAllAsync</c> does not eager-load the
    /// shop, so shop names are resolved separately rather than by changing a query the list page
    /// also depends on.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Export(OrderStatus? status, string? q, string? format, CancellationToken ct)
    {
        var orders = await _orders.GetAllAsync(status, q, 200, ct);
        var shopNames = await Db.Shops.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var rows = orders
            .Select(o => new object?[]
            {
                o.OrderNumber,
                o.PlacedUtc,
                o.Status.ToDisplay(),
                o.CustomerName,
                o.Email,
                shopNames.TryGetValue(o.ShopId, out var shop) ? shop : string.Empty,
                o.Lines.Sum(l => l.Quantity),
                o.Subtotal,
                o.DiscountTotal,
                o.CouponCode,
                o.ShippingTotal,
                o.TaxTotal,
                o.GrandTotal,
                o.TrackingNumber,
                o.ShippingCity,
                o.ShippingCountry
            })
            .ToList();

        var data = new TabularData(
            "Orders",
            "orders",
            new[]
            {
                "Order", "Placed (UTC)", "Status", "Customer", "Email", "Shop", "Items",
                "Subtotal", "Discount", "Coupon", "Shipping", "Tax", "Total", "Tracking", "City", "Country"
            },
            rows);

        return ExportFile(data, format);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var order = await _orders.GetOrderAsync(id, null, ct);
        if (order is null)
        {
            return NotFound();
        }

        ViewData["Title"] = $"Order {order.OrderNumber}";
        ViewData["Returns"] = await _returns.GetForOrderAsync(id, ct);
        return View(order);
    }

    /// <summary>Applies a status transition, including the refund and restock side effects.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateStatus(int id, OrderStatus status, string? trackingNumber, CancellationToken ct)
    {
        var before = await Db.Orders.AsNoTracking()
            .Where(o => o.Id == id)
            .Select(o => new { o.OrderNumber, o.Status })
            .FirstOrDefaultAsync(ct);

        var result = await _orders.UpdateStatusAsync(id, status, trackingNumber, ct);
        Flash(result.Message, result.Success);

        if (result.Success && before is not null)
        {
            await _audit.RecordAsync(CurrentUserName, "order.status", "Order", id,
                $"Order {before.OrderNumber}: {before.Status.ToDisplay()} → {status.ToDisplay()}"
                + (status == OrderStatus.Refunded ? " (full refund issued to the shopper)." : "."));
        }
        else if (!result.Success && before is not null)
        {
            await _audit.RecordAsync(CurrentUserName, "order.status.rejected", "Order", id,
                $"Order {before.OrderNumber}: refused move to {status.ToDisplay()} — {result.Message}");
        }

        return RedirectToAction(nameof(Details), new { id });
    }
}
