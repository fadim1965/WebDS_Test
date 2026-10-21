using WebDS_Test.Domain;

namespace WebDS_Test.Models;

/// <summary>Backing model for the catalogue browse page: results plus the filter state to re-render.</summary>
public sealed class CatalogViewModel
{
    public PagedResult<ProductCard> Results { get; init; } = new();
    public ProductQuery Query { get; init; } = new();

    public IReadOnlyList<Category> Categories { get; init; } = Array.Empty<Category>();
    public IReadOnlyList<string> Brands { get; init; } = Array.Empty<string>();
    public PriceBounds PriceBounds { get; init; }

    public Category? CurrentCategory { get; init; }

    /// <summary>Breadcrumb from the root category down to <see cref="CurrentCategory"/>.</summary>
    public IReadOnlyList<Category> Breadcrumb { get; init; } = Array.Empty<Category>();

    public string Heading => CurrentCategory?.Name ?? (string.IsNullOrWhiteSpace(Query.Search) ? "All products" : $"Results for “{Query.Search}”");
    public bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(Query.Search) ||
        !string.IsNullOrWhiteSpace(Query.Brand) ||
        Query.MinPrice is not null ||
        Query.MaxPrice is not null ||
        Query.MinRating is not null ||
        Query.InStockOnly ||
        !string.IsNullOrWhiteSpace(Query.CategorySlug);
}

/// <summary>Backing model for the product detail page.</summary>
public sealed class ProductDetailViewModel
{
    public Product Product { get; init; } = null!;
    public IReadOnlyList<ProductVariant> Variants { get; init; } = Array.Empty<ProductVariant>();
    public IReadOnlyList<ProductImage> Images { get; init; } = Array.Empty<ProductImage>();
    public IReadOnlyList<ProductCard> Related { get; init; } = Array.Empty<ProductCard>();
    public IReadOnlyList<Category> Breadcrumb { get; init; } = Array.Empty<Category>();

    public bool IsInWishlist { get; init; }

    // ------------------------------------------------------------------
    // Reviews
    // ------------------------------------------------------------------

    /// <summary>Rating headline: average, total and the per-star distribution.</summary>
    public ReviewSummary ReviewSummary { get; init; } = ReviewSummary.Empty;

    /// <summary>The current page of reviews, filtered and sorted by <see cref="ReviewQuery"/>.</summary>
    public PagedResult<Review> Reviews { get; init; } = new();

    /// <summary>The filter/sort state the review list was rendered from, echoed back into the UI.</summary>
    public ReviewQuery ReviewQuery { get; init; } = new();

    /// <summary>Review ids the signed-in shopper has already voted on, with the direction of the vote.</summary>
    public IReadOnlyDictionary<int, bool> MyVotes { get; init; } = new Dictionary<int, bool>();

    /// <summary>True when the shopper is signed in and eligible to review this product.</summary>
    public bool CanReview { get; init; }

    /// <summary>The shopper's existing review, if any, so the form can edit it rather than duplicate it.</summary>
    public Review? MyReview { get; init; }

    /// <summary>True when there is a signed-in shopper at all, whether or not they may review.</summary>
    public bool IsSignedIn { get; init; }

    /// <summary>Other variants of the same product, so the page can offer "customers also bought".</summary>
    public int TotalStock => Variants.Where(v => v.IsActive).Sum(v => v.StockQuantity);
    public bool InStock => TotalStock > 0;
    public decimal MinPrice => Variants.Count == 0 ? 0m : Variants.Where(v => v.IsActive).Select(v => v.Price).DefaultIfEmpty(0m).Min();
}

/// <summary>
/// The review form. Bound from a POST, so the fields carry the same validation the service applies
/// server-side; the service remains the authority.
/// </summary>
public sealed class ReviewFormModel
{
    [System.ComponentModel.DataAnnotations.Required]
    public int ProductId { get; set; }

    [System.ComponentModel.DataAnnotations.Range(1, 5, ErrorMessage = "Choose a rating between 1 and 5 stars.")]
    public int Rating { get; set; } = 5;

    [System.ComponentModel.DataAnnotations.StringLength(140, ErrorMessage = "Keep the title under 140 characters.")]
    public string? Title { get; set; }

    [System.ComponentModel.DataAnnotations.StringLength(4000, ErrorMessage = "Keep the review under 4000 characters.")]
    public string? Body { get; set; }

    /// <summary>Where to send the shopper back to after the review is accepted.</summary>
    public string? ReturnSlug { get; set; }
}

