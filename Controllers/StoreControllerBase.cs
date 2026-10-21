using Microsoft.AspNetCore.Mvc;
using WebDS_Test.Domain;

namespace WebDS_Test.Controllers;

/// <summary>
/// Shared services for storefront pages: store settings for the chrome and the header
/// cart badge count. Admin controllers derive from <c>AdminControllerBase</c> instead.
/// </summary>
public abstract class StoreControllerBase : Controller
{
    protected StoreControllerBase(ICatalogService catalog, IOptions<StoreSettings> settings, ICartService cart)
    {
        Catalog = catalog;
        Settings = settings.Value;
        CartService = cart;
    }

    protected ICatalogService Catalog { get; }
    protected StoreSettings Settings { get; }
    protected ICartService CartService { get; }

    /// <summary>Item count shown on the header cart badge, recalculated on every storefront render.</summary>
    protected async Task LoadCartBadgeAsync(CancellationToken ct = default) =>
        ViewData["CartCount"] = await CartService.GetItemCountAsync(ct);

    /// <summary>Copies TempData flash state into the view bag used by <c>_Layout</c>.</summary>
    protected void Flash(string? message = null, bool success = true)
    {
        if (message is not null)
        {
            TempData["Message"] = message;
            TempData["MessageType"] = success ? "success" : "danger";
        }
    }
}
