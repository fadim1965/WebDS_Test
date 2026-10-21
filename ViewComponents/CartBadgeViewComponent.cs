using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebDS_Test.Domain;

namespace WebDS_Test.ViewComponents;

/// <summary>
/// Renders the header cart pill. Implemented as a view component so the badge count is never
/// stale in a cached layout, and so the layout does not need the cart in its model.
/// </summary>
public sealed class CartBadgeViewComponent : ViewComponent
{
    private readonly ICartService _cart;
    private readonly StoreSettings _settings;

    public CartBadgeViewComponent(ICartService cart, IOptions<StoreSettings> settings)
    {
        _cart = cart;
        _settings = settings.Value;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var cart = await _cart.GetCartAsync();
        var count = cart?.TotalQuantity ?? 0;

        return View(new CartBadgeModel(count, cart?.Subtotal ?? 0m, _settings.CurrencySymbol));
    }
}

public sealed record CartBadgeModel(int Count, decimal Subtotal, string CurrencySymbol);
