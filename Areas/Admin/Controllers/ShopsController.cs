using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebDS_Test.Domain;
using WebDS_Test.Services;

namespace WebDS_Test.Areas.Admin.Controllers;

[Area("Admin")]
public class ShopsController : AdminControllerBase
{
    private readonly IShopService _shops;
    private readonly IAuditTrail _audit;

    public ShopsController(
        ApplicationDbContext db,
        IOptions<StoreSettings> settings,
        IShopService shops,
        IAuditTrail audit)
        : base(db, settings)
    {
        _shops = shops;
        _audit = audit;
    }

    public async Task<IActionResult> Index(ShopStatus? status, CancellationToken ct)
    {
        var shops = await _shops.GetAllAsync(status, ct);
        ViewData["Title"] = "Shops";
        ViewData["Status"] = status;
        return View(shops);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, decimal? commissionPercent, CancellationToken ct)
    {
        // The form collects a percentage (10 means 10%); the domain stores a fraction (0.10).
        var rate = commissionPercent is { } percent ? percent / 100m : (decimal?)null;

        var result = await _shops.ApproveAsync(id, rate, ct);
        Flash(result.Message, result.Success);

        if (result.Success && result.Shop is { } approved)
        {
            await _audit.RecordAsync(CurrentUserName, "shop.approved", "Shop", id,
                $"Approved {approved.Name}"
                + (commissionPercent is { } pct ? $" with commission {pct}%." : "."));
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id, string reason, CancellationToken ct)
    {
        var result = await _shops.RejectAsync(id, reason, ct);
        Flash(result.Message, result.Success);

        if (result.Success && result.Shop is { } rejected)
        {
            await _audit.RecordAsync(CurrentUserName, "shop.rejected", "Shop", id,
                $"Rejected {rejected.Name}: {reason}");
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Suspend(int id, string reason, CancellationToken ct)
    {
        var result = await _shops.SuspendAsync(id, reason, ct);
        Flash(result.Message, result.Success);

        if (result.Success && result.Shop is { } suspended)
        {
            await _audit.RecordAsync(CurrentUserName, "shop.suspended", "Shop", id,
                $"Suspended {suspended.Name}: {reason}");
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reinstate(int id, CancellationToken ct)
    {
        var result = await _shops.ReinstateAsync(id, ct);
        Flash(result.Message, result.Success);

        if (result.Success && result.Shop is { } reinstated)
        {
            await _audit.RecordAsync(CurrentUserName, "shop.reinstated", "Shop", id,
                $"Reinstated {reinstated.Name} as {reinstated.Status.ToDisplay()}.");
        }

        return RedirectToAction(nameof(Index));
    }
}
