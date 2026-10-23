using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebDS_Test.Domain;
using WebDS_Test.Services;

namespace WebDS_Test.Areas.Vendor.Controllers;

/// <summary>
/// The shop's view of the marketplace's after-sales processes. LibanPost decides and issues the
/// remedy; the seller needs to see what is coming back and to answer questions about it.
/// </summary>
[Area("Vendor")]
[Authorize(Policy = "VendorOnly")]
public class PostSaleController : Controller
{
    private readonly IPostSaleService _postSales;
    private readonly IShopService _shops;
    private readonly StoreSettings _settings;

    public PostSaleController(
        IPostSaleService postSales,
        IShopService shops,
        IOptions<StoreSettings> settings)
    {
        _postSales = postSales;
        _shops = shops;
        _settings = settings.Value;
    }

    private async Task<Shop?> ResolveShopAsync(CancellationToken ct)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return userId is null ? null : await _shops.GetPrimaryShopForUserAsync(userId, ct);
    }

    private void Flash(string message, bool success = true)
    {
        TempData["Message"] = message;
        TempData["MessageType"] = success ? "success" : "danger";
    }

    [HttpGet]
    public async Task<IActionResult> Index(bool? open, CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });

        var rows = await _postSales.GetForShopAsync(shop.Id, open == true, ct);

        ViewData["Title"] = "After-sales";
        ViewData["ShopName"] = shop.Name;
        ViewData["OpenOnly"] = open;
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });

        var request = await _postSales.GetAsync(id, ct);
        if (request is null || request.ShopId != shop.Id) return NotFound();

        ViewData["Title"] = $"After-sales {request.Reference}";
        ViewData["ShopName"] = shop.Name;
        return View(request);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Message(int id, string content, CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });

        var request = await _postSales.GetAsync(id, ct);
        if (request is null || request.ShopId != shop.Id) return NotFound();

        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "vendor";
        var result = await _postSales.AddMessageAsync(
            id, userId, User.Identity?.Name ?? "Seller", fromCustomer: false, content, ct);

        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Details), new { id });
    }

    // ---- Seller fulfilment ---------------------------------------------

    /// <summary>The shop's own consignments and where each one is in the label process.</summary>
    [HttpGet]
    public async Task<IActionResult> Fulfilments(CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });

        var rows = (await _postSales.GetFulfilmentsAsync(openOnly: false, ct))
            .Where(f => f.ShopId == shop.Id)
            .ToList();

        ViewData["Title"] = "Packages & labels";
        ViewData["ShopName"] = shop.Name;
        return View(rows);
    }

    /// <summary>
    /// Confirms what the seller packed. LibanPost's process is explicit about the nylon pocket and
    /// the invoice, so the form asks for both rather than assuming them.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmFulfilment(
        int orderId, int packageCount, bool hasNylonPockets, bool invoiceAttached, CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });

        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "vendor";
        var result = await _postSales.ConfirmSellerFulfilmentAsync(
            orderId, shop.Id, packageCount, hasNylonPockets, invoiceAttached, userId, ct);

        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Fulfilments));
    }
}
