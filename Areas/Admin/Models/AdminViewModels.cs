using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using WebDS_Test.Domain;
using WebDS_Test.Services;
using WebDS_Test.Services.Email;

namespace WebDS_Test.Areas.Admin.Models;

/// <summary>One editable variant row inside the product editor.</summary>
public sealed class VariantInput
{
    /// <summary>Zero for a newly added variant.</summary>
    public int Id { get; set; }

    [Required(ErrorMessage = "Every variant needs a SKU.")]
    [StringLength(64)]
    [Display(Name = "SKU")]
    public string Sku { get; set; } = string.Empty;

    [StringLength(80)]
    [Display(Name = "Option")]
    public string Name { get; set; } = string.Empty;

    [Range(0.01, 1_000_000, ErrorMessage = "Price must be greater than zero.")]
    public decimal Price { get; set; }

    [Range(0, 1_000_000)]
    [Display(Name = "Was")]
    public decimal? CompareAtPrice { get; set; }

    [Range(0, 1_000_000)]
    [Display(Name = "Stock")]
    public int StockQuantity { get; set; }

    [Display(Name = "Active")]
    public bool IsActive { get; set; } = true;

    [Range(0, 10_000)]
    [Display(Name = "Low stock at")]
    public int LowStockThreshold { get; set; } = 5;

    /// <summary>Set when the administrator ticks the row for removal.</summary>
    public bool Delete { get; set; }
}

/// <summary>Backing model for the admin product editor.</summary>
public sealed class ProductEditViewModel
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Enter a product name.")]
    [StringLength(160)]
    public string Name { get; set; } = string.Empty;

    [StringLength(160)]
    [Display(Name = "URL slug")]
    public string? Slug { get; set; }

    [StringLength(200)]
    [Display(Name = "Short description")]
    public string? ShortDescription { get; set; }

    public string? Description { get; set; }

    [StringLength(120)]
    public string? Brand { get; set; }

    [Required(ErrorMessage = "Choose a category.")]
    [Display(Name = "Category")]
    public int CategoryId { get; set; }

    [Display(Name = "Published")]
    public bool IsPublished { get; set; } = true;

    /// <summary>
    /// Where the listing sits in the marketplace's review process. Shown on the edit screen; it is
    /// decided by the review queue, not by the form.
    /// </summary>
    public ListingStatus ModerationStatus { get; set; } = ListingStatus.Published;

    /// <summary>Why an administrator turned the listing down, when they did. Shown, not set by the form.</summary>
    public string? ModerationReason { get; set; }

    [Display(Name = "Featured")]
    public bool IsFeatured { get; set; }

    public List<VariantInput> Variants { get; set; } = new();

    /// <summary>
    /// One image URL or path per line; the first is treated as primary. Optional — a product may rely
    /// on uploaded files alone, or have no images at all and fall back to the placeholder.
    /// </summary>
    [Display(Name = "Images")]
    public string? ImageUrls { get; set; }

    /// <summary>
    /// Optional image files to store on save. Each is validated and written by the configured
    /// <see cref="IImageStore"/>, and its URL is appended to <see cref="ImageUrls"/>.
    /// </summary>
    [Display(Name = "Upload images")]
    public List<IFormFile> ImageFiles { get; set; } = new();

    public IReadOnlyList<Category> Categories { get; set; } = Array.Empty<Category>();
    public bool IsNew => Id == 0;
}

/// <summary>Backing model for the admin category editor.</summary>
public sealed class CategoryEditViewModel
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Enter a category name.")]
    [StringLength(80)]
    public string Name { get; set; } = string.Empty;

    [StringLength(80)]
    [Display(Name = "URL slug")]
    public string? Slug { get; set; }

    [StringLength(500)]
    public string? Description { get; set; }

    [Display(Name = "Parent category")]
    public int? ParentId { get; set; }

    [Display(Name = "Display order")]
    public int DisplayOrder { get; set; }

    [Display(Name = "Active")]
    public bool IsActive { get; set; } = true;

    public IReadOnlyList<Category> AllCategories { get; set; } = Array.Empty<Category>();
    public int ProductCount { get; set; }
    public bool IsNew => Id == 0;
}

/// <summary>Backing model for the inventory list.</summary>
public sealed class InventoryViewModel
{
    public IReadOnlyList<ProductVariant> Variants { get; set; } = Array.Empty<ProductVariant>();
    public IReadOnlyList<StockMovement> RecentMovements { get; set; } = Array.Empty<StockMovement>();
}

/// <summary>Backing model for the admin (and vendor) product list.</summary>
public sealed class AdminProductListViewModel
{
    public IReadOnlyList<AdminProductRow> Rows { get; init; } = Array.Empty<AdminProductRow>();
    public string? Search { get; init; }
    public int? CategoryId { get; init; }
    public string? State { get; init; }
    public IReadOnlyList<Category> Categories { get; init; } = Array.Empty<Category>();
}

