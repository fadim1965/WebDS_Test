using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebDS_Test.Domain;

namespace WebDS_Test.Areas.Admin.Controllers;

/// <summary>
/// Discount-code administration. Inherits the area's Admin-only policy from
/// <see cref="AdminControllerBase"/>; nothing here can be reached by a shopper.
/// </summary>
public class CouponsController : AdminControllerBase
{
    public CouponsController(ApplicationDbContext db, IOptions<StoreSettings> settings)
        : base(db, settings)
    {
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["CurrencySymbol"] = Settings.CurrencySymbol;
        var coupons = await Db.Coupons
            .AsNoTracking()
            .OrderByDescending(c => c.IsActive)
            .ThenBy(c => c.Code)
            .ToListAsync(ct);

        ViewData["Title"] = "Coupons";
        return View(coupons);
    }

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        await LoadShopsAsync(ct);
        ViewData["Title"] = "New coupon";
        return View(new Coupon());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Coupon model, CancellationToken ct)
    {
        Normalize(model);

        if (ModelState.IsValid && await Db.Coupons.AnyAsync(c => c.Code == model.Code, ct))
        {
            ModelState.AddModelError(nameof(Coupon.Code), "That code is already in use.");
        }

        if (!ModelState.IsValid)
        {
            await LoadShopsAsync(ct);
            ViewData["Title"] = "New coupon";
            return View(model);
        }

        model.CreatedUtc = DateTime.UtcNow;
        Db.Coupons.Add(model);
        await Db.SaveChangesAsync(ct);

        Flash($"Coupon {model.Code} created.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var coupon = await Db.Coupons.FindAsync(new object[] { id }, ct);
        if (coupon is null)
        {
            return NotFound();
        }

        ViewData["Title"] = $"Edit {coupon.Code}";
        await LoadShopsAsync(ct);
        return View(coupon);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, Coupon model, CancellationToken ct)
    {
        var coupon = await Db.Coupons.FindAsync(new object[] { id }, ct);
        if (coupon is null)
        {
            return NotFound();
        }

        Normalize(model);

        if (await Db.Coupons.AnyAsync(c => c.Code == model.Code && c.Id != id, ct))
        {
            ModelState.AddModelError(nameof(Coupon.Code), "That code is already in use.");
        }

        if (!ModelState.IsValid)
        {
            ViewData["Title"] = $"Edit {model.Code}";
            await LoadShopsAsync(ct);
            return View(model);
        }

        coupon.Code = model.Code;
        coupon.Description = model.Description;
        coupon.Type = model.Type;
        coupon.Value = model.Value;
        coupon.MinSubtotal = model.MinSubtotal;
        coupon.MaxUses = model.MaxUses;
        coupon.MaxUsesPerUser = model.MaxUsesPerUser;
        coupon.StartsUtc = model.StartsUtc;
        coupon.ExpiresUtc = model.ExpiresUtc;
        coupon.IsActive = model.IsActive;
        coupon.AppliesToAll = model.AppliesToAll;
        coupon.ShopId = model.AppliesToAll ? null : model.ShopId;

        await Db.SaveChangesAsync(ct);

        Flash($"Coupon {coupon.Code} saved.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var coupon = await Db.Coupons.FindAsync(new object[] { id }, ct);
        if (coupon is null)
        {
            return NotFound();
        }

        // A redeemed coupon is part of the order history, so it is deactivated rather than removed.
        if (coupon.UsedCount > 0)
        {
            coupon.IsActive = false;
            Flash($"Coupon {coupon.Code} has already been redeemed, so it was deactivated instead of deleted.", success: false);
        }
        else
        {
            Db.Coupons.Remove(coupon);
            Flash($"Coupon {coupon.Code} deleted.");
        }

        await Db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Trims/cases the code and rejects the value shapes the calculator cannot honour.</summary>
    private void Normalize(Coupon model)
    {
        model.Code = (model.Code ?? string.Empty).Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(model.Code))
        {
            ModelState.AddModelError(nameof(Coupon.Code), "Enter a code.");
        }

        if (model.Type == DiscountType.Percentage && (model.Value <= 0m || model.Value > 100m))
        {
            ModelState.AddModelError(nameof(Coupon.Value), "A percentage discount must be greater than 0 and at most 100.");
        }
        else if (model.Type == DiscountType.FixedAmount && model.Value <= 0m)
        {
            ModelState.AddModelError(nameof(Coupon.Value), "A fixed discount must be greater than 0.");
        }

        if (model.MaxUses is < 1)
        {
            ModelState.AddModelError(nameof(Coupon.MaxUses), "Leave blank for unlimited, or enter at least 1.");
        }

        if (model.MaxUsesPerUser is < 1)
        {
            ModelState.AddModelError(nameof(Coupon.MaxUsesPerUser), "Leave blank for unlimited, or enter at least 1.");
        }

        if (model.StartsUtc is { } start && model.ExpiresUtc is { } end && end <= start)
        {
            ModelState.AddModelError(nameof(Coupon.ExpiresUtc), "The expiry must be after the start.");
        }

        if (!model.AppliesToAll && model.ShopId is null)
        {
            ModelState.AddModelError(nameof(Coupon.ShopId), "Choose the shop this coupon applies to, or let it apply to all shops.");
        }
    }

    private async Task LoadShopsAsync(CancellationToken ct)
    {
        ViewData["Shops"] = new SelectList(
            await Db.Shops.OrderBy(s => s.Name).Select(s => new { s.Id, s.Name }).ToListAsync(ct),
            "Id", "Name");
    }
}
