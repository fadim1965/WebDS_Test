using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebDS_Test.Areas.Admin.Models;
using WebDS_Test.Domain;

namespace WebDS_Test.Areas.Admin.Controllers;

/// <summary>Admin landing page: trading figures, alerts and the fulfilment queue.</summary>
public class DashboardController : AdminControllerBase
{
    private readonly IOrderService _orders;

    public DashboardController(
        ApplicationDbContext db,
        IOptions<StoreSettings> settings,
        IOrderService orders)
        : base(db, settings)
    {
        _orders = orders;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        // Everything the dashboard needs comes from one aggregated stats call, so the page does
        // not fan out into a query per tile.
        var stats = await _orders.GetDashboardStatsAsync(ct);

        var model = new DashboardViewModel
        {
            Stats = stats,
            PendingOrders = (await _orders.GetAllAsync(OrderStatus.Pending, null, 6, ct)),
            OutOfStockCount = stats.OutOfStockVariants
        };

        ViewData["Title"] = "Dashboard";
        return View(model);
    }
}

/// <summary>Backing model for the admin dashboard.</summary>
public sealed class DashboardViewModel
{
    public OrderDashboardStats Stats { get; init; } = new();
    public IReadOnlyList<Order> PendingOrders { get; init; } = Array.Empty<Order>();
    public int OutOfStockCount { get; init; }
}
