using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebDS_Test.Domain;
using WebDS_Test.Services;

namespace WebDS_Test.Areas.Vendor.Controllers;

[Area("Vendor")]
[Authorize(Policy = "VendorOnly")]
public class OnboardingController : Controller
{
    private readonly IShopService _shops;

    public OnboardingController(IShopService shops) => _shops = shops;

    /// <summary>
    /// The application form. Serves three purposes: a first-time registration, a resubmission of a
    /// rejected application (prefilled, with the rejection reason shown), and a read-only status
    /// notice for shops still pending or suspended — each of which the vendor can only reach if
    /// the form actually answers their situation.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Register(CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (userId is null) return Challenge();

        var shop = await _shops.GetPrimaryShopForUserAsync(userId, ct);
        if (shop is null) return View(new ShopRegistration());

        // An approved shop has nothing to register — send the vendor back to their dashboard
        // rather than showing a form whose submit button can only fail.
        if (shop.Status == ShopStatus.Approved)
        {
            return RedirectToAction("Index", "Dashboard", new { area = "Vendor" });
        }

        SetStatusViewData(shop);
        return View(RegistrationFrom(shop));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(ShopRegistration model, CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (userId is null) return Challenge();

        if (!ModelState.IsValid)
        {
            await AttachStatusAsync(userId, ct);
            return View(model);
        }

        var existing = await _shops.GetPrimaryShopForUserAsync(userId, ct);
        var result = existing is null
            ? await _shops.RegisterAsync(userId, model, ct)
            : await _shops.ReapplyAsync(userId, model, ct);

        if (!result.Success)
        {
            ModelState.AddModelError(string.Empty, result.Message);
            await AttachStatusAsync(userId, ct);
            return View(model);
        }

        TempData["Message"] = result.Message;
        TempData["MessageType"] = "success";
        return RedirectToAction("Index", "Dashboard", new { area = "Vendor" });
    }

    private string? CurrentUserId =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    /// <summary>Re-attaches the status banner after a failed post, so the reason stays visible.</summary>
    private async Task AttachStatusAsync(string userId, CancellationToken ct)
    {
        var shop = await _shops.GetPrimaryShopForUserAsync(userId, ct);
        if (shop is not null) SetStatusViewData(shop);
    }

    private void SetStatusViewData(Shop shop)
    {
        ViewData["ShopStatus"] = shop.Status;
        ViewData["StatusReason"] = shop.StatusReason;
    }

    private static ShopRegistration RegistrationFrom(Shop shop) => new()
    {
        Name = shop.Name,
        Tagline = shop.Tagline,
        Description = shop.Description,
        ContactName = shop.ContactName,
        Phone = shop.Phone,
        Email = shop.Email,
        AddressLine1 = shop.AddressLine1,
        City = shop.City,
        PostalCode = shop.PostalCode,
        Country = shop.Country,
        PickupLocation = shop.PickupLocation,
        BankAccountName = shop.BankAccountName,
        BankName = shop.BankName,
        BankIban = shop.BankIban,
        ThemeKey = shop.ThemeKey,
        AcceptTerms = false
    };
}
