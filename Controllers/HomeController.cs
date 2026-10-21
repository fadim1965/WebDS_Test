using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using WebDS_Test.Models;
using Microsoft.Extensions.Options;
using WebDS_Test.Domain;

namespace WebDS_Test.Controllers;

/// <summary>Storefront home page and static/error pages.</summary>
public class HomeController : StoreControllerBase
{
    private readonly ILogger<HomeController> _logger;

    public HomeController(
        ICatalogService catalog,
        IOptions<StoreSettings> settings,
        ICartService cart,
        ILogger<HomeController> logger)
        : base(catalog, settings, cart)
    {
        _logger = logger;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        await LoadCartBadgeAsync(ct);

        ViewData["Title"] = "Home";

        var model = new HomeViewModel
        {
            Featured = await Catalog.GetFeaturedAsync(8, null, ct),
            BestSellers = await Catalog.GetBestSellersAsync(4, null, ct),
            Categories = await Catalog.GetActiveCategoriesAsync(null, ct)
        };

        return View(model);
    }

    public async Task<IActionResult> Privacy(CancellationToken ct)
    {
        await LoadCartBadgeAsync(ct);
        ViewData["Title"] = "Privacy";
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> Error(CancellationToken ct)
    {
        await LoadCartBadgeAsync(ct);
        ViewData["Title"] = "Something went wrong";
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }

    /// <summary>Friendly 404 reached through <c>UseStatusCodePagesWithReExecute</c>.</summary>
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> NotFound(int? code, CancellationToken ct)
    {
        await LoadCartBadgeAsync(ct);

        ViewData["Title"] = "Not found";
        Response.StatusCode = code ?? StatusCodes.Status404NotFound;

        var model = new NotFoundViewModel
        {
            StatusCode = code ?? StatusCodes.Status404NotFound,
            Suggestions = await Catalog.GetFeaturedAsync(4, null, ct)
        };

        return View(model);
    }
}

/// <summary>Backing model for the home page.</summary>
public sealed class HomeViewModel
{
    public IReadOnlyList<ProductCard> Featured { get; init; } = Array.Empty<ProductCard>();
    public IReadOnlyList<ProductCard> BestSellers { get; init; } = Array.Empty<ProductCard>();
    public IReadOnlyList<Category> Categories { get; init; } = Array.Empty<Category>();
}

/// <summary>Backing model for the 404 page, which suggests products instead of a dead end.</summary>
public sealed class NotFoundViewModel
{
    public int StatusCode { get; init; } = 404;
    public IReadOnlyList<ProductCard> Suggestions { get; init; } = Array.Empty<ProductCard>();
}
