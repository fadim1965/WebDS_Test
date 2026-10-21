using System.Globalization;
using Microsoft.Extensions.Caching.Memory;

namespace WebDS_Test.Services.Shopify;

/// <summary>
/// Reads the storefront catalogue from Shopify's Storefront API instead of the local database.
/// </summary>
/// <remarks>
/// <para>
/// This is the step-2 port: <see cref="ICatalogService"/> is read-only, so it swaps cleanly and
/// leaves cart, checkout and orders on the existing implementation. Register it instead of
/// <c>CatalogService</c> to serve the catalogue from Shopify.
/// </para>
/// <para><b>Three places where Shopify's model does not match ours</b>, all handled explicitly:</para>
/// <list type="number">
/// <item>
/// <b>Paging.</b> <c>PagedResult&lt;T&gt;</c> is page-number based; the Storefront API is
/// cursor-based. There is no "skip N" argument, so reaching page N requires walking the cursors.
/// The walk is bounded (<see cref="MaxItemsWalked"/>) to keep rate-limit cost predictable.
/// </item>
/// <item>
/// <b>Price filtering.</b> Shopify cannot filter by price range in the <c>query</c> argument.
/// When a price filter is present the window is over-fetched and filtered in memory, and the
/// reported total is therefore an estimate — see <see cref="ShopifyQueryTranslator.TotalsAreApproximate"/>.
/// </item>
/// <item>
/// <b>Identifiers.</b> Shopify uses global IDs (<c>gid://shopify/ProductVariant/123</c>). The
/// domain model uses <c>int</c> keys, so the numeric tail is parsed into <c>Id</c> and the whole
/// GID is preserved on <c>ExternalId</c> so a later cart/checkout port can round-trip it.
/// </item>
/// </list>
/// </remarks>
public sealed class ShopifyCatalogService : ICatalogService
{
    /// <summary>
    /// Upper bound on nodes walked to satisfy an offset/page request. Beyond this the page is
    /// served from what has been walked and reports itself as partial, rather than issuing an
    /// unbounded number of GraphQL calls.
    /// </summary>
    private const int MaxItemsWalked = 1000;

    private readonly IShopifyStorefrontClient _shopify;
    private readonly ShopifyOptions _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ShopifyCatalogService> _logger;

