using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebDS_Test.Domain;

namespace WebDS_Test.Services;

/// <summary>
/// Ratings and reviews: the storefront's trust surface. Everything a shopper can do to a review —
/// write, edit, vote, delete — is funnelled through here so the rules (who may review, one review
/// each, what counts as a verified purchase) live in one place.
/// </summary>
public interface IReviewService
{
    /// <summary>The rating headline for a product: average, count and per-star distribution.</summary>
    Task<ReviewSummary> GetSummaryAsync(int productId, CancellationToken ct = default);

    /// <summary>A page of a product's published reviews, filtered and sorted.</summary>
    Task<PagedResult<Review>> GetForProductAsync(int productId, ReviewQuery query, CancellationToken ct = default);

    /// <summary>A page of reviews written against a shop's products, for the vendor area.</summary>
    Task<PagedResult<Review>> GetForShopAsync(int shopId, ReviewQuery query, CancellationToken ct = default);

    /// <summary>The signed-in shopper's own review of a product, if they have written one.</summary>
    Task<Review?> GetMineAsync(int productId, string userId, CancellationToken ct = default);

    /// <summary>
    /// True when the shopper has bought the product (an order that was at least paid). This is the
    /// gate on writing a review and the source of the "Verified purchase" badge.
    /// </summary>
    Task<bool> CanReviewAsync(int productId, string userId, CancellationToken ct = default);

    Task<ReviewResult> SubmitAsync(
        int productId, string userId, string reviewerName,
        int rating, string? title, string? body, CancellationToken ct = default);

    /// <summary>Toggles the caller's "was this helpful?" vote and refreshes the tallies.</summary>
    Task<ReviewResult> VoteAsync(int reviewId, string userId, bool helpful, CancellationToken ct = default);

    /// <summary>Records the owning seller's public reply to a review.</summary>
    Task<ReviewResult> ReplyAsync(int reviewId, int shopId, string? reply, CancellationToken ct = default);

    /// <summary>Moves a review between the moderation states.</summary>
    Task<ReviewResult> SetStatusAsync(int reviewId, ReviewStatus status, CancellationToken ct = default);

    /// <summary>Deletes a review. Allowed for its author or an administrator.</summary>
    Task<ReviewResult> DeleteAsync(int reviewId, string? userId, bool isAdmin, CancellationToken ct = default);

    /// <summary>The moderation queue, newest first.</summary>
    Task<PagedResult<AdminReviewRow>> GetModerationAsync(
        ReviewStatus? status, int? rating, int page, CancellationToken ct = default);

    /// <summary>Counts per moderation state, for the admin queue's filter chips.</summary>
    Task<IReadOnlyDictionary<ReviewStatus, int>> GetModerationCountsAsync(CancellationToken ct = default);

    /// <summary>A seller's aggregate rating, computed from its products' published reviews.</summary>
    Task<ShopRating> GetShopRatingAsync(int shopId, CancellationToken ct = default);

    /// <summary>Aggregate ratings for many shops at once, for the shop directory.</summary>
    Task<IReadOnlyDictionary<int, ShopRating>> GetShopRatingsAsync(
        IEnumerable<int> shopIds, CancellationToken ct = default);

    /// <summary>The star value a shopper's own vote carried, if any, keyed by review id.</summary>
    Task<IReadOnlyDictionary<int, bool>> GetMyVotesAsync(
        IEnumerable<int> reviewIds, string userId, CancellationToken ct = default);
}

public sealed class ReviewService : IReviewService
{
    /// <summary>
    /// Order states that evidence a real purchase. A pending order has not been paid.
    /// Always wrapped in <c>EF.Constant</c> when used in a query: a captured collection would be
    /// translated to OPENJSON, which the SQL Server 2014 store cannot parse (see
    /// <c>CatalogService.BrowseAsync</c>).
    /// </summary>
    private static readonly OrderStatus[] ReviewableStatuses =
        { OrderStatus.Paid, OrderStatus.Shipped, OrderStatus.Delivered };

    private readonly ApplicationDbContext _db;
    private readonly ReviewSettings _settings;
    private readonly ILogger<ReviewService> _logger;

