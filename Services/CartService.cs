using Microsoft.EntityFrameworkCore;

namespace WebDS_Test.Services;

/// <summary>Add-to-cart / update outcome surfaced to the user as a flash message.</summary>
public sealed record CartResult(bool Success, string Message)
{
    public static CartResult Ok(string message) => new(true, message);
    public static CartResult Fail(string message) => new(false, message);
}

/// <summary>
/// Shopping-cart operations. A cart belongs either to a signed-in user or to an anonymous
/// session key; the two are merged when the shopper signs in.
/// </summary>
public interface ICartService
{
    /// <summary>The cart for the current request, or null when nothing has been added yet.</summary>
    Task<Cart?> GetCartAsync(CancellationToken ct = default);

    /// <summary>The current cart, creating and persisting an empty one when absent.</summary>
    Task<Cart> GetOrCreateCartAsync(CancellationToken ct = default);

    Task<CartResult> AddItemAsync(int variantId, int quantity = 1, CancellationToken ct = default);
    Task<CartResult> UpdateQuantityAsync(int cartItemId, int quantity, CancellationToken ct = default);
    Task<CartResult> RemoveItemAsync(int cartItemId, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);

    Task<int> GetItemCountAsync(CancellationToken ct = default);

    /// <summary>Attaches an anonymous cart to the user who just signed in and drops the session key.</summary>
    Task MergeAnonymousCartAsync(string userId, CancellationToken ct = default);
}

public sealed class CartService : ICartService
{
    /// <summary>Hard ceiling per line, independent of stock, to blunt accidental bulk adds.</summary>
    private const int MaxQuantityPerLine = 99;

    private readonly ApplicationDbContext _db;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<CartService> _logger;

    public CartService(ApplicationDbContext db, IHttpContextAccessor http, ILogger<CartService> logger)
    {
        _db = db;
        _http = http;
        _logger = logger;
    }

    private const string SessionKey = "WebDS.CartKey";

    private string? CurrentUserId => _http.HttpContext?.User?.Identity?.IsAuthenticated == true
        ? _http.HttpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        : null;

    /// <summary>Stable per-browser key; created on first use and kept in session.</summary>
    private string GetAnonymousId()
    {
        var session = _http.HttpContext?.Session;
        var existing = session?.GetString(SessionKey);
        if (!string.IsNullOrEmpty(existing))
        {
            return existing;
        }

        var created = Guid.NewGuid().ToString("N");
        session?.SetString(SessionKey, created);
        return created;
    }

    public async Task<Cart?> GetCartAsync(CancellationToken ct = default)
    {
        var userId = CurrentUserId;

        if (!string.IsNullOrEmpty(userId))
        {
            return await CartQuery().FirstOrDefaultAsync(c => c.UserId == userId, ct);
        }

        // Do not create a session key just to read; only read an existing one.
        var anonymousId = _http.HttpContext?.Session?.GetString(SessionKey);
        if (string.IsNullOrEmpty(anonymousId))
        {
            return null;
        }

        return await CartQuery().FirstOrDefaultAsync(c => c.AnonymousId == anonymousId, ct);
    }

    public async Task<Cart> GetOrCreateCartAsync(CancellationToken ct = default)
    {
        var userId = CurrentUserId;

        if (!string.IsNullOrEmpty(userId))
        {
            var forUser = await CartQuery().FirstOrDefaultAsync(c => c.UserId == userId, ct);
            if (forUser is not null)
            {
                return forUser;
            }

            var created = new Cart { UserId = userId };
            _db.Carts.Add(created);
            await _db.SaveChangesAsync(ct);
            return created;
        }

        var anonymousId = GetAnonymousId();
        var anonymous = await CartQuery().FirstOrDefaultAsync(c => c.AnonymousId == anonymousId, ct);
        if (anonymous is not null)
        {
            return anonymous;
        }

        var newCart = new Cart { AnonymousId = anonymousId };
        _db.Carts.Add(newCart);
        await _db.SaveChangesAsync(ct);
        return newCart;
    }

