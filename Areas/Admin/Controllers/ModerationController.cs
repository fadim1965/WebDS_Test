using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebDS_Test.Domain;
using WebDS_Test.Services;

namespace WebDS_Test.Areas.Admin.Controllers;

/// <summary>
/// Reviews the listings vendors have submitted, and can pull a live listing back if it should not
/// be on sale.
/// </summary>
/// <remarks>
/// This is the same shape as the shop approval queue: a vendor cannot put a listing on sale by
/// itself, and every decision is written to the audit trail and mailed to the vendor.
/// </remarks>
[Area("Admin")]
public class ModerationController : AdminControllerBase
{
    private readonly IListingModerationService _moderation;
    private readonly IAuditTrail _audit;

    public ModerationController(
        ApplicationDbContext db,
        IOptions<StoreSettings> settings,
        IListingModerationService moderation,
        IAuditTrail audit)
        : base(db, settings)
    {
        _moderation = moderation;
        _audit = audit;
    }

    /// <summary>The queue. The state filter is a word, not the enum's number, so links stay readable.</summary>
    public async Task<IActionResult> Index(string? state, string? q, CancellationToken ct)
    {
        var rows = await _moderation.GetQueueAsync(ParseState(state), q, ct);

        ViewData["Title"] = "Listing moderation";
        ViewData["State"] = state ?? string.Empty;
        ViewData["Search"] = q ?? string.Empty;
        ViewData["Pending"] = await _moderation.CountPendingAsync(ct);

        return View(rows);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, CancellationToken ct)
    {
        var result = await _moderation.ApproveAsync(id, ct);
        Flash(result.Message, result.Success);

        if (result.Success && result.Product is { } product)
        {
            await _audit.RecordAsync(CurrentUserName, "listing.approved", "Product", id,
                $"Approved the listing '{product.Name}' from {product.Shop?.Name ?? "a shop"}.");
        }

        return RedirectToAction(nameof(Index), new { state = "pending" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id, string? reason, CancellationToken ct)
    {
        var result = await _moderation.RejectAsync(id, reason ?? string.Empty, ct);
        Flash(result.Message, result.Success);

        if (result.Success && result.Product is { } product)
        {
            await _audit.RecordAsync(CurrentUserName, "listing.rejected", "Product", id,
                $"Rejected the listing '{product.Name}': {product.ModerationReason}");
        }

        return RedirectToAction(nameof(Index), new { state = "pending" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(int id, string? reason, CancellationToken ct)
    {
        var result = await _moderation.WithdrawAsync(id, reason, ct);
        Flash(result.Message, result.Success);

        if (result.Success && result.Product is { } product)
        {
            await _audit.RecordAsync(CurrentUserName, "listing.withdrawn", "Product", id,
                $"Withdrew the listing '{product.Name}' from the catalogue.");
        }

        return RedirectToAction(nameof(Index), new { state = "published" });
    }

    /// <summary>Maps the filter word on the query string to a state. An unknown word shows everything.</summary>
    private static ListingStatus? ParseState(string? state) => state?.ToLowerInvariant() switch
    {
        "pending" => ListingStatus.PendingReview,
        "draft" => ListingStatus.Draft,
        "published" => ListingStatus.Published,
        "rejected" => ListingStatus.Rejected,
        _ => null
    };
}
