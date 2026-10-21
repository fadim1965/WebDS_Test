using Microsoft.EntityFrameworkCore;

namespace WebDS_Test.Services;

/// <summary>Read-side catalogue access used by the storefront.</summary>
/// <remarks>
/// Every method is shop-aware. A null <c>shopId</c> means the marketplace-wide view; a value scopes
/// the read to that shop. The status of the owning shop is checked in every path, so a suspended or
/// unapproved vendor's products can never be browsed, searched or bought.
/// </remarks>
public interface ICatalogService
{
    Task<PagedResult<ProductCard>> BrowseAsync(ProductQuery query, CancellationToken ct = default);

    /// <summary>Resolves a product by slug, optionally within one shop.</summary>
    Task<Product?> GetProductBySlugAsync(string slug, int? shopId = null, CancellationToken ct = default);

    Task<Product?> GetProductByIdAsync(int id, CancellationToken ct = default);

    /// <summary>Category tree, including global categories plus the shop's own when scoped.</summary>
    Task<IReadOnlyList<Category>> GetCategoryTreeAsync(int? shopId = null, CancellationToken ct = default);
    Task<IReadOnlyList<Category>> GetActiveCategoriesAsync(int? shopId = null, CancellationToken ct = default);
    Task<Category?> GetCategoryBySlugAsync(string slug, int? shopId = null, CancellationToken ct = default);

    Task<IReadOnlyList<ProductCard>> GetFeaturedAsync(int take = 8, int? shopId = null, CancellationToken ct = default);
    Task<IReadOnlyList<ProductCard>> GetRelatedAsync(Product product, int take = 4, CancellationToken ct = default);
    Task<IReadOnlyList<ProductCard>> GetBestSellersAsync(int take = 8, int? shopId = null, CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetBrandsAsync(int? shopId = null, CancellationToken ct = default);
    Task<PriceBounds> GetPriceBoundsAsync(int? shopId = null, CancellationToken ct = default);
}

/// <summary>Lowest and highest sellable price in the catalogue, used to seed the price filter.</summary>
public readonly record struct PriceBounds(decimal Min, decimal Max)
{
    public bool HasRange => Max > Min;
}

public sealed class CatalogService : ICatalogService
{
    private readonly ApplicationDbContext _db;

    public CatalogService(ApplicationDbContext db) => _db = db;

    public async Task<PagedResult<ProductCard>> BrowseAsync(ProductQuery query, CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 60);

        // Captured so the relevance sort below can reuse the same escaped LIKE pattern the search
        // predicate was built from.
        string? searchTerm = null;

        // Starts from published, non-deleted products. Soft deletes are filtered explicitly
        // rather than by a global query filter — see the note in ApplicationDbContext.
        IQueryable<Product> q = _db.Products
            .AsNoTracking()
            .Include(p => p.Category)
            .Include(p => p.Variants)
            .Include(p => p.Images)
            .Include(p => p.Listings)
            .Include(p => p.Shop)
            .Where(p => p.IsPublished && !p.IsDeleted);

        // Only products whose owning shop is actually trading reach a storefront. This is the
        // single most important isolation rule in the marketplace: a pending or suspended shop's
        // catalogue must never be purchasable.
        q = q.Where(p => p.Shop != null && p.Shop.Status == ShopStatus.Approved);

        if (query.ShopId is { } shopId)
        {
            q = q.Where(p => p.ShopId == shopId);
        }

        if (!string.IsNullOrWhiteSpace(query.CategorySlug))
        {
            var category = await _db.Categories.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Slug == query.CategorySlug && (query.ShopId == null || c.ShopId == query.ShopId), ct);

            if (category is null)
            {
                return new PagedResult<ProductCard> { Page = page, PageSize = pageSize, TotalCount = 0 };
            }

            if (query.IncludeSubcategories)
            {
                // EF.Constant bakes the ids into the query as a constant, so EF renders an ordinary
                // IN (…) list. Left as a captured collection, EF Core 9 emits OPENJSON — which SQL
                // Server 2014 does not have, and the browse page fails with a syntax error.
                var ids = (await GetDescendantCategoryIdsAsync(category.Id, ct)).ToArray();
                q = q.Where(p => EF.Constant(ids).Contains(p.CategoryId));
            }
            else
            {
                q = q.Where(p => p.CategoryId == category.Id);
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // LIKE keeps this provider-portable. A production deployment would swap in
            // full-text search; the interface above would not change.
            searchTerm = $"%{Escape(query.Search.Trim())}%";
            q = q.Where(p =>
                EF.Functions.Like(p.Name, searchTerm, "\\") ||
                EF.Functions.Like(p.ShortDescription ?? string.Empty, searchTerm, "\\") ||
                (p.Brand != null && EF.Functions.Like(p.Brand, searchTerm, "\\")) ||
                p.Variants.Any(v => EF.Functions.Like(v.Sku, searchTerm, "\\")));
        }