    public ReviewService(ApplicationDbContext db, IOptions<ReviewSettings> settings, ILogger<ReviewService> logger)
    {
        _db = db;
        _settings = settings.Value;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // Reads
    // ------------------------------------------------------------------

    public async Task<ReviewSummary> GetSummaryAsync(int productId, CancellationToken ct = default)
    {
        var distribution = await Published()
            .Where(r => r.ProductId == productId)
            .GroupBy(r => r.Rating)
            .Select(g => new { Rating = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var total = distribution.Sum(d => d.Count);
        if (total == 0)
        {
            return ReviewSummary.Empty;
        }

        var verified = await Published()
            .CountAsync(r => r.ProductId == productId && r.IsVerifiedPurchase, ct);

        var buckets = new int[5];
        foreach (var row in distribution)
        {
            if (row.Rating is >= 1 and <= 5)
            {
                buckets[row.Rating - 1] = row.Count;
            }
        }

        // Weighted in memory: SQLite cannot average an int column through EF without a cast, and
        // the bucket counts are already in hand.
        var sum = buckets.Select((count, index) => count * (index + 1)).Sum();
        var average = Math.Round((double)sum / total, 2, MidpointRounding.AwayFromZero);

        return new ReviewSummary(average, total, verified, buckets);
    }

    public async Task<PagedResult<Review>> GetForProductAsync(
        int productId, ReviewQuery query, CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 50);

        var q = Published().Where(r => r.ProductId == productId);

        if (query.Stars is >= 1 and <= 5)
        {
            q = q.Where(r => r.Rating == query.Stars);
        }

        if (query.VerifiedOnly)
        {
            q = q.Where(r => r.IsVerifiedPurchase);
        }

        var total = await q.CountAsync(ct);
        page = Paging.ClampPage(page, pageSize, total);

        q = query.Sort switch
        {
            ReviewSort.MostHelpful => q.OrderByDescending(r => r.HelpfulCount).ThenByDescending(r => r.CreatedUtc),
            ReviewSort.HighestRating => q.OrderByDescending(r => r.Rating).ThenByDescending(r => r.CreatedUtc),
            ReviewSort.LowestRating => q.OrderBy(r => r.Rating).ThenByDescending(r => r.CreatedUtc),
            _ => q.OrderByDescending(r => r.CreatedUtc)
        };

        var items = await q.Skip(Paging.Skip(page, pageSize)).Take(pageSize).ToListAsync(ct);

        return new PagedResult<Review>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = total
        };
    }

    public async Task<PagedResult<Review>> GetForShopAsync(
        int shopId, ReviewQuery query, CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 50);

        IQueryable<Review> q = _db.Reviews.AsNoTracking()
            .Include(r => r.Product)
            .Where(r => r.ShopId == shopId && r.Status != ReviewStatus.Rejected);

        if (query.Stars is >= 1 and <= 5)
        {
            q = q.Where(r => r.Rating == query.Stars);
        }

        var total = await q.CountAsync(ct);
        page = Paging.ClampPage(page, pageSize, total);

        q = query.Sort switch
        {
            ReviewSort.MostHelpful => q.OrderByDescending(r => r.HelpfulCount).ThenByDescending(r => r.CreatedUtc),
            ReviewSort.HighestRating => q.OrderByDescending(r => r.Rating).ThenByDescending(r => r.CreatedUtc),
            ReviewSort.LowestRating => q.OrderBy(r => r.Rating).ThenByDescending(r => r.CreatedUtc),
            _ => q.OrderByDescending(r => r.CreatedUtc)
        };

        var items = await q.Skip(Paging.Skip(page, pageSize)).Take(pageSize).ToListAsync(ct);

        return new PagedResult<Review>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = total
        };
    }

    public Task<Review?> GetMineAsync(int productId, string userId, CancellationToken ct = default) =>
        _db.Reviews.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProductId == productId && r.UserId == userId, ct);

    public Task<bool> CanReviewAsync(int productId, string userId, CancellationToken ct = default) =>
        _db.OrderLines.AsNoTracking().AnyAsync(
            l => l.ProductId == productId &&
                 l.Order!.UserId == userId &&
                 EF.Constant(ReviewableStatuses).Contains(l.Order.Status),
            ct);

    public async Task<IReadOnlyDictionary<int, bool>> GetMyVotesAsync(
        IEnumerable<int> reviewIds, string userId, CancellationToken ct = default)
    {
        var ids = reviewIds.Distinct().ToArray();
        if (ids.Length == 0 || string.IsNullOrEmpty(userId))
        {
            return new Dictionary<int, bool>();
        }

        var votes = await _db.ReviewVotes.AsNoTracking()
            .Where(v => EF.Constant(ids).Contains(v.ReviewId) && v.UserId == userId)
            .Select(v => new { v.ReviewId, v.IsHelpful })
            .ToListAsync(ct);

        return votes.ToDictionary(v => v.ReviewId, v => v.IsHelpful);
    }

    // ------------------------------------------------------------------
    // Writes
    // ------------------------------------------------------------------

    public async Task<ReviewResult> SubmitAsync(
        int productId, string userId, string reviewerName,
        int rating, string? title, string? body, CancellationToken ct = default)
    {
        if (rating is < 1 or > 5)
        {
            return ReviewResult.Fail("Choose a rating between 1 and 5 stars.");
        }

        title = Clean(title);
        body = Clean(body);

        if (title is { Length: > 140 })
        {
            return ReviewResult.Fail("The review title is too long.");
        }

        if (body is { Length: > 0 } && body.Length < _settings.MinBodyLength)
        {
            return ReviewResult.Fail($"A written review needs at least {_settings.MinBodyLength} characters.");
        }

        if (body is { Length: > 0 } && body.Length > _settings.MaxBodyLength)
        {
            return ReviewResult.Fail($"Keep the review under {_settings.MaxBodyLength} characters.");
        }

        var product = await _db.Products
            .FirstOrDefaultAsync(p => p.Id == productId && !p.IsDeleted, ct);

        if (product is null || !product.IsPublished)
        {
            return ReviewResult.Fail("That product is no longer available to review.");
        }

        // The verified badge is derived from the order history in this request, never from the
        // form — a shopper cannot claim a purchase they did not make.
        var verified = await CanReviewAsync(productId, userId, ct);

        var existing = await _db.Reviews
            .FirstOrDefaultAsync(r => r.ProductId == productId && r.UserId == userId, ct);

        // A rejection is a moderation decision: an edit sends the review back to the queue rather
        // than letting a rewrite slip past it.
        var status = existing?.Status == ReviewStatus.Rejected
            ? ReviewStatus.Pending
            : _settings.AutoPublish ? ReviewStatus.Published : ReviewStatus.Pending;

        if (existing is null)
        {
            _db.Reviews.Add(new Review
            {
                ProductId = productId,
                ShopId = product.ShopId,
                UserId = userId,
                ReviewerName = string.IsNullOrWhiteSpace(reviewerName) ? "Shopper" : reviewerName.Trim(),
                Rating = rating,
                Title = title,
                Body = body,
                IsVerifiedPurchase = verified,
                Status = status,
                CreatedUtc = DateTime.UtcNow,
                PublishedUtc = status == ReviewStatus.Published ? DateTime.UtcNow : null
            });

            _logger.LogInformation("Review created for product {ProductId} with {Rating} stars (verified: {Verified})",
                productId, rating, verified);
        }
        else
        {
            existing.Rating = rating;
            existing.Title = title;
            existing.Body = body;
            existing.IsVerifiedPurchase = existing.IsVerifiedPurchase || verified;
            existing.Status = status;
            existing.UpdatedUtc = DateTime.UtcNow;
            if (status == ReviewStatus.Published)
            {
                existing.PublishedUtc ??= DateTime.UtcNow;
            }

            _logger.LogInformation("Review {ReviewId} updated for product {ProductId}", existing.Id, productId);
        }

        await _db.SaveChangesAsync(ct);

        return ReviewResult.Ok(status == ReviewStatus.Published
            ? "Thank you — your review is now live."
            : "Thank you — your review has been received and is awaiting moderation.");
    }

    public async Task<ReviewResult> VoteAsync(
        int reviewId, string userId, bool helpful, CancellationToken ct = default)
    {
        var review = await _db.Reviews.FirstOrDefaultAsync(r => r.Id == reviewId, ct);
        if (review is null || review.Status == ReviewStatus.Rejected)
        {
            return ReviewResult.Fail("That review is no longer available.");
        }

        if (review.UserId == userId)
        {
            return ReviewResult.Fail("You cannot vote on your own review.");
        }

        var vote = await _db.ReviewVotes
            .FirstOrDefaultAsync(v => v.ReviewId == reviewId && v.UserId == userId, ct);

        if (vote is null)
        {
            _db.ReviewVotes.Add(new ReviewVote
            {
                ReviewId = reviewId,
                UserId = userId,
                IsHelpful = helpful,
                CreatedUtc = DateTime.UtcNow
            });

            Bump(review, helpful, +1);
        }
        else if (vote.IsHelpful == helpful)
        {
            // Clicking the same button again retracts the vote.
            _db.ReviewVotes.Remove(vote);
            Bump(review, helpful, -1);
        }
        else
        {
            vote.IsHelpful = helpful;
            vote.CreatedUtc = DateTime.UtcNow;

            // The vote moved from one column to the other, so both tallies shift.
            Bump(review, helpful, +1);
            Bump(review, !helpful, -1);
        }

        await _db.SaveChangesAsync(ct);

        return ReviewResult.Ok("Thanks for the feedback.");
    }

    /// <summary>
    /// Adjusts a tally, never letting it fall below zero. The unique (review, user) index means a
    /// reviewer can hold at most one vote, so a running count cannot drift from the rows that
    /// justify it.
    /// </summary>
    private static void Bump(Review review, bool helpful, int delta)
    {
        if (helpful)
        {
            review.HelpfulCount = Math.Max(0, review.HelpfulCount + delta);
        }
        else
        {
            review.NotHelpfulCount = Math.Max(0, review.NotHelpfulCount + delta);
        }
    }

    public async Task<ReviewResult> ReplyAsync(
        int reviewId, int shopId, string? reply, CancellationToken ct = default)
    {
        reply = Clean(reply);

        var review = await _db.Reviews.FirstOrDefaultAsync(r => r.Id == reviewId, ct);
        if (review is null)
        {
            return ReviewResult.Fail("That review no longer exists.");
        }

        // A seller may only answer reviews of its own products.
        if (review.ShopId != shopId)
        {
            return ReviewResult.Fail("That review is not for one of your products.");
        }

        if (reply is { Length: > 2000 })
        {
            return ReviewResult.Fail("Keep the reply under 2000 characters.");
        }

        review.VendorReply = reply;
        review.VendorReplyUtc = reply is null ? null : DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return ReviewResult.Ok(reply is null ? "Reply removed." : "Your reply has been published.");
    }

    public async Task<ReviewResult> SetStatusAsync(
        int reviewId, ReviewStatus status, CancellationToken ct = default)
    {
        var review = await _db.Reviews.FirstOrDefaultAsync(r => r.Id == reviewId, ct);
        if (review is null)
        {
            return ReviewResult.Fail("That review no longer exists.");
        }

        review.Status = status;
        if (status == ReviewStatus.Published)
        {
            review.PublishedUtc ??= DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Review {ReviewId} moved to {Status} by moderation", reviewId, status);

        return ReviewResult.Ok(status switch
        {
            ReviewStatus.Published => "Review published.",
            ReviewStatus.Rejected => "Review rejected and hidden.",
            _ => "Review returned to the moderation queue."
        });
    }

    public async Task<ReviewResult> DeleteAsync(
        int reviewId, string? userId, bool isAdmin, CancellationToken ct = default)
    {
        var review = await _db.Reviews.FirstOrDefaultAsync(r => r.Id == reviewId, ct);
        if (review is null)
        {
            return ReviewResult.Fail("That review no longer exists.");
        }

        if (!isAdmin && (userId is null || review.UserId != userId))
        {
            return ReviewResult.Fail("You can only delete your own review.");
        }

        // Votes reference the review with ON DELETE CASCADE by convention, but the change tracker
        // will not cascade a set it has not loaded, so they are removed explicitly.
        var votes = await _db.ReviewVotes.Where(v => v.ReviewId == reviewId).ToListAsync(ct);
        _db.ReviewVotes.RemoveRange(votes);
        _db.Reviews.Remove(review);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Review {ReviewId} deleted by {Actor}", reviewId, isAdmin ? "an administrator" : "its author");

        return ReviewResult.Ok("Review deleted.");
    }

    // ------------------------------------------------------------------
    // Moderation
    // ------------------------------------------------------------------

    public async Task<PagedResult<AdminReviewRow>> GetModerationAsync(
        ReviewStatus? status, int? rating, int page, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        var pageSize = Math.Clamp(_settings.PageSize * 4, 5, 50);

        IQueryable<Review> q = _db.Reviews.AsNoTracking()
            .Include(r => r.Product)
                .ThenInclude(p => p!.Shop);

        if (status is { } s)
        {
            q = q.Where(r => r.Status == s);
        }

        if (rating is >= 1 and <= 5)
        {
            q = q.Where(r => r.Rating == rating);
        }

        var total = await q.CountAsync(ct);
        page = Paging.ClampPage(page, pageSize, total);

        var items = await q
            .OrderBy(r => r.Status == ReviewStatus.Pending ? 0 : 1)
            .ThenByDescending(r => r.CreatedUtc)
            .Skip(Paging.Skip(page, pageSize))
            .Take(pageSize)
            .Select(r => new AdminReviewRow
            {
                Id = r.Id,
                Rating = r.Rating,
                Title = r.Title,
                Body = r.Body,
                ReviewerName = r.ReviewerName,
                ProductName = r.Product != null ? r.Product.Name : "(deleted product)",
                ProductId = r.ProductId,
                ProductSlug = r.Product != null ? r.Product.Slug : string.Empty,
                ShopName = r.Product != null && r.Product.Shop != null ? r.Product.Shop.Name : string.Empty,
                IsVerifiedPurchase = r.IsVerifiedPurchase,
                HasVendorReply = r.VendorReply != null && r.VendorReply != "",
                Status = r.Status,
                CreatedUtc = r.CreatedUtc
            })
            .ToListAsync(ct);

        return new PagedResult<AdminReviewRow>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = total
        };
    }

    public async Task<IReadOnlyDictionary<ReviewStatus, int>> GetModerationCountsAsync(CancellationToken ct = default)
    {
        var counts = await _db.Reviews.AsNoTracking()
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return counts.ToDictionary(c => c.Status, c => c.Count);
    }

    // ------------------------------------------------------------------
    // Seller reputation
    // ------------------------------------------------------------------

    public async Task<ShopRating> GetShopRatingAsync(int shopId, CancellationToken ct = default)
    {
        var ratings = await GetShopRatingsAsync(new[] { shopId }, ct);
        return ratings.TryGetValue(shopId, out var rating) ? rating : ShopRating.Empty;
    }

    public async Task<IReadOnlyDictionary<int, ShopRating>> GetShopRatingsAsync(
        IEnumerable<int> shopIds, CancellationToken ct = default)
    {
        var ids = shopIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<int, ShopRating>();
        }

        // One grouped pass for the averages, one for the per-star distribution: two queries for the
        // whole shop directory, regardless of how many shops are on the page. EF.Constant keeps each
        // predicate a literal IN (…) rather than OPENJSON (see CatalogService.BrowseAsync).
        var averages = await Published()
            .Where(r => EF.Constant(ids).Contains(r.ShopId))
            .GroupBy(r => r.ShopId)
            .Select(g => new { ShopId = g.Key, Count = g.Count(), Sum = g.Sum(r => r.Rating) })
            .ToListAsync(ct);

        var distribution = await Published()
            .Where(r => EF.Constant(ids).Contains(r.ShopId))
            .GroupBy(r => new { r.ShopId, r.Rating })
            .Select(g => new { g.Key.ShopId, g.Key.Rating, Count = g.Count() })
            .ToListAsync(ct);

        var verified = await Published()
            .Where(r => EF.Constant(ids).Contains(r.ShopId) && r.IsVerifiedPurchase)
            .GroupBy(r => r.ShopId)
            .Select(g => new { ShopId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var result = new Dictionary<int, ShopRating>();

        foreach (var row in averages)
        {
            var buckets = new int[5];
            foreach (var d in distribution.Where(d => d.ShopId == row.ShopId))
            {
                if (d.Rating is >= 1 and <= 5)
                {
                    buckets[d.Rating - 1] = d.Count;
                }
            }

            var average = Math.Round((double)row.Sum / row.Count, 2, MidpointRounding.AwayFromZero);
            var verifiedCount = verified.FirstOrDefault(v => v.ShopId == row.ShopId)?.Count ?? 0;
            var summary = new ReviewSummary(average, row.Count, verifiedCount, buckets);

            result[row.ShopId] = new ShopRating(average, row.Count, summary);
        }

        return result;
    }

    // ------------------------------------------------------------------

    /// <summary>Published reviews only — the storefront never sees a pending or rejected row.</summary>
    private IQueryable<Review> Published() =>
        _db.Reviews.AsNoTracking().Where(r => r.Status == ReviewStatus.Published);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
