namespace WebDS_Test.Services.Shopify;

/// <summary>
/// Storefront API GraphQL documents. Kept as constants so they can be reviewed and diffed against
/// Shopify's schema when the pinned <c>ApiVersion</c> is upgraded.
/// </summary>
/// <remarks>
/// Fragments are used deliberately: the same product shape is needed by six different queries, and
/// a field added to <c>ProductCard</c> should only have to be added in one place.
/// </remarks>
internal static class ShopifyQueries
{
    /// <summary>Fields the storefront needs to build a <c>ProductCard</c>.</summary>
    private const string ProductCardFields = """
        fragment ProductCardFields on Product {
          id
          handle
          title
          description
          vendor
          productType
          tags
          createdAt
          updatedAt
          availableForSale
          totalInventory
          featuredImage { url altText width height }
          priceRange {
            minVariantPrice { amount currencyCode }
            maxVariantPrice { amount currencyCode }
          }
          images(first: 1) { nodes { url altText width height } }
          variants(first: 50) {
            nodes {
              id
              title
              sku
              availableForSale
              quantityAvailable
              price { amount currencyCode }
              compareAtPrice { amount currencyCode }
              image { url altText width height }
              selectedOptions { name value }
            }
          }
        }
        """;

    /// <summary>Fields needed for the product detail page.</summary>
    private const string ProductDetailFields = """
        fragment ProductDetailFields on Product {
          id
          handle
          title
          description
          descriptionHtml
          vendor
          productType
          tags
          createdAt
          updatedAt
          publishedAt
          availableForSale
          totalInventory
          featuredImage { url altText width height }
          priceRange {
            minVariantPrice { amount currencyCode }
            maxVariantPrice { amount currencyCode }
          }
          images(first: 20) { nodes { url altText width height } }
          variants(first: 100) {
            nodes {
              id
              title
              sku
              availableForSale
              quantityAvailable
              price { amount currencyCode }
              compareAtPrice { amount currencyCode }
              image { url altText width height }
              selectedOptions { name value }
            }
          }
        }
        """;

    /// <summary>Paged product browse. <c>$query</c> carries the translated search filters.</summary>
    public static readonly string Browse = $$"""
        query BrowseProducts($first: Int!, $after: String, $query: String, $sortKey: ProductSortKeys, $reverse: Boolean) {
          products(first: $first, after: $after, query: $query, sortKey: $sortKey, reverse: $reverse) {
            totalCount
            pageInfo { hasNextPage hasPreviousPage startCursor endCursor }
            nodes { ...ProductCardFields }
          }
        }
        {{ProductCardFields}}
        """;

    /// <summary>Products filtered to a single collection, used for category browsing.</summary>
    public static readonly string BrowseByCollection = $$"""
        query BrowseCollection($handle: String!, $first: Int!, $after: String, $sortKey: ProductSortKeys, $reverse: Boolean) {
          collection(handle: $handle) {
            id
            handle
            title
            description
            products(first: $first, after: $after, sortKey: $sortKey, reverse: $reverse) {
              pageInfo { hasNextPage hasPreviousPage startCursor endCursor }
              nodes { ...ProductCardFields }
            }
          }
        }
        {{ProductCardFields}}
        """;

    /// <summary>Single product by handle, for the detail page.</summary>
    public static readonly string ProductByHandle = $$"""
        query ProductByHandle($handle: String!) {
          product(handle: $handle) {
            ...ProductDetailFields
          }
        }
        {{ProductDetailFields}}
        """;

    /// <summary>
    /// Featured and newest products in one round trip. Two aliased connections avoid paying two
    /// separate rate-limit charges for what the home page renders together.
    /// </summary>
    public static readonly string HomePage = $$"""
        query HomeProducts($first: Int!, $featuredQuery: String) {
          featured: products(first: $first, query: $featuredQuery, sortKey: UPDATED_AT, reverse: true) {
            nodes { ...ProductCardFields }
          }
          newest: products(first: $first, sortKey: CREATED_AT, reverse: true) {
            nodes { ...ProductCardFields }
          }
          bestSelling: products(first: $first, sortKey: BEST_SELLING) {
            nodes { ...ProductCardFields }
          }
        }
        {{ProductCardFields}}
        """;

    /// <summary>All collections, used to build the category tree and navigation.</summary>
    public const string Collections = """
        query Collections($first: Int!) {
          collections(first: $first, sortKey: TITLE) {
            nodes { id handle title description updatedAt }
          }
        }
        """;

    /// <summary>Products in a collection, used for related-product suggestions.</summary>
    public static readonly string RelatedByCollection = $$"""
        query RelatedProducts($handle: String!, $first: Int!, $excludeHandle: String) {
          collection(handle: $handle) {
            products(first: $first, query: $excludeHandle, sortKey: BEST_SELLING) {
              nodes { ...ProductCardFields }
            }
          }
        }
        {{ProductCardFields}}
        """;
}