    public async Task<CartResult> AddItemAsync(int variantId, int quantity = 1, CancellationToken ct = default)
    {
        if (quantity <= 0)
        {
            return CartResult.Fail("Please choose a quantity of at least 1.");
        }

        var variant = await _db.ProductVariants
            .Include(v => v.Product)
                .ThenInclude(p => p!.Shop)
            .Include(v => v.Product)
                .ThenInclude(p => p!.Listings)
            .FirstOrDefaultAsync(v => v.Id == variantId, ct);

        if (variant is null)
        {
            return CartResult.Fail("That product is no longer available.");
        }

        var product = variant.Product;

        if (product is null || product.IsDeleted || !product.IsPublished)
        {
            return CartResult.Fail("That product is not currently for sale.");
        }

        // A shop that is not trading cannot sell, whatever the state of its catalogue.
        if (product.Shop is not { Status: ShopStatus.Approved })
        {
            return CartResult.Fail("That shop is not currently accepting orders.");
        }

        if (!variant.IsActive)
        {
            return CartResult.Fail("That option is no longer for sale.");
        }

        // Stock belongs to the shop's listing, not to the shared product definition.
        var listing = product.Listings.FirstOrDefault(l => l.IsActive);
        if (listing is null)
        {
            return CartResult.Fail("That shop is no longer offering this product.");
        }

        if (listing.StockQuantity <= 0)
        {
            return CartResult.Fail($"'{product.Name}' is out of stock.");
        }

        var cart = await GetOrCreateCartAsync(ct);

        // One order per shop means one shop per cart. Mixing shops here would make checkout
        // ambiguous about who fulfils what, so the second shop is refused with a clear reason.
        var cartShopId = cart.Items
            .Select(i => i.ProductVariant?.Product?.ShopId)
            .FirstOrDefault(id => id is not null);

        if (cartShopId is { } existingShopId && existingShopId != product.ShopId)
        {
            var existingShopName = cart.Items
                .Select(i => i.ProductVariant?.Product?.Shop?.Name)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

            return CartResult.Fail(
                $"Your cart already contains items from {existingShopName ?? "another shop"}. " +
                "An order is fulfilled by a single shop, so please complete or empty that cart first.");
        }

        var existing = cart.Items.FirstOrDefault(i => i.ProductVariantId == variantId);
        var originalQuantity = existing?.Quantity ?? 0;
        var desired = originalQuantity + quantity;

        if (desired > listing.StockQuantity)
        {
            desired = listing.StockQuantity;
            if (existing is not null && existing.Quantity >= listing.StockQuantity)
            {
                return CartResult.Fail(
                    $"Only {listing.StockQuantity} of '{product.Name}' are in stock, and your cart already has them all.");
            }
        }

        if (desired > MaxQuantityPerLine)
        {
            desired = MaxQuantityPerLine;
        }

        if (existing is not null)
        {
            existing.Quantity = desired;
        }
        else
        {
            cart.Items.Add(new CartItem
            {
                CartId = cart.Id,
                ProductVariantId = variantId,
                Quantity = desired,
                AddedUtc = DateTime.UtcNow
            });
        }

        cart.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var addedCount = Math.Max(1, desired - originalQuantity);
        return CartResult.Ok($"{addedCount} × '{variant.Product?.Name}' added to your cart.");
    }

    public async Task<CartResult> UpdateQuantityAsync(int cartItemId, int quantity, CancellationToken ct = default)
    {
        var cart = await GetCartAsync(ct);
        if (cart is null)
        {
            return CartResult.Fail("Your cart is empty.");
        }

        var item = cart.Items.FirstOrDefault(i => i.Id == cartItemId);
        if (item is null)
        {
            return CartResult.Fail("That item is no longer in your cart.");
        }

        if (quantity <= 0)
        {
            return await RemoveItemAsync(cartItemId, ct);
        }

        // Available stock comes from the shop's listing, which is what the shop actually holds.
        var stock = item.ProductVariant?.Product?.Listings.FirstOrDefault(l => l.IsActive)?.StockQuantity
                    ?? item.ProductVariant?.StockQuantity
                    ?? 0;

        if (quantity > stock)
        {
            return CartResult.Fail($"Only {stock} available — quantity not changed.");
        }

        item.Quantity = Math.Min(quantity, MaxQuantityPerLine);
        cart.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return CartResult.Ok("Cart updated.");
    }