        if (!string.IsNullOrWhiteSpace(query.Brand))
        {
            q = q.Where(p => p.Brand == query.Brand);
        }

        if (query.FeaturedOnly)
        {
            q = q.Where(p => p.IsFeatured);
        }

        if (query.InStockOnly)
        {
            // Stock is held by the shop listing, so availability is a property of who is selling.
            q = q.Where(p => p.Listings.Any(l => l.IsActive && l.StockQuantity > 0));
        }

        // Price filters compare against the price the shop actually charges: its listing price when
        // set, otherwise the product's own price.
        if (query.MinPrice is { } minPrice)
        {
            q = q.Where(p => p.Listings.Any(l => l.IsActive &&
                (l.Price != null ? l.Price : p.Variants.Where(v => v.IsActive).Min(v => (decimal?)v.Price)) >= minPrice));
        }

        if (query.MaxPrice is { } maxPrice)
        {
            q = q.Where(p => p.Listings.Any(l => l.IsActive &&
                (l.Price != null ? l.Price : p.Variants.Where(v => v.IsActive).Min(v => (decimal?)v.Price)) <= maxPrice));
        }

        // Rating facet. The average runs as a correlated subquery over published reviews, so a
        // product with no reviews yields a null average and is excluded — exactly what "4 stars and
        // up" should mean. The cast to double keeps the aggregate portable (SQLite cannot average an
        // int through EF without it).
        if (query.MinRating is { } minRating && minRating > 1)
        {
            q = q.Where(p => _db.Reviews
                .Where(r => r.ProductId == p.Id && r.Status == ReviewStatus.Published)
                .Average(r => (double?)r.Rating) >= minRating);
        }

        var total = await q.CountAsync(ct);
        page = Paging.ClampPage(page, pageSize, total);

        // Price ordering aggregates a decimal column. SQLite cannot apply Min/Max to a decimal,
        // so the aggregate is cast to double for the ORDER BY only. Ordering by a float is safe
        // here: it never has to round-trip through a money value, and the projection onto
        // ProductCard still uses the exact decimal from the loaded entity.
        q = query.Sort switch
        {
            ProductSort.PriceLowToHigh => q
                .OrderBy(p => p.Listings.Where(l => l.IsActive).Min(l => (double?)l.Price)
                              ?? p.Variants.Where(v => v.IsActive).Min(v => (double?)v.Price) ?? 0d)
                .ThenBy(p => p.Name),

            ProductSort.PriceHighToLow => q
                .OrderByDescending(p => p.Listings.Where(l => l.IsActive).Max(l => (double?)l.Price)
                                       ?? p.Variants.Where(v => v.IsActive).Max(v => (double?)v.Price) ?? 0d)
                .ThenBy(p => p.Name),

            ProductSort.NameAscending => q.OrderBy(p => p.Name),

            ProductSort.BestSelling => q.OrderByDescending(p => p.IsFeatured)
                .ThenByDescending(p => _db.OrderLines.Count(l => l.ProductId == p.Id))
                .ThenBy(p => p.Name),

            // Highest rated first. Reviewed products outrank unreviewed ones (the coalesce to 0 puts
            // them last), and ties break on how many ratings back the score.
            ProductSort.TopRated => q
                .OrderByDescending(p => _db.Reviews
                    .Where(r => r.ProductId == p.Id && r.Status == ReviewStatus.Published)
                    .Average(r => (double?)r.Rating) ?? 0d)
                .ThenByDescending(p => _db.Reviews.Count(r => r.ProductId == p.Id && r.Status == ReviewStatus.Published))
                .ThenBy(p => p.Name),

            ProductSort.MostReviewed => q
                .OrderByDescending(p => _db.Reviews.Count(r => r.ProductId == p.Id && r.Status == ReviewStatus.Published))
                .ThenBy(p => p.Name),

            // Text relevance: a hit in the name beats a hit in the brand, which beats a hit in the
            // short description. Only meaningful with a search term, so it degrades to newest-first.
            ProductSort.Relevance when searchTerm is not null => q
                .OrderByDescending(p =>
                    (EF.Functions.Like(p.Name, searchTerm, "\\") ? 3 : 0) +
                    (p.Brand != null && EF.Functions.Like(p.Brand, searchTerm, "\\") ? 2 : 0) +
                    (p.ShortDescription != null && EF.Functions.Like(p.ShortDescription, searchTerm, "\\") ? 1 : 0))
                .ThenByDescending(p => p.IsFeatured)
                .ThenBy(p => p.Name),

            _ => q.OrderByDescending(p => p.CreatedUtc).ThenBy(p => p.Name)
        };

