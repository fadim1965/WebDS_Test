using WebDS_Test.Domain;

namespace WebDS_Test.Services;

/// <summary>Outcome of a review operation, surfaced to the user as a flash message.</summary>
public sealed record ReviewResult(bool Success, string Message)
{
    public static ReviewResult Ok(string message) => new(true, message);
    public static ReviewResult Fail(string message) => new(false, message);
}

/// <summary>How the review list on a product page is ordered.</summary>
public enum ReviewSort
{
    /// <summary>Newest first — the default, because fresh feedback is what matters most.</summary>
    Recent = 0,
    MostHelpful = 1,
    HighestRating = 2,
    LowestRating = 3
}

public static class ReviewSortExtensions
{
    /// <summary>Human label for the review list's sort control.</summary>
    public static string ToDisplay(this ReviewSort sort) => sort switch
    {
        ReviewSort.Recent => "Most recent",
        ReviewSort.MostHelpful => "Most helpful",
        ReviewSort.HighestRating => "Highest rated",
        ReviewSort.LowestRating => "Lowest rated",
        _ => sort.ToString()
    };
}

/// <summary>Filter, sort and paging inputs for a review list.</summary>
public sealed class ReviewQuery
{
    /// <summary>Restricts to a single star value, for the "5 star only" facet.</summary>
    public int? Stars { get; set; }

    public bool VerifiedOnly { get; set; }

    /// <summary>Restricts to one moderation state. Null means every state (moderation view only).</summary>
    public ReviewStatus? Status { get; set; }

    public ReviewSort Sort { get; set; } = ReviewSort.Recent;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 5;
}

/// <summary>
/// The rating headline for a product: the average, the total, and the per-star distribution that
/// drives the histogram on the product page.
/// </summary>
public sealed record ReviewSummary(double Average, int Count, int VerifiedCount, IReadOnlyList<int> Distribution)
{
    public static readonly ReviewSummary Empty = new(0d, 0, 0, new int[5]);

    public bool HasRatings => Count > 0;

    /// <summary>Count of reviews carrying the given star value (1–5).</summary>
    public int ForStar(int star) => star is >= 1 and <= 5 ? Distribution[star - 1] : 0;

    /// <summary>Share of reviews awarded the given star value, as a whole percentage.</summary>
    public int PercentageForStar(int star) =>
        Count == 0 ? 0 : (int)Math.Round(ForStar(star) * 100d / Count, MidpointRounding.AwayFromZero);

    /// <summary>Share of reviews at four stars or above.</summary>
    public int PositivePercentage =>
        Count == 0 ? 0 : (int)Math.Round((ForStar(4) + ForStar(5)) * 100d / Count, MidpointRounding.AwayFromZero);

    /// <summary>The star value to highlight in a five-star strip, rounded to the nearest whole star.</summary>
    public int RoundedStars => (int)Math.Round(Average, MidpointRounding.AwayFromZero);
}

/// <summary>The rating a five-star strip renders, with optional metadata for a tooltip.</summary>
public readonly record struct StarRating(double Value, int Count)
{
    public static readonly StarRating None = new(0d, 0);
    public bool HasValue => Count > 0;
}

/// <summary>A seller's aggregate reputation, computed from the reviews of what it sells.</summary>
public sealed record ShopRating(double Average, int Count, ReviewSummary Summary)
{
    public static readonly ShopRating Empty = new(0d, 0, ReviewSummary.Empty);

    public bool HasRatings => Count > 0;
    public int RoundedStars => (int)Math.Round(Average, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The badge the marketplace awards this seller. Thresholds are configuration, not code, so
    /// the policy can be tuned without a deployment — see <see cref="ReviewSettings"/>.
    /// </summary>
    public SellerBadge Badge(ReviewSettings settings)
    {
        if (!HasRatings)
        {
            return SellerBadge.None;
        }

        if (Count >= settings.TopRatedMinCount && Average >= settings.TopRatedMinAverage)
        {
            return SellerBadge.TopRated;
        }

        if (Count >= settings.StarSellerMinCount && Average >= settings.StarSellerMinAverage)
        {
            return SellerBadge.StarSeller;
        }

        return SellerBadge.None;
    }
}

/// <summary>A seller accolade shown next to a shop's name, mirroring Etsy's Star Seller badge.</summary>
public enum SellerBadge
{
    None = 0,
    StarSeller = 1,
    TopRated = 2
}

public static class SellerBadgeExtensions
{
    public static string ToDisplay(this SellerBadge badge) => badge switch
    {
        SellerBadge.TopRated => "Top rated seller",
        SellerBadge.StarSeller => "Star seller",
        _ => string.Empty
    };

    /// <summary>Bootstrap background class for the badge.</summary>
    public static string ToCssClass(this SellerBadge badge) => badge switch
    {
        SellerBadge.TopRated => "text-bg-warning",
        SellerBadge.StarSeller => "text-bg-info",
        _ => string.Empty
    };
}

/// <summary>A row in the admin moderation queue.</summary>
public sealed class AdminReviewRow
{
    public int Id { get; init; }
    public int Rating { get; init; }
    public string? Title { get; init; }
    public string? Body { get; init; }
    public string ReviewerName { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public int ProductId { get; init; }
    public string ProductSlug { get; init; } = string.Empty;
    public string ShopName { get; init; } = string.Empty;
    public bool IsVerifiedPurchase { get; init; }
    public bool HasVendorReply { get; init; }
    public ReviewStatus Status { get; init; }
    public DateTime CreatedUtc { get; init; }
}