    public async Task<CartResult> RemoveItemAsync(int cartItemId, CancellationToken ct = default)
    {
        var cart = await GetCartAsync(ct);
        if (cart is null)
        {
            return CartResult.Fail("Your cart is empty.");
        }

        var item = cart.Items.FirstOrDefault(i => i.Id == cartItemId);
        if (item is null)
        {
            return CartResult.Fail("That item is no longer in your cart.");
        }

        _db.CartItems.Remove(item);
        cart.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return CartResult.Ok("Item removed.");
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        var cart = await GetCartAsync(ct);
        if (cart is null || cart.Items.Count == 0)
        {
            return;
        }

        _db.CartItems.RemoveRange(cart.Items);
        cart.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<int> GetItemCountAsync(CancellationToken ct = default)
    {
        var userId = CurrentUserId;

        if (!string.IsNullOrEmpty(userId))
        {
            return await _db.CartItems
                .Where(i => i.Cart!.UserId == userId)
                .SumAsync(i => (int?)i.Quantity, ct) ?? 0;
        }

        var anonymousId = _http.HttpContext?.Session?.GetString(SessionKey);
        if (string.IsNullOrEmpty(anonymousId))
        {
            return 0;
        }

        return await _db.CartItems
            .Where(i => i.Cart!.AnonymousId == anonymousId)
            .SumAsync(i => (int?)i.Quantity, ct) ?? 0;
    }

    public async Task MergeAnonymousCartAsync(string userId, CancellationToken ct = default)
    {
        var anonymousId = _http.HttpContext?.Session?.GetString(SessionKey);

        var userCart = await CartQuery().FirstOrDefaultAsync(c => c.UserId == userId, ct);

        if (string.IsNullOrEmpty(anonymousId))
        {
            return;
        }

        var anonymousCart = await CartQuery().FirstOrDefaultAsync(c => c.AnonymousId == anonymousId, ct);
        if (anonymousCart is null)
        {
            return;
        }

        if (userCart is null)
        {
            // Simplest merge: adopt the anonymous cart for the signed-in user.
            anonymousCart.UserId = userId;
            anonymousCart.AnonymousId = null;
            anonymousCart.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            _http.HttpContext?.Session?.Remove(SessionKey);
            _logger.LogInformation("Adopted anonymous cart {CartId} for user {UserId}", anonymousCart.Id, userId);
            return;
        }

        // Both exist: fold the anonymous lines in, respecting stock and the per-line cap.
        foreach (var item in anonymousCart.Items.ToList())
        {
            var stock = item.ProductVariant?.StockQuantity ?? 0;
            var target = userCart.Items.FirstOrDefault(i => i.ProductVariantId == item.ProductVariantId);

            if (target is null)
            {
                userCart.Items.Add(new CartItem
                {
                    CartId = userCart.Id,
                    ProductVariantId = item.ProductVariantId,
                    Quantity = Math.Min(item.Quantity, Math.Min(stock, MaxQuantityPerLine)),
                    AddedUtc = item.AddedUtc
                });
            }
            else
            {
                target.Quantity = Math.Min(target.Quantity + item.Quantity, Math.Min(stock, MaxQuantityPerLine));
            }
        }

        _db.Carts.Remove(anonymousCart);
        userCart.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _http.HttpContext?.Session?.Remove(SessionKey);
        _logger.LogInformation("Merged anonymous cart {AnonymousCartId} into {CartId} for user {UserId}",
            anonymousCart.Id, userCart.Id, userId);
    }

    private IQueryable<Cart> CartQuery() =>
        _db.Carts
            .Include(c => c.Items)
                .ThenInclude(i => i.ProductVariant)
                    .ThenInclude(v => v!.Product)
                        .ThenInclude(p => p!.Images)
            .Include(c => c.Items)
                .ThenInclude(i => i.ProductVariant)
                    .ThenInclude(v => v!.Product)
                        .ThenInclude(p => p!.Shop)
            .Include(c => c.Items)
                .ThenInclude(i => i.ProductVariant)
                    .ThenInclude(v => v!.Product)
                        .ThenInclude(p => p!.Listings)
            // The tax class decides each line's rate at checkout, so it has to travel with the cart.
            .Include(c => c.Items)
                .ThenInclude(i => i.ProductVariant)
                    .ThenInclude(v => v!.Product)
                        .ThenInclude(p => p!.TaxClass);
}