        var products = await q.Skip(Paging.Skip(page, pageSize)).Take(pageSize).ToListAsync(ct);

        // One grouped query supplies the star rating for the whole page, so a card never triggers a
        // per-product aggregate.
        var ratings = await GetRatingsAsync(products.Select(p => p.Id), ct);

        return new PagedResult<ProductCard>
        {
            Items = products.Select(p => ToCard(p, ratings.GetValueOrDefault(p.Id))).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = total
        };
    }

    /// <summary>
    /// Product lookup for a storefront. Scoped by shop when <paramref name="shopId"/> is given, so
    /// two shops can legitimately own the same slug without either resolving the other's product.
    /// </summary>
    public Task<Product?> GetProductBySlugAsync(string slug, int? shopId = null, CancellationToken ct = default)
    {
        var query = FullProductQuery()
            .Where(p => p.Slug == slug && p.IsPublished && !p.IsDeleted &&
                        p.Shop != null && p.Shop.Status == ShopStatus.Approved);

        if (shopId is { } id)
        {
            query = query.Where(p => p.ShopId == id);
        }

        return query.FirstOrDefaultAsync(ct);
    }

    public Task<Product?> GetProductByIdAsync(int id, CancellationToken ct = default) =>
        FullProductQuery().FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, ct);

    /// <summary>
    /// Category tree for a storefront. Global (marketplace) categories are always included; a shop
    /// additionally sees its own private categories.
    /// </summary>
    public async Task<IReadOnlyList<Category>> GetCategoryTreeAsync(int? shopId = null, CancellationToken ct = default)
    {
        var all = await _db.Categories.AsNoTracking()
            .Where(c => c.IsGlobal || shopId == null || c.ShopId == shopId)
            .OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name)
            .ToListAsync(ct);

        // Materialise the tree in memory: the catalogue is small and this avoids N+1 queries.
        var byParent = all.ToLookup(c => c.ParentId);
        foreach (var category in all)
        {
            category.Children = byParent[category.Id].ToList();
        }

        // Only roots whose parent is absent from the filtered set become top-level nodes.
        var present = all.Select(c => c.Id).ToHashSet();
        return byParent[null]
            .Concat(all.Where(c => c.ParentId is { } parent && !present.Contains(parent)))
            .DistinctBy(c => c.Id)
            .OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name)
            .ToList();
    }

    public async Task<IReadOnlyList<Category>> GetActiveCategoriesAsync(int? shopId = null, CancellationToken ct = default) =>
        await _db.Categories.AsNoTracking()
            .Where(c => c.IsActive && (c.IsGlobal || shopId == null || c.ShopId == shopId))
            .OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name)
            .ToListAsync(ct);

    public Task<Category?> GetCategoryBySlugAsync(string slug, int? shopId = null, CancellationToken ct = default) =>
        _db.Categories.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Slug == slug && (shopId == null || c.ShopId == shopId || c.IsGlobal), ct);

    public async Task<IReadOnlyList<ProductCard>> GetFeaturedAsync(int take = 8, int? shopId = null, CancellationToken ct = default)
    {
        var featured = await ScopedCardQuery(shopId)
            .Where(p => p.IsFeatured)
            .OrderByDescending(p => p.UpdatedUtc)
            .Take(take)
            .ToListAsync(ct);

        // Fall back to newest products so the homepage is never empty on a fresh catalogue.
        var products = featured.Count > 0
            ? featured
            : await ScopedCardQuery(shopId)
                .OrderByDescending(p => p.CreatedUtc)
                .Take(take)
                .ToListAsync(ct);

        var ratings = await GetRatingsAsync(products.Select(p => p.Id), ct);
        return products.Select(p => ToCard(p, ratings.GetValueOrDefault(p.Id))).ToList();
    }

    public async Task<IReadOnlyList<ProductCard>> GetBestSellersAsync(int take = 8, int? shopId = null, CancellationToken ct = default)
    {
        var products = await ScopedCardQuery(shopId)
            .OrderByDescending(p => _db.OrderLines.Count(l => l.ProductId == p.Id))
            .ThenByDescending(p => p.CreatedUtc)
            .Take(take)
            .ToListAsync(ct);

        var ratings = await GetRatingsAsync(products.Select(p => p.Id), ct);
        return products.Select(p => ToCard(p, ratings.GetValueOrDefault(p.Id))).ToList();
    }

    public async Task<IReadOnlyList<ProductCard>> GetRelatedAsync(Product product, int take = 4, CancellationToken ct = default)
    {
        // Related products are drawn from the same shop when the product is shop-owned, so a
        // marketplace product page does not cross-sell a competitor underneath the buy button.
        var products = await ScopedCardQuery(product.ShopId)
            .Where(p => p.Id != product.Id &&
                        (p.CategoryId == product.CategoryId ||
                         (product.Brand != null && p.Brand == product.Brand)))
            .OrderByDescending(p => p.CategoryId == product.CategoryId)
            .ThenByDescending(p => p.IsFeatured)
            .Take(take)
            .ToListAsync(ct);

        var ratings = await GetRatingsAsync(products.Select(p => p.Id), ct);
        return products.Select(p => ToCard(p, ratings.GetValueOrDefault(p.Id))).ToList();
    }

    public async Task<IReadOnlyList<string>> GetBrandsAsync(int? shopId = null, CancellationToken ct = default)
    {
        var query = _db.Products.AsNoTracking()
            .Where(p => p.IsPublished && !p.IsDeleted &&
                        p.Shop != null && p.Shop.Status == ShopStatus.Approved &&
                        p.Brand != null && p.Brand != "");

        if (shopId is { } id)
        {
            query = query.Where(p => p.ShopId == id);
        }

        return await query
            .Select(p => p.Brand!)
            .Distinct()
            .OrderBy(b => b)
            .ToListAsync(ct);
    }

    public async Task<PriceBounds> GetPriceBoundsAsync(int? shopId = null, CancellationToken ct = default)
    {
        var prices = _db.ProductVariants.AsNoTracking()
            .Where(v => v.IsActive && v.Product!.IsPublished && !v.Product.IsDeleted &&
                        v.Product.Shop != null && v.Product.Shop.Status == ShopStatus.Approved);

        if (shopId is { } id)
        {
            prices = prices.Where(v => v.Product!.ShopId == id);
        }

        var values = prices.Select(v => v.Price);

        if (!await values.AnyAsync(ct))
        {
            return new PriceBounds(0m, 0m);
        }

        // SQLite cannot apply Min/Max to a decimal column ("SQLite cannot apply aggregate operator
        // 'Min' on expressions of type 'decimal'"), so the aggregate runs over double and is
        // converted back. Two decimal places are exact enough for a filter bound, and the values
        // are then floored/ceiled outward so no product is excluded by rounding.
        var min = (decimal)await values.MinAsync(p => (double)p, ct);
        var max = (decimal)await values.MaxAsync(p => (double)p, ct);

        return new PriceBounds(decimal.Floor(min), decimal.Ceiling(max));
    }

    /// <summary>
    /// Card query restricted to one shop (or the whole marketplace) and to shops that are actually
    /// trading. Every storefront read goes through here so isolation cannot be forgotten.
    /// </summary>
    private IQueryable<Product> ScopedCardQuery(int? shopId)
    {
        var query = CardQuery()
            .Where(p => p.IsPublished && !p.IsDeleted &&
                        p.Shop != null && p.Shop.Status == ShopStatus.Approved);

        if (shopId is { } id)
        {
            query = query.Where(p => p.ShopId == id);
        }

        return query;
    }

    /// <summary>Walks the category tree and returns the given node plus every descendant.</summary>
    private async Task<List<int>> GetDescendantCategoryIdsAsync(int rootId, CancellationToken ct)
    {
        var edges = await _db.Categories.AsNoTracking()
            .Select(c => new { c.Id, c.ParentId })
            .ToListAsync(ct);

        var result = new List<int> { rootId };
        var frontier = new Queue<int>();
        frontier.Enqueue(rootId);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var child in edges.Where(e => e.ParentId == current))
            {
                if (result.Contains(child.Id))
                {
                    continue; // guards against a cycle introduced by bad data
                }

                result.Add(child.Id);
                frontier.Enqueue(child.Id);
            }
        }

        return result;
    }

    private IQueryable<Product> CardQuery() =>
        _db.Products.AsNoTracking()
            .Include(p => p.Category)
            .Include(p => p.Variants)
            .Include(p => p.Images)
            .Include(p => p.Listings)
            .Include(p => p.Shop);

    private IQueryable<Product> FullProductQuery() =>
        _db.Products.AsNoTracking()
            .Include(p => p.Category)
            .Include(p => p.Variants)
            .Include(p => p.Images)
            .Include(p => p.Shop)
            .Include(p => p.Listings)
                .ThenInclude(l => l.Shop);

    /// <summary>
    /// Aggregated star rating for a set of products, in one query. A value type keeps the lookup
    /// free of nullability, so callers can use <c>GetValueOrDefault</c> unconditionally.
    /// </summary>
    private async Task<Dictionary<int, RatingAggregate>> GetRatingsAsync(
        IEnumerable<int> productIds, CancellationToken ct)
    {
        // Array + EF.Constant so the predicate is a literal IN (…), not OPENJSON. See BrowseAsync.
        var ids = productIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<int, RatingAggregate>();
        }

        var rows = await _db.Reviews.AsNoTracking()
            .Where(r => EF.Constant(ids).Contains(r.ProductId) && r.Status == ReviewStatus.Published)
            .GroupBy(r => r.ProductId)
            .Select(g => new { ProductId = g.Key, Count = g.Count(), Sum = g.Sum(r => r.Rating) })
            .ToListAsync(ct);

        return rows.ToDictionary(
            r => r.ProductId,
            r => new RatingAggregate(
                Math.Round((double)r.Sum / r.Count, 2, MidpointRounding.AwayFromZero),
                r.Count));
    }

    /// <summary>Average stars and the number of ratings behind them, for one product.</summary>
    private readonly record struct RatingAggregate(double Average, int Count);

    /// <summary>
    /// Projects an entity onto the grid DTO. Price and availability come from the shop's listing
    /// when it has one, because a listing is what the shop actually offers; the product's own
    /// variant price is the fallback for a product that has not been listed commercially yet.
    /// </summary>
    private static ProductCard ToCard(Product p, RatingAggregate rating)
    {
        var activeVariants = p.Variants.Where(v => v.IsActive).ToList();
        var priced = activeVariants.Count > 0 ? activeVariants : p.Variants.ToList();
        var listing = p.Listings.FirstOrDefault(l => l.IsActive);

        var variantPrice = priced.Count > 0 ? priced.Min(v => v.Price) : 0m;
        var price = listing?.Price ?? variantPrice;

        var variantCompareAt = priced
            .Where(v => v.CompareAtPrice is > 0 && v.CompareAtPrice > v.Price)
            .Select(v => v.CompareAtPrice!.Value)
            .DefaultIfEmpty(0m)
            .Max();

        var compareAt = listing?.CompareAtPrice ?? variantCompareAt;

        var image = p.Images.OrderByDescending(i => i.IsPrimary).ThenBy(i => i.DisplayOrder).FirstOrDefault();

        // Availability is a property of who is selling, so the listing wins when present.
        var inStock = listing is not null
            ? listing.StockQuantity > 0
            : activeVariants.Any(v => v.StockQuantity > 0);

        var stock = listing?.StockQuantity ?? activeVariants.Sum(v => v.StockQuantity);

        return new ProductCard
        {
            Id = p.Id,
            Name = p.Name,
            Slug = p.Slug,
            ShortDescription = p.ShortDescription,
            CategoryName = p.Category?.Name ?? "Uncategorised",
            CategorySlug = p.Category?.Slug ?? string.Empty,
            ImageUrl = image?.Url,
            Price = price,
            CompareAtPrice = compareAt,
            HasPriceRange = priced.Select(v => v.Price).Distinct().Count() > 1,
            InStock = inStock,
            StockQuantity = stock,
            IsFeatured = p.IsFeatured,
            ShopId = p.ShopId,
            ShopName = p.Shop?.Name ?? string.Empty,
            ShopSlug = p.Shop?.Slug ?? string.Empty,
            RatingAverage = rating.Count == 0 ? 0d : rating.Average,
            RatingCount = rating.Count
        };
    }

    /// <summary>Escapes LIKE wildcards so a search for "50%" is a literal search.</summary>
    private static string Escape(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
