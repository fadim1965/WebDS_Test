using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebDS_Test.Domain;
using WebDS_Test.Services;

namespace WebDS_Test.Areas.Vendor.Controllers;

[Area("Vendor")]
[Authorize(Policy = "VendorOnly")]
public class DashboardController : Controller
{
    private readonly IShopService _shops;

    public DashboardController(IShopService shops) => _shops = shops;

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return Challenge();

        var shop = await _shops.GetPrimaryShopForUserAsync(userId, ct);
        if (shop is null)
        {
            return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });
        }

        var access = await _shops.ResolveAccessAsync(userId, shop.Id, ct);
        if (access is null) return Forbid();

        var dashboard = await _shops.GetDashboardAsync(shop.Id, ct);
        ViewData["Title"] = "Vendor dashboard";
        ViewData["Shop"] = shop;
        return View(dashboard);
    }
}
