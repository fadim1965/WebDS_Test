using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebDS_Test.Domain;
using WebDS_Test.Models;

namespace WebDS_Test.Controllers;

/// <summary>Product browsing: category, search, filter, sort and paging.</summary>
public class CatalogController : StoreControllerBase
{
    public CatalogController(ICatalogService catalog, IOptions<StoreSettings> settings, ICartService cart)
        : base(catalog, settings, cart)
    {
    }

    /// <summary>
    /// Browse with filters carried entirely in the query string, so every result set is
    /// bookmarkable and shareable.
    /// </summary>
    public async Task<IActionResult> Index(
        string? q = null,
        string? category = null,
        decimal? minPrice = null,
        decimal? maxPrice = null,
        string? brand = null,
        bool inStock = false,
        int? minRating = null,
        ProductSort sort = ProductSort.Newest,
        int page = 1,
        CancellationToken ct = default)
    {
        await LoadCartBadgeAsync(ct);

        // Guard against an inverted price range rather than returning zero results.
        if (minPrice is { } low && maxPrice is { } high && low > high)
        {
            (minPrice, maxPrice) = (high, low);
        }

        // Rating filters below 2 stars are not offered, so anything out of range is dropped rather
        // than turned into a filter the shopper cannot see or clear.
        if (minRating is not (>= 2 and <= 5))
        {
            minRating = null;
        }

        // A search with no explicit sort is ranked by relevance, which is what shoppers expect and
        // what every large marketplace does. Choosing a sort overrides it.
        if (!string.IsNullOrWhiteSpace(q) && sort == ProductSort.Newest)
        {
            sort = ProductSort.Relevance;
        }

        var query = new ProductQuery
        {
            Search = q,
            CategorySlug = category,
            MinPrice = minPrice,
            MaxPrice = maxPrice,
            Brand = brand,
            InStockOnly = inStock,
            MinRating = minRating,
            Sort = sort,
            Page = page,
            PageSize = Settings.ProductsPerPage
        };

        var results = await Catalog.BrowseAsync(query, ct);
        var categories = await Catalog.GetCategoryTreeAsync(null, ct);
        var currentCategory = string.IsNullOrWhiteSpace(category)
            ? null
            : await Catalog.GetCategoryBySlugAsync(category, null, ct);

        var model = new CatalogViewModel
        {
            Results = results,
            Query = query,
            Categories = categories,
            Brands = await Catalog.GetBrandsAsync(null, ct),
            PriceBounds = await Catalog.GetPriceBoundsAsync(null, ct),
            CurrentCategory = currentCategory,
            Breadcrumb = BuildBreadcrumb(categories, currentCategory)
        };

        ViewData["Title"] = model.Heading;
        return View(model);
    }

    /// <summary>
    /// Type-ahead suggestions for the header search box. Returns a small JSON payload so the
    /// client can render a dropdown without a full page load.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Suggest(string? q, CancellationToken ct)
    {
        var term = q?.Trim();

        // One character is noise; the shopper is still typing.
        if (string.IsNullOrWhiteSpace(term) || term.Length < 2)
        {
            return Json(new { products = Array.Empty<object>(), categories = Array.Empty<object>() });
        }

        var results = await Catalog.BrowseAsync(new ProductQuery
        {
            Search = term,
            Sort = ProductSort.Relevance,
            Page = 1,
            PageSize = 6
        }, ct);

        var categories = (await Catalog.GetActiveCategoriesAsync(null, ct))
            .Where(c => c.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .ToList();

        return Json(new
        {
            products = results.Items.Select(p => new
            {
                name = p.Name,
                slug = p.Slug,
                price = p.Price.ToString("0.00", CultureInfo.InvariantCulture),
                shop = p.ShopName
            }),
            categories = categories.Select(c => new { name = c.Name, slug = c.Slug })
        });
    }

    /// <summary>Walks up the tree from the current category to produce a breadcrumb trail.</summary>
    private static IReadOnlyList<Category> BuildBreadcrumb(IReadOnlyList<Category> roots, Category? current)
    {
        if (current is null)
        {
            return Array.Empty<Category>();
        }

        var path = new List<Category>();
        var cursor = current;
        var guard = 0;

        while (cursor is not null && guard++ < 16)
        {
            path.Insert(0, cursor);

            if (cursor.ParentId is null)
            {
                break;
            }

            cursor = FindById(roots, cursor.ParentId.Value);
        }

        return path;
    }

    private static Category? FindById(IReadOnlyList<Category> nodes, int id)
    {
        foreach (var node in nodes)
        {
            if (node.Id == id)
            {
                return node;
            }

            var found = FindById(node.Children.ToList(), id);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
