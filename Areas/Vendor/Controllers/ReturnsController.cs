using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebDS_Test.Domain;
using WebDS_Test.Services;

namespace WebDS_Test.Areas.Vendor.Controllers;

/// <summary>
/// The shop's returns worklist. Requests come in from shoppers, are approved or rejected, the goods
/// are received and restocked — and nothing here moves money: the refund is issued by the
/// marketplace's administration once the goods are back, because it reverses the commission the
/// marketplace charged as much as the sale itself.
/// </summary>
/// <remarks>
/// Every query is filtered by the resolved shop, so a vendor sees their own returns and nothing
/// else — a request belonging to another shop is a 404, not a redaction.
/// </remarks>
[Area("Vendor")]
[Authorize(Policy = "VendorOnly")]
public class ReturnsController : Controller
{
    private readonly IReturnService _returns;
    private readonly IShopService _shops;
    private readonly IAuditTrail _audit;
    private readonly StoreSettings _settings;

    public ReturnsController(
        IReturnService returns,
        IShopService shops,
        IAuditTrail audit,
        IOptions<StoreSettings> settings)
    {
        _returns = returns;
        _shops = shops;
        _audit = audit;
        _settings = settings.Value;
    }

    private void Flash(string message, bool success = true)
    {
        TempData["Message"] = message;
        TempData["MessageType"] = success ? "success" : "danger";
    }

    private async Task<Shop?> ResolveShopAsync(CancellationToken ct)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return null;
        return await _shops.GetPrimaryShopForUserAsync(userId, ct);
    }

    /// <summary>
    /// The caller's role in this shop. Membership alone is enough to read the queue; the decisions
    /// below additionally require <see cref="ShopAccessPolicy.CanDecideAfterSales"/>.
    /// </summary>
    private async Task<ShopAccess?> ResolveAccessAsync(Shop shop, CancellationToken ct)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return userId is null ? null : await _shops.ResolveAccessAsync(userId, shop.Id, ct);
    }

    [HttpGet]
    public async Task<IActionResult> Index(bool? open, CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });

        var rows = await _returns.GetForShopAsync(shop.Id, open, ct);

        ViewData["Title"] = "Returns";
        ViewData["ShopName"] = shop.Name;
        ViewData["OpenOnly"] = open;
        ViewData["Currency"] = _settings.CurrencySymbol;
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });

        var request = await _returns.GetAsync(id, ct);

        // Belonging to another shop is indistinguishable from not existing.
        if (request is null || request.ShopId != shop.Id) return NotFound();

        ViewData["Title"] = $"Return {request.Reference}";
        ViewData["ShopName"] = shop.Name;
        ViewData["Currency"] = _settings.CurrencySymbol;
        ViewData["CanDecide"] = (await ResolveAccessAsync(shop, ct))?.CanDecideAfterSales() == true;
        return View(request);
    }

    /// <summary>Approves or rejects a requested return, with a note for the shopper.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Decide(int id, bool approve, string? note, CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });

        if ((await ResolveAccessAsync(shop, ct))?.CanDecideAfterSales() != true) return Forbid();

        var request = await _returns.GetAsync(id, ct);
        if (request is null || request.ShopId != shop.Id) return NotFound();

        var actor = User.Identity?.Name ?? "vendor";
        var result = await _returns.DecideAsync(id, approve, note, actor, ct);
        Flash(result.Message, result.Success);

        if (result.Success)
        {
            await _audit.RecordAsync(actor,
                approve ? "return.approved" : "return.rejected",
                "ReturnRequest", id,
                $"{(approve ? "Approved" : "Rejected")} {request.Reference} on order {request.Order?.OrderNumber} (shop {shop.Name})"
                + (string.IsNullOrWhiteSpace(note) ? "." : $": {note}"));
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Records that the goods came back and puts the units back on the shelf.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Receive(int id, CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });

        if ((await ResolveAccessAsync(shop, ct))?.CanDecideAfterSales() != true) return Forbid();

        var request = await _returns.GetAsync(id, ct);
        if (request is null || request.ShopId != shop.Id) return NotFound();

        var actor = User.Identity?.Name ?? "vendor";
        var result = await _returns.MarkReceivedAsync(id, actor, ct);
        Flash(result.Message, result.Success);

        if (result.Success)
        {
            await _audit.RecordAsync(actor, "return.received", "ReturnRequest", id,
                $"Received goods for {request.Reference} ({request.Lines.Sum(l => l.Quantity)} unit(s)) at shop {shop.Name} and restocked them.");
        }

        return RedirectToAction(nameof(Details), new { id });
    }
}