    public ShopifyCatalogService(
        IShopifyStorefrontClient shopify,
        IOptions<ShopifyOptions> options,
        IMemoryCache cache,
        ILogger<ShopifyCatalogService> logger)
    {
        _shopify = shopify;
        _options = options.Value;
        _cache = cache;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // Browse
    // ------------------------------------------------------------------

    public async Task<PagedResult<ProductCard>> BrowseAsync(ProductQuery query, CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = ShopifyQueryTranslator.ResolvePageSize(query, _options.MaxPageSize);

        var collections = await GetCollectionsAsync(ct);
        var category = string.IsNullOrWhiteSpace(query.CategorySlug)
            ? null
            : collections.FirstOrDefault(c => string.Equals(c.Handle, query.CategorySlug, StringComparison.OrdinalIgnoreCase));

        // A slug that resolves to a collection browses that collection; otherwise the slug is
        // treated as a product type through the search translator.
        var useCollection = category is not null && string.IsNullOrWhiteSpace(query.Search) && !query.FeaturedOnly;
        var (sortKey, reverse) = ShopifyQueryTranslator.MapSort(query.Sort);

        var priceFiltered = ShopifyQueryTranslator.RequiresClientSidePriceFilter(query);
        var queryArgument = useCollection ? null : ShopifyQueryTranslator.BuildQueryArgument(query);

        // With a client-side price filter the true count is unknown until everything is filtered,
        // so walk a wider window and filter locally.
        var targetCount = priceFiltered
            ? MaxItemsWalked
            : Math.Min(MaxItemsWalked, page * pageSize);

        var walk = await WalkProductsAsync(queryArgument, category?.Handle, useCollection, sortKey, reverse, targetCount, ct);

        IEnumerable<ProductCard> cards = walk.Products;

        if (priceFiltered)
        {
            cards = ShopifyQueryTranslator.ApplyPriceFilter(cards, query);
        }

        var materialised = cards.ToList();

        var totalCount = priceFiltered
            ? materialised.Count
            : walk.TotalCount ?? materialised.Count;

        page = Paging.ClampPage(page, pageSize, totalCount);

        var window = materialised
            .Skip(Paging.Skip(page, pageSize))
            .Take(pageSize)
            .ToList();

        if (priceFiltered)
        {
            _logger.LogDebug(
                "Shopify catalogue browse applied a client-side price filter over {Walked} products; reported total is an estimate",
                materialised.Count);
        }

        return new PagedResult<ProductCard>
        {
            Items = window,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    /// <summary>
    /// Walks a product connection until <paramref name="targetCount"/> nodes are collected or the
    /// connection ends. Each hop is one GraphQL call, which is the cost of number-based paging
    /// against a cursor API.
    /// </summary>
    private async Task<WalkResult> WalkProductsAsync(
        string? queryArgument, string? collectionHandle, bool useCollection,
        string sortKey, bool reverse, int targetCount, CancellationToken ct)
    {
        var collected = new List<ShopifyDtos.Product>();
        string? cursor = null;
        int? totalCount = null;
        var hops = 0;
        var maxHops = (int)Math.Ceiling(targetCount / (double)Math.Min(250, Math.Max(1, _options.MaxPageSize))) + 1;

        while (collected.Count < targetCount && hops < maxHops)
        {
            hops++;
            var first = Math.Min(250, Math.Max(targetCount - collected.Count, _options.MaxPageSize));

            if (useCollection && !string.IsNullOrWhiteSpace(collectionHandle))
            {
                var data = await _shopify.QueryAsync<CollectionProductsData>(
                    ShopifyQueries.BrowseByCollection,
                    new { handle = collectionHandle, first, after = cursor, sortKey, reverse }, ct);

                var connection = data.Collection?.Products;
                if (connection?.Nodes is null || connection.Nodes.Count == 0)
                {
                    break;
                }

                collected.AddRange(connection.Nodes);
                cursor = connection.PageInfo?.EndCursor;

                if (connection.PageInfo?.HasNextPage != true)
                {
                    break;
                }
            }
            else
            {
                var data = await _shopify.QueryAsync<ShopifyDtos.ProductsData>(
                    ShopifyQueries.Browse,
                    new { first, after = cursor, query = queryArgument, sortKey, reverse }, ct);

                var connection = data.Products;
                totalCount ??= connection?.TotalCount;

                if (connection?.Nodes is null || connection.Nodes.Count == 0)
                {
                    break;
                }

                collected.AddRange(connection.Nodes);
                cursor = connection.PageInfo?.EndCursor;

                if (connection.PageInfo?.HasNextPage != true)
                {
                    break;
                }
            }
        }

        return new WalkResult(collected.Select(p => MapToCard(p)).ToList(), totalCount);
    }

    private sealed record WalkResult(IReadOnlyList<ProductCard> Products, int? TotalCount);

    // ------------------------------------------------------------------
    // Single product
    // ------------------------------------------------------------------

    public async Task<Product?> GetProductBySlugAsync(string slug, int? shopId = null, CancellationToken ct = default)
    {
        if (shopId is not null)
        {
            // The Shopify provider fronts a single shop, so a marketplace-scoped lookup is
            // meaningless against it. Returning null is honest; the local provider serves the
            // marketplace. See the class remarks for why the two providers are not interchangeable
            // once the marketplace is in play.
            _logger.LogWarning("Shop-scoped product lookup requested against the Shopify provider; returning null");
            return null;
        }

        var data = await _shopify.QueryAsync<ShopifyDtos.ProductByHandleData>(
            ShopifyQueries.ProductByHandle, new { handle = slug }, ct);

        return data.Product is null ? null : MapToProduct(data.Product);
    }

    /// <summary>
    /// Not supported against Shopify: the Storefront API addresses products by handle, and
    /// numeric ids are only meaningful in the local database. Returns null rather than
    /// fabricating a product.
    /// </summary>
    public Task<Product?> GetProductByIdAsync(int id, CancellationToken ct = default)
    {
        _logger.LogWarning(
            "GetProductByIdAsync({Id}) is not supported by the Shopify catalogue provider; use the handle-based lookup",
            id);

        return Task.FromResult<Product?>(null);
    }

    // ------------------------------------------------------------------
    // Categories
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<Category>> GetCategoryTreeAsync(int? shopId = null, CancellationToken ct = default)
        => BuildTree(await GetCollectionsAsync(ct));

    public async Task<IReadOnlyList<Category>> GetActiveCategoriesAsync(int? shopId = null, CancellationToken ct = default)
        => BuildTree(await GetCollectionsAsync(ct));

    public async Task<Category?> GetCategoryBySlugAsync(string slug, int? shopId = null, CancellationToken ct = default)
    {
        var collections = await GetCollectionsAsync(ct);
        return BuildTree(collections)
            .FirstOrDefault(c => string.Equals(c.Slug, slug, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Shopify collections form a shallow tree (a collection may have one parent). Mapped onto the
    /// domain's self-referencing <see cref="Category"/>, depth is at most two levels here.
    /// </summary>
    private static IReadOnlyList<Category> BuildTree(IReadOnlyList<ShopifyDtos.Collection> collections)
    {
        var byHandle = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);
        var order = 0;

        foreach (var collection in collections)
        {
            if (string.IsNullOrWhiteSpace(collection.Handle))
            {
                continue;
            }

            byHandle[collection.Handle] = new Category
            {
                Id = StableId(collection.Id),
                Name = collection.Title ?? collection.Handle,
                Slug = collection.Handle,
                Description = collection.Description,
                DisplayOrder = order++,
                IsActive = true
            };
        }

        return byHandle.Values.OrderBy(c => c.DisplayOrder).ToList();
    }

    private async Task<IReadOnlyList<ShopifyDtos.Collection>> GetCollectionsAsync(CancellationToken ct)
    {
        var cacheKey = $"shopify:collections:{_options.ShopDomain}";

        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<ShopifyDtos.Collection>? cached) && cached is not null)
        {
            return cached;
        }

        var data = await _shopify.QueryAsync<ShopifyDtos.CollectionsData>(
            ShopifyQueries.Collections, new { first = 250 }, ct);

        var collections = data.Collections?.Nodes ?? new List<ShopifyDtos.Collection>();

        _cache.Set(cacheKey, (IReadOnlyList<ShopifyDtos.Collection>)collections,
            TimeSpan.FromSeconds(Math.Max(5, _options.CacheSeconds)));

        return collections;
    }

    // ------------------------------------------------------------------
    // Home page surfaces
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<ProductCard>> GetFeaturedAsync(int take = 8, int? shopId = null, CancellationToken ct = default)
    {
        var data = await _shopify.QueryAsync<HomeProductsData>(
            ShopifyQueries.HomePage, new { first = Math.Clamp(take, 1, 50), featuredQuery = "tag:featured" }, ct);

        var featured = data.Featured?.Nodes;
        var source = featured is { Count: > 0 } ? featured : data.Newest?.Nodes;

        return (source ?? new List<ShopifyDtos.Product>())
            .Take(take)
            .Select(p => MapToCard(p))
            .ToList();
    }

    public async Task<IReadOnlyList<ProductCard>> GetBestSellersAsync(int take = 8, int? shopId = null, CancellationToken ct = default)
    {
        var data = await _shopify.QueryAsync<HomeProductsData>(
            ShopifyQueries.HomePage, new { first = Math.Clamp(take, 1, 50), featuredQuery = (string?)null }, ct);

        var source = data.BestSelling?.Nodes ?? data.Newest?.Nodes ?? new List<ShopifyDtos.Product>();
        return source.Take(take).Select(p => MapToCard(p)).ToList();
    }

    public async Task<IReadOnlyList<ProductCard>> GetRelatedAsync(Product product, int take = 4, CancellationToken ct = default)
    {
        // Related products are sought in the same customer-facing category, which for Shopify is
        // the product type. The current product is excluded with a negative search term.
        var queryArgument = string.IsNullOrWhiteSpace(product.Category?.Slug)
            ? $"-handle:{ShopifyQueryTranslator.Quote(product.Slug)}"
            : $"product_type:{ShopifyQueryTranslator.Quote(product.Category.Slug)} AND -handle:{ShopifyQueryTranslator.Quote(product.Slug)}";

        var data = await _shopify.QueryAsync<ShopifyDtos.ProductsData>(
            ShopifyQueries.Browse,
            new { first = Math.Clamp(take, 1, 20), after = (string?)null, query = queryArgument, sortKey = "BEST_SELLING", reverse = false },
            ct);

        return (data.Products?.Nodes ?? new List<ShopifyDtos.Product>())
            .Take(take)
            .Select(p => MapToCard(p))
            .ToList();
    }

    // ------------------------------------------------------------------
    // Filter metadata
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<string>> GetBrandsAsync(int? shopId = null, CancellationToken ct = default)
    {
        var cacheKey = $"shopify:brands:{_options.ShopDomain}";

        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<string>? cached) && cached is not null)
        {
            return cached;
        }

        // The Storefront API has no "list all vendors" query, so vendors are derived from a
        // bounded window of products and then cached.
        var data = await _shopify.QueryAsync<ShopifyDtos.ProductsData>(
            ShopifyQueries.Browse,
            new { first = 250, after = (string?)null, query = (string?)null, sortKey = "TITLE", reverse = false }, ct);

        var brands = (data.Products?.Nodes ?? new List<ShopifyDtos.Product>())
            .Select(p => p.Vendor)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _cache.Set(cacheKey, (IReadOnlyList<string>)brands, TimeSpan.FromSeconds(Math.Max(5, _options.CacheSeconds)));

        return brands;
    }

    public async Task<PriceBounds> GetPriceBoundsAsync(int? shopId = null, CancellationToken ct = default)
    {
        var cacheKey = $"shopify:pricebounds:{_options.ShopDomain}";

        if (_cache.TryGetValue(cacheKey, out PriceBounds cached))
        {
            return cached;
        }

        var data = await _shopify.QueryAsync<ShopifyDtos.ProductsData>(
            ShopifyQueries.Browse,
            new { first = 250, after = (string?)null, query = (string?)null, sortKey = "PRICE", reverse = false }, ct);

        var nodes = data.Products?.Nodes ?? new List<ShopifyDtos.Product>();
        var prices = nodes
            .Select(p => p.PriceRange?.MinVariantPrice?.ToDecimal() ?? 0m)
            .Where(p => p > 0m)
            .ToList();

        var bounds = prices.Count == 0
            ? new PriceBounds(0m, 0m)
            : new PriceBounds(decimal.Floor(prices.Min()), decimal.Ceiling(prices.Max()));

        _cache.Set(cacheKey, bounds, TimeSpan.FromSeconds(Math.Max(5, _options.CacheSeconds)));

        return bounds;
    }

    // ------------------------------------------------------------------
    // Mapping
    // ------------------------------------------------------------------

    /// <summary>Projects a Storefront product onto the grid DTO the views already consume.</summary>
    private static ProductCard MapToCard(ShopifyDtos.Product p)
    {
        var variants = p.Variants?.Nodes ?? new List<ShopifyDtos.Variant>();

        var prices = variants
            .Select(v => v.Price?.ToDecimal() ?? 0m)
            .Where(price => price > 0m)
            .ToList();

        var price = prices.Count > 0
            ? prices.Min()
            : p.PriceRange?.MinVariantPrice?.ToDecimal() ?? 0m;

        // A "was" price only counts as a sale when it beats the price being charged.
        var compareAt = variants
            .Select(v => v.CompareAtPrice?.ToDecimal() ?? 0m)
            .Where(was => was > price)
            .DefaultIfEmpty(0m)
            .Max();

        var categoryName = !string.IsNullOrWhiteSpace(p.ProductType) ? p.ProductType! : "Uncategorised";
        var inStock = p.AvailableForSale
                      ?? variants.Any(v => v.AvailableForSale == true);

        return new ProductCard
        {
            Id = StableId(p.Id),
            Name = p.Title ?? "(untitled)",
            Slug = p.Handle ?? string.Empty,
            ShortDescription = Summarise(p.Description),
            CategoryName = categoryName,
            CategorySlug = Slugify(categoryName),
            ImageUrl = p.FeaturedImage?.Url ?? p.Images?.Nodes?.FirstOrDefault()?.Url,
            Price = price,
            CompareAtPrice = compareAt,
            HasPriceRange = prices.Count > 1 && prices.Distinct().Count() > 1,
            InStock = inStock,
            StockQuantity = p.TotalInventory ?? Math.Max(0, variants.Sum(v => v.QuantityAvailable ?? 0)),
            IsFeatured = p.Tags?.Any(t => string.Equals(t, "featured", StringComparison.OrdinalIgnoreCase)) == true
        };
    }

    /// <summary>Projects a Storefront product onto the domain entity the detail view consumes.</summary>
    private Product MapToProduct(ShopifyDtos.Product p)
    {
        var product = new Product
        {
            Id = StableId(p.Id),
            Name = p.Title ?? "(untitled)",
            Slug = p.Handle ?? string.Empty,
            ShortDescription = Summarise(p.Description),
            // The Storefront API returns HTML for descriptionHtml; the detail view renders
            // Description as pre-line plain text, so the plain-text field is used here.
            Description = p.Description,
            Brand = p.Vendor,
            CategoryId = 0,
            Category = new Category
            {
                Id = 0,
                Name = string.IsNullOrWhiteSpace(p.ProductType) ? "Uncategorised" : p.ProductType!,
                Slug = Slugify(p.ProductType ?? "uncategorised"),
                IsActive = true
            },
            IsPublished = true,
            IsDeleted = false,
            IsFeatured = p.Tags?.Any(t => string.Equals(t, "featured", StringComparison.OrdinalIgnoreCase)) == true,
            CreatedUtc = p.CreatedAt?.UtcDateTime ?? DateTime.UtcNow,
            UpdatedUtc = p.UpdatedAt?.UtcDateTime ?? DateTime.UtcNow
        };

        foreach (var (image, index) in (p.Images?.Nodes ?? new List<ShopifyDtos.Image>()).Select((image, i) => (image, i)))
        {
            product.Images.Add(new ProductImage
            {
                Url = image.Url ?? string.Empty,
                AltText = image.AltText,
                DisplayOrder = index,
                IsPrimary = index == 0
            });
        }

        if (product.Images.Count == 0 && p.FeaturedImage?.Url is { } featured)
        {
            product.Images.Add(new ProductImage { Url = featured, AltText = p.Title, DisplayOrder = 0, IsPrimary = true });
        }

        foreach (var v in p.Variants?.Nodes ?? new List<ShopifyDtos.Variant>())
        {
            var options = v.SelectedOptions?
                .Where(o => !string.IsNullOrWhiteSpace(o.Value) && !string.Equals(o.Value, "Default Title", StringComparison.OrdinalIgnoreCase))
                .Select(o => o.Value!)
                .ToList() ?? new List<string>();

            product.Variants.Add(new ProductVariant
            {
                Id = StableId(v.Id),
                ProductId = product.Id,
                Sku = v.Sku ?? string.Empty,
                Name = options.Count > 0 ? string.Join(" / ", options) : (v.Title ?? string.Empty),
                Price = v.Price?.ToDecimal() ?? 0m,
                CompareAtPrice = v.CompareAtPrice?.ToDecimal(),
                StockQuantity = v.QuantityAvailable ?? (v.AvailableForSale == true ? 1 : 0),
                IsActive = true,
                LowStockThreshold = 5
            });
        }

        return product;
    }

    /// <summary>
    /// Converts a Shopify global id (<c>gid://shopify/ProductVariant/123</c>) into the domain's
    /// integer key. Non-numeric or unusually large ids fall back to a stable hash so the same
    /// Shopify entity always maps to the same integer within a session.
    /// </summary>
    private static int StableId(string? gid)
    {
        if (string.IsNullOrWhiteSpace(gid))
        {
            return 0;
        }

        var tail = gid[(gid.LastIndexOf('/') + 1)..];

        if (int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric) && numeric >= 0)
        {
            return numeric;
        }

        // Deterministic fallback: never use string.GetHashCode(), which is randomised per process.
        unchecked
        {
            var hash = 17;
            foreach (var ch in gid)
            {
                hash = (hash * 31) + ch;
            }

            return hash & 0x7FFFFFFF;
        }
    }

