using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebDS_Test.Areas.Admin.Models;
using WebDS_Test.Services.Export;

namespace WebDS_Test.Areas.Admin.Controllers;

/// <summary>
/// Marketing dashboard: revenue trend, best sellers, category mix, order funnel, customer
/// acquisition, payment methods and coupon performance. Authorised to administrators only.
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class MarketingController : AdminControllerBase
{
    private readonly IOrderService _orders;

    public MarketingController(
        ApplicationDbContext db,
        IOptions<StoreSettings> settings,
        IOrderService orders)
        : base(db, settings)
    {
        _orders = orders;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var stats = await _orders.GetMarketingDashboardAsync(ct);
        ViewData["Title"] = "Marketing dashboard";
        return View(stats);
    }

    #region Exports

    [HttpGet]
    public async Task<IActionResult> Export(string section, string? format, CancellationToken ct)
    {
        var stats = await _orders.GetMarketingDashboardAsync(ct);

        return section switch
        {
            "daily-revenue" => ExportDailyRevenue(stats, format),
            "top-products-revenue" => ExportTopProductsRevenue(stats, format),
            "top-products-units" => ExportTopProductsUnits(stats, format),
            "revenue-by-category" => ExportRevenueByCategory(stats, format),
            "top-customers" => ExportTopCustomers(stats, format),
            "payment-methods" => ExportPaymentMethods(stats, format),
            "coupons" => ExportCoupons(stats, format),
            "fast-moving" => ExportFastMoving(stats, format),
            _ => NotFound()
        };
    }

    private IActionResult ExportDailyRevenue(MarketingDashboardStats stats, string? format)
    {
        var rows = stats.DailyRevenue.Select(d => new object[]
        {
            d.Date.ToString("yyyy-MM-dd"),
            d.Orders,
            d.Revenue
        }).ToList();

        var data = new TabularData(
            "marketing-daily-revenue",
            "daily-revenue",
            new[] { "Date", "Orders", "Revenue" },
            rows);

        return ExportFile(data, format);
    }

    private IActionResult ExportTopProductsRevenue(MarketingDashboardStats stats, string? format)
    {
        var rows = stats.TopProductsByRevenue.Select(p => new object[]
        {
            p.Name,
            p.Sku,
            p.Category,
            p.Units,
            p.Orders,
            p.Revenue,
            p.AverageOrderValue
        }).ToList();

        var data = new TabularData(
            "marketing-top-products-revenue",
            "top-products-revenue",
            new[] { "Product", "SKU", "Category", "Units", "Orders", "Revenue", "Avg Order Value" },
            rows);

        return ExportFile(data, format);
    }

    private IActionResult ExportTopProductsUnits(MarketingDashboardStats stats, string? format)
    {
        var rows = stats.TopProductsByUnits.Select(p => new object[]
        {
            p.Name,
            p.Sku,
            p.Category,
            p.Units,
            p.Orders,
            p.Revenue,
            p.AverageOrderValue
        }).ToList();

        var data = new TabularData(
            "marketing-top-products-units",
            "top-products-units",
            new[] { "Product", "SKU", "Category", "Units", "Orders", "Revenue", "Avg Order Value" },
            rows);

        return ExportFile(data, format);
    }

    private IActionResult ExportRevenueByCategory(MarketingDashboardStats stats, string? format)
    {
        var rows = stats.RevenueByCategory.Select(c => new object[]
        {
            c.Name,
            c.Products,
            c.Units,
            c.Orders,
            c.Revenue
        }).ToList();

        var data = new TabularData(
            "marketing-revenue-by-category",
            "revenue-by-category",
            new[] { "Category", "Products", "Units", "Orders", "Revenue" },
            rows);

        return ExportFile(data, format);
    }

    private IActionResult ExportTopCustomers(MarketingDashboardStats stats, string? format)
    {
        var rows = stats.TopCustomers.Select(c => new object[]
        {
            c.Name,
            c.Email,
            c.Orders,
            c.Spend,
            c.LastOrder?.ToString("yyyy-MM-dd") ?? string.Empty
        }).ToList();

        var data = new TabularData(
            "marketing-top-customers",
            "top-customers",
            new[] { "Customer", "Email", "Orders", "Spend", "Last Order (UTC)" },
            rows);

        return ExportFile(data, format);
    }

    private IActionResult ExportPaymentMethods(MarketingDashboardStats stats, string? format)
    {
        var rows = stats.PaymentMethods.Select(p => new object[]
        {
            p.Label,
            p.Orders,
            p.RevenueRaw
        }).ToList();

        var data = new TabularData(
            "marketing-payment-methods",
            "payment-methods",
            new[] { "Method", "Orders", "Revenue" },
            rows);

        return ExportFile(data, format);
    }

    private IActionResult ExportCoupons(MarketingDashboardStats stats, string? format)
    {
        var rows = stats.CouponPerformance.Select(c => new object[]
        {
            c.Code,
            c.MaxUses,
            c.UsedCount,
            c.DiscountGiven,
            c.InfluencedRevenue,
            c.RedemptionRate
        }).ToList();

        var data = new TabularData(
            "marketing-coupons",
            "coupons",
            new[] { "Code", "Max Uses", "Uses", "Discount Given", "Influenced Revenue", "Redemption Rate (%)" },
            rows);

        return ExportFile(data, format);
    }

    private IActionResult ExportFastMoving(MarketingDashboardStats stats, string? format)
    {
        var rows = stats.FastMoving.Select(f => new object[]
        {
            f.ProductName,
            f.VariantSku,
            f.Units,
            f.Orders,
            f.StockRemaining,
            f.LowStock ? "Low stock" : "Healthy"
        }).ToList();

        var data = new TabularData(
            "marketing-fast-moving",
            "fast-moving",
            new[] { "Product", "Variant SKU", "Units", "Orders", "Stock Remaining", "Status" },
            rows);

        return ExportFile(data, format);
    }

    #endregion
}
