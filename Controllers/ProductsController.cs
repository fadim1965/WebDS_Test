using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Models;

namespace WebDS_Test.Controllers;

/// <summary>Product detail page and the wishlist toggle.</summary>
public class ProductsController : StoreControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IReviewService _reviews;
    private readonly ReviewSettings _reviewSettings;
    private readonly ILogger<ProductsController> _logger;

    public ProductsController(
        ICatalogService catalog,
        IOptions<StoreSettings> settings,
        ICartService cart,
        ApplicationDbContext db,
        IReviewService reviews,
        IOptions<ReviewSettings> reviewSettings,
        ILogger<ProductsController> logger)
        : base(catalog, settings, cart)
    {
        _db = db;
        _reviews = reviews;
        _reviewSettings = reviewSettings.Value;
        _logger = logger;
    }

    [HttpGet("product/{slug}")]
    public async Task<IActionResult> Details(
        string slug,
        int? stars = null,
        ReviewSort reviewSort = ReviewSort.Recent,
        int reviewPage = 1,
        CancellationToken ct = default)
    {
        await LoadCartBadgeAsync(ct);

        var product = await Catalog.GetProductBySlugAsync(slug, null, ct);
        if (product is null)
        {
            // Distinguish "never existed" from "soft-deleted" so the shopper gets an explanation
            // instead of a dead 404.
            var withdrawn = await _db.Products.AsNoTracking()
                .AnyAsync(p => p.Slug == slug && p.IsDeleted, ct);

            if (withdrawn)
            {
                Flash("That product is no longer available.", success: false);
                return RedirectToAction("Index", "Catalog");
            }

            return NotFound();
        }

        var userId = User.Identity?.IsAuthenticated == true
            ? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            : null;

        var isInWishlist = false;
        if (userId is not null)
        {
            isInWishlist = await _db.WishlistItems.AnyAsync(w => w.UserId == userId && w.ProductId == product.Id, ct);
        }

        var categories = await Catalog.GetCategoryTreeAsync(null, ct);

        // The review list is filtered and paged through the query string, exactly like the
        // catalogue, so a given view of the reviews is bookmarkable.
        var reviewQuery = new ReviewQuery
        {
            Stars = stars is >= 1 and <= 5 ? stars : null,
            Sort = reviewSort,
            Page = Math.Max(1, reviewPage),
            PageSize = _reviewSettings.PageSize
        };

        var summary = await _reviews.GetSummaryAsync(product.Id, ct);
        var reviewList = await _reviews.GetForProductAsync(product.Id, reviewQuery, ct);

        var myReview = userId is null ? null : await _reviews.GetMineAsync(product.Id, userId, ct);
        var canReview = userId is not null && await _reviews.CanReviewAsync(product.Id, userId, ct);
        var myVotes = userId is null
            ? new Dictionary<int, bool>()
            : await _reviews.GetMyVotesAsync(reviewList.Items.Select(r => r.Id), userId, ct);

        var model = new ProductDetailViewModel
        {
            Product = product,
            // In-stock variants first, then by price, so the default selection is buyable.
            Variants = product.Variants
                .Where(v => v.IsActive)
                .OrderByDescending(v => v.StockQuantity > 0)
                .ThenBy(v => v.Price)
                .ToList(),
            Images = product.Images.OrderByDescending(i => i.IsPrimary).ThenBy(i => i.DisplayOrder).ToList(),
            Related = await Catalog.GetRelatedAsync(product, 4, ct),
            IsInWishlist = isInWishlist,
            Breadcrumb = BuildBreadcrumb(categories, product.Category),
            ReviewSummary = summary,
            Reviews = reviewList,
            ReviewQuery = reviewQuery,
            MyVotes = myVotes,
            CanReview = canReview,
            MyReview = myReview,
            IsSignedIn = userId is not null
        };

        ViewData["Title"] = product.Name;
        return View(model);
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleWishlist(int productId, string? returnSlug, CancellationToken ct)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null)
        {
            return Challenge();
        }

        var existing = await _db.WishlistItems
            .FirstOrDefaultAsync(w => w.UserId == userId && w.ProductId == productId, ct);

        if (existing is not null)
        {
            _db.WishlistItems.Remove(existing);
            await _db.SaveChangesAsync(ct);
            Flash("Removed from your wishlist.");
        }
        else
        {
            var productExists = await _db.Products.AnyAsync(p => p.Id == productId, ct);
            if (!productExists)
            {
                Flash("That product is no longer available.", success: false);
                return RedirectToAction("Index", "Catalog");
            }

            _db.WishlistItems.Add(new WishlistItem { UserId = userId, ProductId = productId });
            await _db.SaveChangesAsync(ct);
            Flash("Saved to your wishlist.");
        }

        return returnSlug is not null
            ? RedirectToAction(nameof(Details), new { slug = returnSlug })
            : RedirectToAction("Index", "Account");
    }

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
