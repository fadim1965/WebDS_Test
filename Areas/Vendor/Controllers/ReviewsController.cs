using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebDS_Test.Areas.Admin.Models;
using WebDS_Test.Domain;

namespace WebDS_Test.Areas.Vendor.Controllers;

/// <summary>
/// The seller's view of its own reviews: what shoppers are saying, and the public reply box. A
/// reply is the only thing a seller may change about a review — the rating and the words are the
/// customer's.
/// </summary>
[Area("Vendor")]
[Authorize(Policy = "VendorOnly")]
public class ReviewsController : Controller
{
    private readonly IReviewService _reviews;
    private readonly IShopService _shops;

    public ReviewsController(IReviewService reviews, IShopService shops)
    {
        _reviews = reviews;
        _shops = shops;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        int? stars = null,
        ReviewSort sort = ReviewSort.Recent,
        int page = 1,
        CancellationToken ct = default)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null)
        {
            return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });
        }

        ViewData["Title"] = "Reviews";

        return View(new VendorReviewListViewModel
        {
            Reviews = await _reviews.GetForShopAsync(shop.Id, new ReviewQuery
            {
                Stars = stars is >= 1 and <= 5 ? stars : null,
                Sort = sort,
                Page = page,
                PageSize = 10
            }, ct),
            Rating = await _reviews.GetShopRatingAsync(shop.Id, ct),
            Stars = stars is >= 1 and <= 5 ? stars : null,
            Sort = sort,
            ShopName = shop.Name
        });
    }

    /// <summary>Records the seller's reply. An empty reply clears the one already there.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reply(
        int reviewId,
        string? reply,
        int? stars = null,
        ReviewSort sort = ReviewSort.Recent,
        int page = 1,
        CancellationToken ct = default)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null)
        {
            return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });
        }

        var result = await _reviews.ReplyAsync(reviewId, shop.Id, reply, ct);
        Flash(result.Message, result.Success);

        return RedirectToAction(nameof(Index), new { stars, sort, page });
    }

    private async Task<Shop?> ResolveShopAsync(CancellationToken ct)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return userId is null ? null : await _shops.GetPrimaryShopForUserAsync(userId, ct);
    }

    private void Flash(string message, bool success = true)
    {
        TempData["Message"] = message;
        TempData["MessageType"] = success ? "success" : "danger";
    }
}
