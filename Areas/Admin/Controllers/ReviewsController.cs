using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebDS_Test.Areas.Admin.Models;
using WebDS_Test.Domain;

namespace WebDS_Test.Areas.Admin.Controllers;

/// <summary>
/// Review moderation. Reviews publish immediately by default, so this queue is reactive: it exists
/// to catch the small minority that need hiding, rather than to bottleneck every review.
/// </summary>
public class ReviewsController : AdminControllerBase
{
    private readonly IReviewService _reviews;

    public ReviewsController(
        ApplicationDbContext db,
        IOptions<StoreSettings> settings,
        IReviewService reviews)
        : base(db, settings)
    {
        _reviews = reviews;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        ReviewStatus? status = null,
        int? rating = null,
        int page = 1,
        CancellationToken ct = default)
    {
        ViewData["Title"] = "Reviews";

        // Anything outside 1–5 would silently return nothing, so it is dropped as a filter.
        if (rating is not (>= 1 and <= 5))
        {
            rating = null;
        }

        return View(new AdminReviewListViewModel
        {
            Reviews = await _reviews.GetModerationAsync(status, rating, page, ct),
            Counts = await _reviews.GetModerationCountsAsync(ct),
            Status = status,
            Rating = rating
        });
    }

    /// <summary>Publishes, hides or returns a review to the queue.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(
        int id,
        ReviewStatus status,
        ReviewStatus? filter = null,
        int? rating = null,
        int page = 1,
        CancellationToken ct = default)
    {
        var result = await _reviews.SetStatusAsync(id, status, ct);
        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Index), new { status = filter, rating, page });
    }

    /// <summary>Removes a review outright — used for spam, not for ordinary criticism.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(
        int id,
        ReviewStatus? filter = null,
        int? rating = null,
        int page = 1,
        CancellationToken ct = default)
    {
        var result = await _reviews.DeleteAsync(id, null, isAdmin: true, ct);
        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Index), new { status = filter, rating, page });
    }
}
