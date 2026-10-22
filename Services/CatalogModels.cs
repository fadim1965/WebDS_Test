namespace WebDS_Test.Services;

/// <summary>A page of results plus the metadata a pager needs to render.</summary>
public sealed class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 12;
    public int TotalCount { get; init; }

    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;

    public int FirstItemNumber => TotalCount == 0 ? 0 : (int)Math.Min(((long)(Page - 1) * PageSize) + 1, TotalCount);
    public int LastItemNumber => (int)Math.Min((long)Page * PageSize, TotalCount);
}

/// <summary>
/// Paging arithmetic that cannot produce the negative OFFSET SQL Server rejects. A query string may
/// carry any page number at all, and <c>(page - 1) * pageSize</c> overflows a signed 32-bit int long
/// before a caller runs out of page numbers to invent.
/// </summary>
public static class Paging
{
    /// <summary>Clamps a page to one that exists, so paging past the end lands on the last page.</summary>
    public static int ClampPage(int page, int pageSize, int totalCount)
    {
        var size = Math.Max(1, pageSize);
        var lastPage = Math.Max(1, (int)Math.Ceiling(totalCount / (double)size));
        return Math.Clamp(page, 1, lastPage);
    }

    /// <summary>The rows to skip for a 1-based page. Never negative, and never overflowed.</summary>
    public static int Skip(int page, int pageSize)
    {
        var size = Math.Max(1, pageSize);
        var offset = (Math.Max(1, page) - 1L) * size;
        return offset >= int.MaxValue ? int.MaxValue : (int)offset;
    }
}

/// <summary>Filter, sort and paging inputs for a catalogue browse request.</summary>
public sealed class ProductQuery
{
    public string? Search { get; set; }
    public string? CategorySlug { get; set; }

    /// <summary>
    /// Restricts results to one shop. Null means the marketplace-wide view, which is the default on
    /// the home page; a shop storefront always sets this.
    /// </summary>
    public int? ShopId { get; set; }

    /// <summary>Restricts to a single shop by slug, resolved by the caller.</summary>
    public string? ShopSlug { get; set; }

    /// <summary>When true, products in descendant categories are included too.</summary>
    public bool IncludeSubcategories { get; set; } = true;

    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }
    public string? Brand { get; set; }
    public bool InStockOnly { get; set; }
    public bool FeaturedOnly { get; set; }

    /// <summary>
    /// Minimum average rating (1–5). A product with no ratings is excluded whenever this is set,
    /// which is what a shopper choosing "4 stars and up" expects.
    /// </summary>
    public int? MinRating { get; set; }

    public ProductSort Sort { get; set; } = ProductSort.Newest;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 12;
}

public enum ProductSort
{
    Newest = 0,
    PriceLowToHigh = 1,
    PriceHighToLow = 2,
    NameAscending = 3,
    BestSelling = 4,
    /// <summary>Highest average rating first — the "customer reviews" sort of a marketplace.</summary>
    TopRated = 5,
    /// <summary>Most reviewed first.</summary>
    MostReviewed = 6,
    /// <summary>
    /// Text-relevance first. Only meaningful with a search term; the catalogue falls back to it
    /// automatically when a query is present and no sort was chosen.
    /// </summary>
    Relevance = 7
}

public static class ProductSortExtensions
{
    /// <summary>Readable label for sort dropdowns (already humanised by <c>PrettyEnum</c> where used).</summary>
    public static string ToDisplay(this ProductSort sort) => sort switch
    {
        ProductSort.Newest => "Newest arrivals",
        ProductSort.PriceLowToHigh => "Price: low to high",
        ProductSort.PriceHighToLow => "Price: high to low",
        ProductSort.NameAscending => "Name: A–Z",
        ProductSort.BestSelling => "Best selling",
        ProductSort.TopRated => "Average customer review",
        ProductSort.MostReviewed => "Most reviewed",
        ProductSort.Relevance => "Relevance",
        _ => sort.ToString()
    };
}

/// <summary>A lightweight product projection for grids and carousels.</summary>
public sealed class ProductCard
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string? ShortDescription { get; init; }
    public string CategoryName { get; init; } = string.Empty;
    public string CategorySlug { get; init; } = string.Empty;
    public string? ImageUrl { get; init; }
    public decimal Price { get; init; }
    public decimal CompareAtPrice { get; init; }
    public bool HasPriceRange { get; init; }
    public bool InStock { get; init; }
    public int StockQuantity { get; init; }
    public bool IsFeatured { get; init; }

    /// <summary>Selling shop, so marketplace listings can attribute and link to it.</summary>
    public int ShopId { get; init; }
    public string ShopName { get; init; } = string.Empty;
    public string ShopSlug { get; init; } = string.Empty;

    /// <summary>Average star rating, or 0 when the product has not been reviewed yet.</summary>
    public double RatingAverage { get; init; }

    /// <summary>Number of published reviews behind <see cref="RatingAverage"/>.</summary>
    public int RatingCount { get; init; }

    public bool HasRating => RatingCount > 0;

    /// <summary>Whole-star value used by the five-star strip.</summary>
    public int RoundedStars => (int)Math.Round(RatingAverage, MidpointRounding.AwayFromZero);

    /// <summary>The star strip to render for this card.</summary>
    public StarRating Stars => new(RatingAverage, RatingCount);

    public bool IsOnSale => CompareAtPrice > Price && Price > 0m;

    public int DiscountPercent => IsOnSale
        ? (int)Math.Round((CompareAtPrice - Price) / CompareAtPrice * 100m, MidpointRounding.AwayFromZero)
        : 0;
}

/// <summary>A result row for the admin product list, including soft-deleted products.</summary>
public sealed class AdminProductRow
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string CategoryName { get; init; } = string.Empty;
    public string Brand { get; init; } = string.Empty;
    public int VariantCount { get; init; }
    public int TotalStock { get; init; }
    public decimal MinPrice { get; init; }
    public decimal MaxPrice { get; init; }
    public bool IsPublished { get; init; }

    /// <summary>Where the listing sits in the marketplace's review process.</summary>
    public ListingStatus ModerationStatus { get; init; } = ListingStatus.Published;

    /// <summary>Why an administrator turned the listing down, when they did.</summary>
    public string? ModerationReason { get; init; }
    public bool IsDeleted { get; init; }
    public bool IsFeatured { get; init; }
    public DateTime UpdatedUtc { get; init; }
}
