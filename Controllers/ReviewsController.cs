using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using WebDS_Test.Domain;
using WebDS_Test.Models;

namespace WebDS_Test.Controllers;

/// <summary>
/// Write-side handling of reviews: submit, vote and delete. Reading happens on the product page, so
/// every action here ends in a redirect back to it with a flash message — the POST-redirect-GET
/// pattern the rest of the storefront uses.
/// </summary>
[Authorize]
public class ReviewsController : StoreControllerBase
{
    private readonly IReviewService _reviews;

    public ReviewsController(
        ICatalogService catalog,
        IOptions<StoreSettings> settings,
        ICartService cart,
        IReviewService reviews)
        : base(catalog, settings, cart)
    {
        _reviews = reviews;
    }

    /// <summary>Creates or updates the signed-in shopper's review of a product.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitPolicies.Review)]
    public async Task<IActionResult> Create(ReviewFormModel form, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null)
        {
            return Challenge();
        }

        if (!ModelState.IsValid)
        {
            Flash(FirstError(), success: false);
            return Back(form.ReturnSlug);
        }

        // The display name is what the cookie carries; the service snapshots it onto the review.
        var reviewerName = User.Identity?.Name ?? "Shopper";

        var result = await _reviews.SubmitAsync(
            form.ProductId, userId, reviewerName, form.Rating, form.Title, form.Body, ct);

        Flash(result.Message, success: result.Success);
        return Back(form.ReturnSlug);
    }

    /// <summary>Records or retracts the shopper's "was this helpful?" vote.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitPolicies.Review)]
    public async Task<IActionResult> Vote(
        int reviewId,
        bool helpful,
        string? returnSlug,
        int? stars = null,
        ReviewSort reviewSort = ReviewSort.Recent,
        int reviewPage = 1,
        CancellationToken ct = default)
    {
        var userId = CurrentUserId();
        if (userId is null)
        {
            return Challenge();
        }

        var result = await _reviews.VoteAsync(reviewId, userId, helpful, ct);

        // Voting is quiet on success — the tally updates in place on the reloaded page.
        if (!result.Success)
        {
            Flash(result.Message, success: false);
        }

        return Back(returnSlug, stars, reviewSort, reviewPage, $"review-{reviewId}");
    }

    /// <summary>Deletes a review. Only its author, or an administrator, may do so.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int reviewId, string? returnSlug, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null)
        {
            return Challenge();
        }

        var result = await _reviews.DeleteAsync(reviewId, userId, User.IsInRole(AccountService.AdminRole), ct);
        Flash(result.Message, success: result.Success);
        return Back(returnSlug);
    }

    private string? CurrentUserId() =>
        User.Identity?.IsAuthenticated == true
            ? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            : null;

    /// <summary>
    /// Sends the shopper back to the product page with the review filter state preserved, so a vote
    /// or a failed submit does not reset which page of reviews they were reading.
    /// </summary>
    private IActionResult Back(
        string? slug,
        int? stars = null,
        ReviewSort reviewSort = ReviewSort.Recent,
        int reviewPage = 1,
        string fragment = "reviews")
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return RedirectToAction("Index", "Catalog");
        }

        return RedirectToAction(
            nameof(ProductsController.Details),
            "Products",
            new { slug, stars, reviewSort, reviewPage },
            fragment);
    }

    /// <summary>The first validation message, so the flash banner explains what went wrong.</summary>
    private string FirstError() =>
        ModelState.Values
            .SelectMany(v => v.Errors)
            .Select(e => e.ErrorMessage)
            .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))
        ?? "Please check the review and try again.";
}