    /// <summary>First sentence or first 160 characters, for the card's two-line teaser.</summary>
    private static string? Summarise(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var text = description.Trim().ReplaceLineEndings(" ");

        var stop = text.IndexOf(". ", StringComparison.Ordinal);
        if (stop is > 40 and < 160)
        {
            return text[..(stop + 1)];
        }

        return text.Length <= 160 ? text : text[..157] + "…";
    }

    /// <summary>Local slugifier. Does not depend on <see cref="ISlugService"/> so the provider is self-contained.</summary>
    private static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length);

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }

    // ---- Extra payload roots not shared with the DTO file -------------

    private sealed class HomeProductsData
    {
        [System.Text.Json.Serialization.JsonPropertyName("featured")] public ShopifyDtos.Connection<ShopifyDtos.Product>? Featured { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("newest")] public ShopifyDtos.Connection<ShopifyDtos.Product>? Newest { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("bestSelling")] public ShopifyDtos.Connection<ShopifyDtos.Product>? BestSelling { get; set; }
    }

    private sealed class CollectionProductsData
    {
        [System.Text.Json.Serialization.JsonPropertyName("collection")] public CollectionWithProducts? Collection { get; set; }
    }

    private sealed class CollectionWithProducts
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")] public string? Id { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("handle")] public string? Handle { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("title")] public string? Title { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("description")] public string? Description { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("products")] public ShopifyDtos.Connection<ShopifyDtos.Product>? Products { get; set; }
    }
}
