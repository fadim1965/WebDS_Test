using System.Text;

namespace WebDS_Test.Services.Shopify;

/// <summary>
/// Translates the application's <see cref="ProductQuery"/> into Storefront API arguments.
/// </summary>
/// <remarks>
/// The important asymmetry to understand: the Storefront API has no notion of our price-range or
/// brand dropdowns as first-class filters. Free-text search, <c>product_type</c>, <c>tag</c>,
/// <c>vendor</c> and <c>available_for_sale</c> are supported in the <c>query</c> argument;
/// a price range is not. Rather than silently ignore the price filter (which would show shoppers
/// products they filtered out), <see cref="RequiresClientSidePriceFilter"/> reports it and the
/// catalogue service then filters and paginates in memory over an over-fetched window.
/// </remarks>
internal static class ShopifyQueryTranslator
{
    /// <summary>Hard ceiling Shopify allows for a single <c>first:</c> argument.</summary>
    internal const int MaxConnectionSize = 250;

    /// <summary>Builds the <c>query</c> argument, or null when there is nothing to filter on.</summary>
    internal static string? BuildQueryArgument(ProductQuery query)
    {
        var terms = new List<string>();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // A bare term is treated as free text across title, vendor, product type and tags.
            terms.Add(Quote(query.Search.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(query.CategorySlug))
        {
            // Categories map onto Shopify product types. A deployment that models them as
            // collections instead is handled by ShopifyCatalogService, which switches to the
            // collection query when the slug resolves to a collection handle.
            terms.Add($"product_type:{Quote(query.CategorySlug)}");
        }

        if (!string.IsNullOrWhiteSpace(query.Brand))
        {
            terms.Add($"vendor:{Quote(query.Brand)}");
        }

        if (query.InStockOnly)
        {
            terms.Add("available_for_sale:true");
        }

        if (query.FeaturedOnly)
        {
            // Featured-ness is expressed as a tag in Shopify, since it has no such field.
            terms.Add("tag:featured");
        }

        return terms.Count == 0 ? null : string.Join(" AND ", terms);
    }

    /// <summary>
    /// Maps our sort order onto <c>ProductSortKeys</c>. Returns the key and whether the result
    /// must be reversed.
    /// </summary>
    internal static (string SortKey, bool Reverse) MapSort(ProductSort productSort) => productSort switch
    {
        ProductSort.PriceLowToHigh => ("PRICE", false),
        ProductSort.PriceHighToLow => ("PRICE", true),
        ProductSort.NameAscending => ("TITLE", false),
        ProductSort.BestSelling => ("BEST_SELLING", false),
        _ => ("CREATED_AT", true)
    };

    /// <summary>
    /// True when the filter cannot be expressed in the Storefront query argument and therefore has
    /// to be applied to the fetched window in memory.
    /// </summary>
    internal static bool RequiresClientSidePriceFilter(ProductQuery query) =>
        query.MinPrice is not null || query.MaxPrice is not null;

    /// <summary>
    /// True when a price filter is in play and it is not safe to trust the server-side total.
    /// A client-side filter changes the real result count, so the page must report an estimate
    /// rather than Shopify's <c>totalCount</c>.
    /// </summary>
    internal static bool TotalsAreApproximate(ProductQuery query) => RequiresClientSidePriceFilter(query);

    /// <summary>Applies a price range to an already-materialised card list.</summary>
    internal static IEnumerable<ProductCard> ApplyPriceFilter(IEnumerable<ProductCard> cards, ProductQuery query)
    {
        if (query.MinPrice is { } min)
        {
            cards = cards.Where(c => c.Price >= min);
        }

        if (query.MaxPrice is { } max)
        {
            cards = cards.Where(c => c.Price <= max);
        }

        return cards;
    }

    /// <summary>
    /// Escapes a value for the search-syntax string. Shopify uses quoted phrases, so embedded
    /// double quotes and backslashes must be escaped rather than dropped.
    /// </summary>
    internal static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');

        foreach (var ch in value)
        {
            if (ch is '"' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(ch);
        }

        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>
    /// Converts a page number into a <c>first:</c> plus a cursor. A cursor is only obtainable by
    /// walking the connection, so page 1 is cursor-free and later pages walk forward from it.
    /// </summary>
    internal static int ResolvePageSize(ProductQuery query, int maxPageSize) =>
        Math.Clamp(query.PageSize <= 0 ? 12 : query.PageSize, 1, Math.Min(maxPageSize, MaxConnectionSize));

    /// <summary>Number of items to skip to reach <paramref name="page"/>.</summary>
    internal static int OffsetFor(ProductQuery query) =>
        Math.Max(0, (Math.Max(1, query.Page) - 1) * ResolvePageSize(query, MaxConnectionSize));
}
