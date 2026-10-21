using System.ComponentModel.DataAnnotations;

namespace WebDS_Test.Domain;

/// <summary>
/// A shopping cart. Owned either by a signed-in user (<see cref="UserId"/> set) or by an
/// anonymous browser identified through a session key, which is merged on sign-in.
/// </summary>
public class Cart
{
    public int Id { get; set; }

    public string? UserId { get; set; }
    public ApplicationUser? User { get; set; }

    /// <summary>Stable random key stored in session for anonymous carts.</summary>
    [StringLength(64)]
    public string? AnonymousId { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public ICollection<CartItem> Items { get; set; } = new List<CartItem>();

    public decimal Subtotal => Items.Sum(i => i.LineTotal);

    public int TotalQuantity => Items.Sum(i => i.Quantity);

    public bool IsEmpty => Items.Count == 0;

    /// <summary>
    /// The single shop that will fulfil this cart. An order is always fulfilled by one shop, so a
    /// cart either holds items from one shop or is empty; <c>CartService</c> refuses to mix them.
    /// </summary>
    public int? ShopId => Items
        .Select(i => i.ProductVariant?.Product?.ShopId)
        .FirstOrDefault(id => id is not null);

    public string? ShopName => Items
        .Select(i => i.ProductVariant?.Product?.Shop?.Name)
        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
}

/// <summary>A line in a cart. The price is always re-read from the variant at render time.</summary>
public class CartItem
{
    public int Id { get; set; }

    public int CartId { get; set; }
    public Cart? Cart { get; set; }

    public int ProductVariantId { get; set; }
    public ProductVariant? ProductVariant { get; set; }

    public int Quantity { get; set; }

    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    public decimal UnitPrice => ProductVariant?.Price ?? 0m;

    public decimal LineTotal => UnitPrice * Quantity;

    /// <summary>
    /// Stock available for this line, or null when the variant is no longer sellable. Read from the
    /// shop's listing, because stock is held by whoever is selling.
    /// </summary>
    public int? AvailableStock
    {
        get
        {
            if (ProductVariant is not { IsActive: true } variant)
            {
                return null;
            }

            var listing = variant.Product?.Listings.FirstOrDefault(l => l.IsActive);
            return listing?.StockQuantity ?? variant.StockQuantity;
        }
    }

    /// <summary>The shop selling this line, used for attribution in the cart.</summary>
    public string? ShopName => ProductVariant?.Product?.Shop?.Name;
}
