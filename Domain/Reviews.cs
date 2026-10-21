using System.ComponentModel.DataAnnotations;

namespace WebDS_Test.Domain;

/// <summary>
/// Editorial state of a review. A review is written by a shopper, published immediately, and may
/// later be rejected by a moderator or hidden by its author.
/// </summary>
public enum ReviewStatus
{
    /// <summary>Written but withheld from the storefront pending moderation.</summary>
    Pending = 0,
    /// <summary>Visible on the product page and counted in the rating.</summary>
    Published = 1,
    /// <summary>Rejected by a moderator. Kept for the audit trail, never rendered.</summary>
    Rejected = 2
}

public static class ReviewStatusExtensions
{
    /// <summary>Human label used by badges in the admin moderation queue.</summary>
    public static string ToDisplay(this ReviewStatus status) => status switch
    {
        ReviewStatus.Pending => "Pending",
        ReviewStatus.Published => "Published",
        ReviewStatus.Rejected => "Rejected",
        _ => status.ToString()
    };

    /// <summary>Bootstrap contextual colour for the label above.</summary>
    public static string ToCssClass(this ReviewStatus status) => status switch
    {
        ReviewStatus.Pending => "text-bg-warning",
        ReviewStatus.Published => "text-bg-success",
        ReviewStatus.Rejected => "text-bg-secondary",
        _ => "text-bg-secondary"
    };
}

/// <summary>
/// A shopper's rating and review of a product — the unit of trust on a marketplace.
/// </summary>
/// <remarks>
/// <para>
/// Two decisions shape this entity. First, the shop is denormalised onto the review
/// (<see cref="ShopId"/>), because a seller's reputation is the average of the reviews of
/// everything it sells and that aggregate must be computable without walking the catalogue.
/// Second, <see cref="ReviewerName"/> is snapshotted: a review is a historical statement, so later
/// changes to an account's display name must not rewrite what a shopper appeared to have said.
/// </para>
/// <para>
/// <see cref="UserId"/> is nullable and set to null when the account is deleted
/// (<c>OnDelete(SetNull)</c>), which is what lets reviews outlive the accounts that wrote them
/// while still being attributable to a shopper for the "one review each" rule.
/// </para>
/// </remarks>
public class Review
{
    public int Id { get; set; }

    public int ProductId { get; set; }
    public Product? Product { get; set; }

    /// <summary>Owning shop of the reviewed product, denormalised for seller rating aggregates.</summary>
    public int ShopId { get; set; }
    public Shop? Shop { get; set; }

    /// <summary>The author, or null once the account has been deleted.</summary>
    public string? UserId { get; set; }
    public ApplicationUser? User { get; set; }

    /// <summary>Display name as it stood when the review was written.</summary>
    [Required, StringLength(120)]
    public string ReviewerName { get; set; } = string.Empty;

    /// <summary>Whole stars, 1–5. Range-checked in <c>ReviewService</c> rather than by the schema.</summary>
    public int Rating { get; set; }

    [StringLength(140)]
    public string? Title { get; set; }

    [StringLength(4000)]
    public string? Body { get; set; }

    /// <summary>
    /// True when the author has an order containing this product. This is what the "Verified
    /// purchase" badge means, and it is set from order history at write time — never trusted from
    /// the form.
    /// </summary>
    public bool IsVerifiedPurchase { get; set; }

    public ReviewStatus Status { get; set; } = ReviewStatus.Published;

    public int HelpfulCount { get; set; }
    public int NotHelpfulCount { get; set; }

    /// <summary>The seller's public answer. One per review, edited in place.</summary>
    [StringLength(2000)]
    public string? VendorReply { get; set; }
    public DateTime? VendorReplyUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedUtc { get; set; }
    public DateTime? UpdatedUtc { get; set; }

    public ICollection<ReviewVote> Votes { get; set; } = new List<ReviewVote>();

    /// <summary>True when the review carries any prose, as opposed to only a star rating.</summary>
    public bool HasComment => !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Body);

    public bool HasVendorReply => !string.IsNullOrWhiteSpace(VendorReply);
}

/// <summary>
/// A shopper's "was this helpful?" vote on a review. One row per reviewer per review, so the
/// unique index is the guard against ballot stuffing.
/// </summary>
public class ReviewVote
{
    public int Id { get; set; }

    public int ReviewId { get; set; }
    public Review? Review { get; set; }

    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

    /// <summary>True for "helpful", false for "not helpful".</summary>
    public bool IsHelpful { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Review policy, bound from the <c>Reviews</c> configuration section. Keeps business thresholds —
/// whether reviews auto-publish and what qualifies a seller badge — out of the code.
/// </summary>
public sealed class ReviewSettings
{
    public const string SectionName = "Reviews";

    /// <summary>
    /// When true a submitted review appears immediately; when false it waits in the moderation
    /// queue. Marketplaces normally auto-publish and moderate reactively, which is the default.
    /// </summary>
    public bool AutoPublish { get; set; } = true;

    /// <summary>Minimum length of a written comment. A five-star rating with no words is fine.</summary>
    public int MinBodyLength { get; set; } = 10;

    /// <summary>Maximum characters accepted for a review body, mirroring the column length.</summary>
    public int MaxBodyLength { get; set; } = 4000;

    /// <summary>How many reviews are shown per page on the product page and in the admin queue.</summary>
    public int PageSize { get; set; } = 5;

    /// <summary>Reviews needed before a seller can earn the top badge.</summary>
    public int TopRatedMinCount { get; set; } = 15;

    public double TopRatedMinAverage { get; set; } = 4.5;

    /// <summary>A second, easier badge for a younger shop with a strong record.</summary>
    public int StarSellerMinCount { get; set; } = 5;

    public double StarSellerMinAverage { get; set; } = 4.2;
}
