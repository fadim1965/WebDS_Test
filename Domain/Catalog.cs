using System.ComponentModel.DataAnnotations;

namespace WebDS_Test.Domain;

/// <summary>A self-referencing product category tree.</summary>
/// <remarks>
/// A category belongs either to one shop or to the marketplace as a whole. Global categories
/// (<see cref="IsGlobal"/>) are maintained by administrators and shared by every shop — they are
/// what makes the marketplace browsable as one catalogue. A shop may additionally keep its own
/// private categories, visible only on its own storefront.
/// </remarks>
public class Category
{
    public int Id { get; set; }

    /// <summary>Owning shop, or null for a marketplace-wide global category.</summary>
    public int? ShopId { get; set; }
    public Shop? Shop { get; set; }

    /// <summary>True when an administrator maintains this category for the whole marketplace.</summary>
    public bool IsGlobal { get; set; }

    [Required, StringLength(80)]
    public string Name { get; set; } = string.Empty;

    /// <summary>URL segment, unique within its shop (and within the global set).</summary>
    [Required, StringLength(80)]
    public string Slug { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    public int? ParentId { get; set; }
    public Category? Parent { get; set; }

    public ICollection<Category> Children { get; set; } = new List<Category>();

    public int DisplayOrder { get; set; }

    /// <summary>Inactive categories are hidden from the storefront but kept for history.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Optional commission rate for this category, as a fraction (0.10 = 10%). When set it overrides
    /// the shop's default commission rate for products in this category.
    /// </summary>
    /// <remarks>
    /// The marketplace can set a higher or lower rate per category (e.g. electronics 5%, fashion 15%).
    /// A null value means "use the shop's rate". The value is clamped to 0..1 when read.
    /// </summary>
    public decimal? CommissionRate { get; set; }

    public ICollection<Product> Products { get; set; } = new List<Product>();
}

/// <summary>A catalogue entry. The sellable unit is a <see cref="ProductVariant"/>.</summary>
/// <remarks>
/// A product belongs to exactly one shop. The shop's commercial terms — its price and the stock it
/// physically holds — live on <see cref="ShopListing"/>, so two shops can offer the same product
/// definition on their own terms.
/// </remarks>
public class Product
{
    public int Id { get; set; }

    /// <summary>Owning shop. Every product has one; the marketplace is not itself a seller.</summary>
    public int ShopId { get; set; }
    public Shop? Shop { get; set; }

    [Required, StringLength(160)]
    public string Name { get; set; } = string.Empty;

    [Required, StringLength(160)]
    public string Slug { get; set; } = string.Empty;

    [StringLength(200)]
    public string? ShortDescription { get; set; }

    public string? Description { get; set; }

    [StringLength(120)]
    public string? Brand { get; set; }

    public int CategoryId { get; set; }
    public Category? Category { get; set; }

    /// <summary>
    /// Optional tax class. Null falls back to <c>Store:TaxRate</c>, so a product created before tax
    /// classes existed — or one deliberately left on the store default — keeps its current behaviour.
    /// </summary>
    public int? TaxClassId { get; set; }
    public TaxClass? TaxClass { get; set; }

    /// <summary>Unpublished products are invisible to shoppers and cannot be added to a cart.</summary>
    public bool IsPublished { get; set; } = true;

    /// <summary>
    /// Where this listing sits in the marketplace's review process. A vendor submits a listing and
    /// an administrator approves it; only an approved listing is published.
    /// </summary>
    /// <remarks>
    /// The zero value is <see cref="ListingStatus.Published"/>, so a product that predates
    /// moderation keeps its place in the catalogue without an administrator having to re-approve it.
    /// <c>ListingModerationService</c> keeps this and <see cref="IsPublished"/> in step: a listing is
    /// live exactly when it is published.
    /// </remarks>
    public ListingStatus ModerationStatus { get; set; } = ListingStatus.Published;

    /// <summary>
    /// Why an administrator turned the listing down. Shown to the vendor so they can fix it.
    /// </summary>
    [StringLength(500)]
    public string? ModerationReason { get; set; }

    /// <summary>When the listing last changed state.</summary>
    public DateTime? ModeratedUtc { get; set; }

    /// <summary>Soft delete. Products referenced by an order are never physically removed.</summary>
    public bool IsDeleted { get; set; }

    public bool IsFeatured { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public ICollection<ProductVariant> Variants { get; set; } = new List<ProductVariant>();
    public ICollection<ProductImage> Images { get; set; } = new List<ProductImage>();

    /// <summary>The shop's commercial offer for this product, including the stock it holds.</summary>
    public ICollection<ShopListing> Listings { get; set; } = new List<ShopListing>();

    /// <summary>Shopper reviews. Used to aggregate the product's star rating.</summary>
    public ICollection<Review> Reviews { get; set; } = new List<Review>();

    /// <summary>Cheapest variant price, used in listings. Falls back to zero for an empty product.</summary>
    public decimal DisplayPrice => Variants.Count == 0 ? 0m : Variants.Min(v => v.Price);

    /// <summary>
    /// True when the product is sellable. Stock is held per shop listing, so a product with no
    /// listing is not sellable even if its variants carry a legacy stock figure.
    /// </summary>
    public bool InStock => Listings.Any(l => l.IsActive && l.StockQuantity > 0)
                           || Variants.Any(v => v.IsActive && v.StockQuantity > 0);

    public bool HasPriceRange => Variants.Count > 1 && Variants.Select(v => v.Price).Distinct().Count() > 1;
}

/// <summary>The sellable unit: a SKU with its own price, stock level and concurrency token.</summary>
public class ProductVariant
{
    public int Id { get; set; }

    public int ProductId { get; set; }
    public Product? Product { get; set; }

    [Required, StringLength(64)]
    public string Sku { get; set; } = string.Empty;

    /// <summary>Variant label, e.g. "Large / Blue". Empty for a single-variant product.</summary>
    [StringLength(80)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Money is decimal(18,2) — never double/float.</summary>
    public decimal Price { get; set; }

    /// <summary>Optional "was" price used to render a strike-through.</summary>
    public decimal? CompareAtPrice { get; set; }

    public int StockQuantity { get; set; }

    /// <summary>When false the variant cannot be purchased but remains on historical orders.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Amount below which the admin dashboard flags the variant as low stock.</summary>
    public int LowStockThreshold { get; set; } = 5;

    /// <summary>
    /// Optimistic concurrency token. SQLite has no native rowversion, so the value is a
    /// manually maintained GUID refreshed on every save by <c>ApplicationDbContext</c>.
    /// A colliding save throws <c>DbUpdateConcurrencyException</c> and prevents overselling.
    /// </summary>
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();

    public ICollection<CartItem> CartItems { get; set; } = new List<CartItem>();

    public bool IsLowStock => StockQuantity <= LowStockThreshold;

    public decimal EffectiveCompareAtPrice =>
        CompareAtPrice is > 0 && CompareAtPrice > Price ? CompareAtPrice.Value : 0m;
}

/// <summary>An ordered gallery image for a product.</summary>
public class ProductImage
{
    public int Id { get; set; }

    public int ProductId { get; set; }
    public Product? Product { get; set; }

    /// <summary>
    /// Absolute URL, a path under <c>wwwroot</c>, or an inline base64 data URI. Left unbounded
    /// because the seeded placeholders (and uploaded previews) are stored as data URIs, which run
    /// well past a few hundred characters.
    /// </summary>
    [Required]
    public string Url { get; set; } = string.Empty;

    [StringLength(200)]
    public string? AltText { get; set; }

    public int DisplayOrder { get; set; }

    public bool IsPrimary { get; set; }
}