/// <summary>Backing model for the admin order list.</summary>
public sealed class AdminOrderListViewModel
{
    public IReadOnlyList<Order> Orders { get; set; } = Array.Empty<Order>();
    public OrderStatus? Status { get; set; }
    public string? Search { get; set; }
    public int PendingCount { get; set; }
}

/// <summary>Backing model for the admin customer list.</summary>
public sealed class AdminCustomerListViewModel
{
    public IReadOnlyList<ApplicationUser> Customers { get; set; } = Array.Empty<ApplicationUser>();
    public string? Search { get; set; }
}

/// <summary>Backing model for the vendor's review workspace.</summary>
public sealed class VendorReviewListViewModel
{
    public PagedResult<Review> Reviews { get; init; } = new();

    /// <summary>The shop's aggregate rating, shown as a headline and used to award its badge.</summary>
    public ShopRating Rating { get; init; } = ShopRating.Empty;

    public int? Stars { get; init; }
    public ReviewSort Sort { get; init; } = ReviewSort.Recent;
    public string ShopName { get; init; } = string.Empty;
}

/// <summary>Backing model for the admin review moderation queue.</summary>
public sealed class AdminReviewListViewModel
{
    public PagedResult<AdminReviewRow> Reviews { get; init; } = new();

    /// <summary>How many reviews sit in each moderation state, for the filter chips.</summary>
    public IReadOnlyDictionary<ReviewStatus, int> Counts { get; init; } = new Dictionary<ReviewStatus, int>();

    public ReviewStatus? Status { get; init; }
    public int? Rating { get; init; }

    public int TotalCount => Counts.Values.Sum();

    public int CountFor(ReviewStatus status) => Counts.TryGetValue(status, out var count) ? count : 0;
}

/// <summary>Shared shape for admin pages that show a confirm/deny banner.</summary>
public abstract class AdminPageViewModel
{
    [TempData]
    public string? Message { get; set; }

    [TempData]
    public string? MessageType { get; set; }
}

/// <summary>Human labels for the email outbox, kept out of the views.</summary>
public static class EmailDisplay
{
    public static string Status(EmailStatus status) => status switch
    {
        EmailStatus.Queued => "Queued",
        EmailStatus.Sent => "Sent",
        EmailStatus.Failed => "Failed",
        EmailStatus.Skipped => "Skipped",
        _ => status.ToString()
    };

    public static string StatusCss(EmailStatus status) => status switch
    {
        EmailStatus.Queued => "text-bg-secondary",
        EmailStatus.Sent => "text-bg-success",
        EmailStatus.Failed => "text-bg-danger",
        EmailStatus.Skipped => "text-bg-light",
        _ => "text-bg-light"
    };

    public static string Kind(string kind) => kind switch
    {
        EmailKind.Welcome => "Welcome",
        EmailKind.OrderConfirmation => "Order confirmation",
        EmailKind.OrderShipped => "Shipping notice",
        EmailKind.OrderCancelled => "Cancellation",
        EmailKind.PasswordReset => "Password reset",
        EmailKind.Test => "Test",
        _ => kind
    };
}

/// <summary>One row in the admin email outbox.</summary>
public sealed class AdminEmailRow
{
    public int Id { get; init; }
    public string ToAddress { get; init; } = string.Empty;
    public string? ToName { get; init; }
    public string Subject { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public EmailStatus Status { get; init; }
    public int Attempts { get; init; }
    public string? LastError { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime? SentUtc { get; init; }
    public int? OrderId { get; init; }

    public string Recipient => string.IsNullOrWhiteSpace(ToName) ? ToAddress : $"{ToName} <{ToAddress}>";
    public string KindLabel => EmailDisplay.Kind(Kind);
    public string StatusLabel => EmailDisplay.Status(Status);
    public string StatusCss => EmailDisplay.StatusCss(Status);

    /// <summary>Anything not currently queued can be put back in the queue by hand.</summary>
    public bool IsRetryable => Status != EmailStatus.Queued;
}

/// <summary>Backing model for the admin transactional-email outbox.</summary>
public sealed class AdminEmailListViewModel
{
    public PagedResult<AdminEmailRow> Messages { get; init; } = new();

    /// <summary>How many messages sit in each state, for the filter chips.</summary>
    public EmailOutboxCounts Counts { get; init; } = new(0, 0, 0, 0);

    public EmailStatus? Status { get; init; }
    public string? Kind { get; init; }
    public IReadOnlyList<string> Kinds { get; init; } = Array.Empty<string>();

    /// <summary>The address prefilled in the "send a test" form — the signed-in administrator.</summary>
    public string TestAddress { get; init; } = string.Empty;

    public int CountFor(EmailStatus status) => status switch
    {
        EmailStatus.Queued => Counts.Queued,
        EmailStatus.Sent => Counts.Sent,
        EmailStatus.Failed => Counts.Failed,
        EmailStatus.Skipped => Counts.Skipped,
        _ => 0
    };
}
