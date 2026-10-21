using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebDS_Test.Areas.Admin.Models;
using WebDS_Test.Domain;

namespace WebDS_Test.Areas.Admin.Controllers;

/// <summary>Inventory oversight: stock levels, the movement ledger and manual adjustments.</summary>
public class InventoryController : AdminControllerBase
{
    private readonly IInventoryService _inventory;

    public InventoryController(
        ApplicationDbContext db,
        IOptions<StoreSettings> settings,
        IInventoryService inventory)
        : base(db, settings)
    {
        _inventory = inventory;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? q, string? filter, CancellationToken ct)
    {
        var query = Db.ProductVariants.AsNoTracking()
            .Include(v => v.Product)
            .ThenInclude(p => p!.Category)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = $"%{q.Trim().Replace("%", "\\%").Replace("_", "\\_")}%";
            query = query.Where(v => EF.Functions.Like(v.Sku, term, "\\") ||
                                     EF.Functions.Like(v.Name, term, "\\") ||
                                     EF.Functions.Like(v.Product!.Name, term, "\\"));
        }

        query = filter switch
        {
            "out" => query.Where(v => v.StockQuantity <= 0),
            "low" => query.Where(v => v.StockQuantity > 0 && v.StockQuantity <= v.LowStockThreshold),
            "inactive" => query.Where(v => !v.IsActive),
            _ => query
        };

        ViewData["Title"] = "Inventory";

        return View(new InventoryViewModel
        {
            Variants = await query
                .OrderBy(v => v.StockQuantity)
                .ThenBy(v => v.Product!.Name)
                .Take(400)
                .ToListAsync(ct),
            RecentMovements = await _inventory.GetRecentMovementsAsync(40, ct)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Adjust(int variantId, int delta, StockMovementReason reason, string? note, string? returnUrl, CancellationToken ct)
    {
        var result = await _inventory.AdjustAsync(variantId, delta, reason, note, CurrentUserName, ct);
        Flash(result.Message, result.Success);

        return !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? Redirect(returnUrl)
            : RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStock(int variantId, int newQuantity, string? note, CancellationToken ct)
    {
        var result = await _inventory.SetStockAsync(variantId, newQuantity, note, CurrentUserName, ct);
        Flash(result.Message, result.Success);

        return RedirectToAction(nameof(Index));
    }
}
