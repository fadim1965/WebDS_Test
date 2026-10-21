using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebDS_Test.Domain;
using WebDS_Test.Services;

namespace WebDS_Test.Areas.Vendor.Controllers;

/// <summary>
/// Lets a vendor choose the storefront template theme for their shop.
/// </summary>
[Area("Vendor")]
[Authorize(Policy = "VendorOnly")]
public class AppearanceController : Controller
{
    private readonly IShopService _shops;
    private readonly IThemeCatalog _themes;

    public AppearanceController(IShopService shops, IThemeCatalog themes)
    {
        _shops = shops;
        _themes = themes;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var (shop, access) = await ResolveAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });
        if (access?.CanManageSettings != true) return Forbid();

        return View(Build(shop));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(string themeKey, CancellationToken ct)
    {
        var (shop, access) = await ResolveAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });
        if (access?.CanManageSettings != true) return Forbid();

        // Never trust a posted key: reject anything the catalogue does not define.
        if (!_themes.Contains(themeKey))
        {
            ModelState.AddModelError(string.Empty, "Choose one of the available themes.");
            return View(Build(shop));
        }

        var result = await _shops.UpdateSettingsAsync(shop.Id, s => s.ThemeKey = themeKey, ct);
        if (!result.Success)
        {
            ModelState.AddModelError(string.Empty, result.Message);
            return View(Build(shop));
        }

        var theme = _themes.Get(themeKey);
        TempData["Message"] = $"'{theme.Name}' is now applied to '{shop.Name}'.";
        TempData["MessageType"] = "success";
        return RedirectToAction(nameof(Index));
    }

    private ShopAppearanceViewModel Build(Shop shop) => new()
    {
        Shop = shop,
        Themes = _themes.All,
        SelectedKey = _themes.Get(shop.ThemeKey).Key
    };

    private async Task<(Shop? Shop, ShopAccess? Access)> ResolveAsync(CancellationToken ct)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return (null, null);

        var shop = await _shops.GetPrimaryShopForUserAsync(userId, ct);
        if (shop is null) return (null, null);

        return (shop, await _shops.ResolveAccessAsync(userId, shop.Id, ct));
    }
}

/// <summary>Backing model for the theme picker.</summary>
public sealed class ShopAppearanceViewModel
{
    public required Shop Shop { get; init; }
    public required IReadOnlyList<ShopTheme> Themes { get; init; }
    public string? SelectedKey { get; init; }
}