/// <summary>Backing model for the shop directory, with each shop's aggregate rating.</summary>
public sealed class ShopDirectoryViewModel
{
    public IReadOnlyList<Shop> Shops { get; init; } = Array.Empty<Shop>();

    /// <summary>Ratings keyed by shop id, fetched in one grouped pass for the whole page.</summary>
    public IReadOnlyDictionary<int, ShopRating> Ratings { get; init; } = new Dictionary<int, ShopRating>();

    public string? Search { get; init; }

    public ShopRating RatingFor(int shopId) =>
        Ratings.TryGetValue(shopId, out var rating) ? rating : ShopRating.Empty;
}

/// <summary>Backing model for the cart page.</summary>
public sealed class CartViewModel
{
    public Cart? Cart { get; init; }
    public StoreSettings Settings { get; init; } = new();

    /// <summary>
    /// The shop this cart belongs to. A cart never mixes shops — the cart service refuses mixed
    /// baskets and checkout creates one order per shop — so the shop's own shipping rules are
    /// the ones that apply, falling back to store defaults when the shop sets none.
    /// </summary>
    public Shop? Shop { get; init; }

    private decimal FlatRate => Shop?.FlatShippingRate ?? Settings.FlatShippingRate;
    private decimal FreeThreshold => Shop?.FreeShippingThreshold ?? Settings.FreeShippingThreshold;

    public decimal Subtotal => Cart?.Subtotal ?? 0m;
    public decimal Shipping => Subtotal <= 0m || Subtotal >= FreeThreshold ? 0m : FlatRate;
    public decimal Tax => Math.Round(Subtotal * Settings.TaxRate, 2, Settings.Rounding);
    public decimal Total => Subtotal + Shipping + Tax;

    public decimal Discount => Math.Max(0m, (CouponDiscount ?? 0m) > Subtotal ? Subtotal : (CouponDiscount ?? 0m));
    public decimal FinalSubtotal => Subtotal - Discount;
    public decimal FinalShipping => FinalSubtotal <= 0m || FinalSubtotal >= FreeThreshold ? 0m : FlatRate;

    /// <summary>
    /// Tax, per line, from each line's own tax class.
    /// </summary>
    /// <remarks>
    /// This is the same <see cref="Services.TaxCalculator"/> checkout uses, so what the shopper is
    /// shown and what the order records cannot disagree. A catalogue with no tax classes produces
    /// exactly the flat-rate figure it always did.
    /// </remarks>
    public decimal FinalTax
    {
        get
        {
            var lines = Cart?.Items
                .Select(i =>
                {
                    var listing = i.ProductVariant?.Product?.Listings.FirstOrDefault(l => l.IsActive);
                    var gross = (listing?.Price ?? i.ProductVariant?.Price ?? 0m) * i.Quantity;
                    return new Services.TaxableLine(gross, i.ProductVariant?.Product?.TaxClass);
                })
                .ToList() ?? new List<Services.TaxableLine>();

            return new Services.TaxCalculator().TaxForMixedBasket(lines, Discount, Settings.TaxRate, Settings.Rounding);
        }
    }

    public decimal FinalTotal => FinalSubtotal + FinalShipping + FinalTax;

    public decimal? CouponDiscount { get; set; }
    public string? AppliedCouponCode { get; set; }

    /// <summary>How much more the shopper must spend to qualify for free shipping.</summary>
    public decimal FreeShippingShortfall =>
        Subtotal > 0m && Subtotal < Settings.FreeShippingThreshold ? Settings.FreeShippingThreshold - Subtotal : 0m;

    /// <summary>Lines that can no longer be fulfilled as requested, surfaced as a warning block.</summary>
    public IReadOnlyList<string> Issues =>
        Cart is null
            ? Array.Empty<string>()
            : Cart.Items
                .Select(i => i switch
                {
                    _ when i.ProductVariant is null => "An item in your cart is no longer available.",
                    _ when !i.ProductVariant.IsActive => $"'{i.ProductVariant.Product?.Name}' is no longer for sale.",
                    _ when i.ProductVariant.Product is { IsPublished: false } => $"'{i.ProductVariant.Product.Name}' is no longer published.",
                    _ when i.ProductVariant.StockQuantity <= 0 => $"'{i.ProductVariant.Product?.Name}' is out of stock.",
                    _ when i.Quantity > i.ProductVariant.StockQuantity => $"Only {i.ProductVariant.StockQuantity} × '{i.ProductVariant.Product?.Name}' available.",
                    _ => null
                })
                .Where(m => m is not null)
                .Select(m => m!)
                .ToList();

    public bool CanCheckout => Cart is { Items.Count: > 0 } && Issues.Count == 0;
}

/// <summary>Checkout form model. Validation attributes drive the client and server checks.</summary>
public sealed class CheckoutViewModel
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter an email address so we can send your receipt.")]
    [System.ComponentModel.DataAnnotations.EmailAddress(ErrorMessage = "That does not look like a valid email address.")]
    [System.ComponentModel.DataAnnotations.StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter the name for this order.")]
    [System.ComponentModel.DataAnnotations.StringLength(120)]
    public string CustomerName { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Phone(ErrorMessage = "That does not look like a valid phone number.")]
    [System.ComponentModel.DataAnnotations.StringLength(40)]
    public string? Phone { get; set; }

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter a recipient name.")]
    [System.ComponentModel.DataAnnotations.StringLength(120)]
    public string ShippingName { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter a street address.")]
    [System.ComponentModel.DataAnnotations.StringLength(200)]
    public string ShippingLine1 { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.StringLength(200)]
    public string? ShippingLine2 { get; set; }

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter a city.")]
    [System.ComponentModel.DataAnnotations.StringLength(100)]
    public string ShippingCity { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.StringLength(100)]
    public string? ShippingState { get; set; }

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter a postal code.")]
    [System.ComponentModel.DataAnnotations.StringLength(20)]
    public string ShippingPostalCode { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter a country.")]
    [System.ComponentModel.DataAnnotations.StringLength(80)]
    public string ShippingCountry { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.StringLength(1000)]
    public string? CustomerNote { get; set; }

    public bool SaveAddressToBook { get; set; } = true;

    /// <summary>Hidden field carried through the form so a double submit cannot place two orders.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>Optional saved address chosen from the address book.</summary>
    public int? SelectedAddressId { get; set; }

    public IReadOnlyList<Address> SavedAddresses { get; set; } = Array.Empty<Address>();
    public CartViewModel Cart { get; set; } = new();

    /// <summary>How the shopper wants to pay. Card is only offered when a hosted gateway is registered.</summary>
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.CashOnDelivery;

    /// <summary>True when a hosted gateway is configured, so the card choice can be shown.</summary>
    public bool CardAvailable { get; set; }
}

/// <summary>The simulated payment provider's page, shown between checkout and the order.</summary>
public sealed class SimulatorPaymentViewModel
{
    public Order Order { get; set; } = new();

    /// <summary>Store currency symbol, so the provider page presents the total the shopper saw.</summary>
    public string CurrencySymbol { get; set; } = "$";

    /// <summary>Where the shopper goes once the simulated payment is settled.</summary>
    public string? ReturnUrl { get; set; }
}

// ------------------------------------------------------------------
// Conversations (shopper <-> shop)
// ------------------------------------------------------------------

/// <summary>Backing model for the shopper's conversation inbox.</summary>
public sealed class ConversationInboxViewModel
{
    public IReadOnlyList<Conversation> Conversations { get; init; } = Array.Empty<Conversation>();

    /// <summary>Threads with something new since the shopper last opened one.</summary>
    public int Unread { get; init; }
}

/// <summary>
/// The "start a conversation" form. Bound from a POST, so the fields carry the same validation the
/// service applies server-side; the service remains the authority.
/// </summary>
public sealed class ConversationStartForm
{
    public int ShopId { get; set; }

    /// <summary>Optional order the question is about, carried from the order page.</summary>
    public int? OrderId { get; set; }

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Give the message a subject.")]
    [System.ComponentModel.DataAnnotations.StringLength(200, ErrorMessage = "Keep the subject under 200 characters.")]
    public string Subject { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Write a message.")]
    [System.ComponentModel.DataAnnotations.StringLength(4000, ErrorMessage = "Keep the message under 4000 characters.")]
    public string Body { get; set; } = string.Empty;
}

/// <summary>One reply box on a conversation thread.</summary>
public sealed class ConversationReplyForm
{
    public int ConversationId { get; set; }

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Write a message.")]
    [System.ComponentModel.DataAnnotations.StringLength(4000, ErrorMessage = "Keep the message under 4000 characters.")]
    public string Body { get; set; } = string.Empty;
}

/// <summary>Backing model for a conversation thread, which is rendered from either side.</summary>
public sealed class ConversationThreadViewModel
{
    public Conversation Conversation { get; init; } = null!;

    /// <summary>True when the reader is the shopper, false for the shop or the marketplace.</summary>
    public bool IsCustomerView { get; init; }

    /// <summary>Where the shopper's "back" link goes; the vendor view links to its own worklist.</summary>
    public bool CanReply => Conversation.IsOpen;
}
