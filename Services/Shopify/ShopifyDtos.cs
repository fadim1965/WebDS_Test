using System.Text.Json.Serialization;

namespace WebDS_Test.Services.Shopify;

/// <summary>
/// Response shapes for the Storefront API queries in <see cref="ShopifyQueries"/>.
/// Every field is nullable: the client must tolerate a pinned API version that omits a field
/// rather than throwing at deserialization time.
/// </summary>
internal static class ShopifyDtos
{
    internal sealed class Money
    {
        [JsonPropertyName("amount")] public string? Amount { get; set; }
        [JsonPropertyName("currencyCode")] public string? CurrencyCode { get; set; }

        /// <summary>Parses the decimal string Shopify returns. Invariant culture, always.</summary>
        public decimal ToDecimal() =>
            decimal.TryParse(Amount, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : 0m;
    }

    internal sealed class Image
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("altText")] public string? AltText { get; set; }
        [JsonPropertyName("width")] public int? Width { get; set; }
        [JsonPropertyName("height")] public int? Height { get; set; }
    }

    internal sealed class PriceRange
    {
        [JsonPropertyName("minVariantPrice")] public Money? MinVariantPrice { get; set; }
        [JsonPropertyName("maxVariantPrice")] public Money? MaxVariantPrice { get; set; }
    }

    internal sealed class Variant
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("sku")] public string? Sku { get; set; }
        [JsonPropertyName("availableForSale")] public bool? AvailableForSale { get; set; }

        /// <summary>
        /// Only returned when inventory tracking is enabled and the channel is permitted to read
        /// it; treat as advisory and fall back to <see cref="AvailableForSale"/>.
        /// </summary>
        [JsonPropertyName("quantityAvailable")] public int? QuantityAvailable { get; set; }

        [JsonPropertyName("price")] public Money? Price { get; set; }
        [JsonPropertyName("compareAtPrice")] public Money? CompareAtPrice { get; set; }
        [JsonPropertyName("image")] public Image? Image { get; set; }

        [JsonPropertyName("selectedOptions")] public List<SelectedOption>? SelectedOptions { get; set; }
    }

    internal sealed class SelectedOption
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("value")] public string? Value { get; set; }
    }

    internal sealed class Product
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("handle")] public string? Handle { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("descriptionHtml")] public string? DescriptionHtml { get; set; }
        [JsonPropertyName("vendor")] public string? Vendor { get; set; }
        [JsonPropertyName("productType")] public string? ProductType { get; set; }
        [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
        [JsonPropertyName("createdAt")] public DateTimeOffset? CreatedAt { get; set; }
        [JsonPropertyName("updatedAt")] public DateTimeOffset? UpdatedAt { get; set; }
        [JsonPropertyName("publishedAt")] public DateTimeOffset? PublishedAt { get; set; }
        [JsonPropertyName("availableForSale")] public bool? AvailableForSale { get; set; }
        [JsonPropertyName("totalInventory")] public int? TotalInventory { get; set; }
        [JsonPropertyName("featuredImage")] public Image? FeaturedImage { get; set; }
        [JsonPropertyName("images")] public Connection<Image>? Images { get; set; }
        [JsonPropertyName("variants")] public Connection<Variant>? Variants { get; set; }
        [JsonPropertyName("priceRange")] public PriceRange? PriceRange { get; set; }
    }

    internal sealed class Collection
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("handle")] public string? Handle { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("updatedAt")] public DateTimeOffset? UpdatedAt { get; set; }
    }

    /// <summary>Generic Relay-style connection: <c>nodes</c> plus <c>pageInfo</c>.</summary>
    internal class Connection<T>
    {
        [JsonPropertyName("nodes")] public List<T>? Nodes { get; set; }
        [JsonPropertyName("pageInfo")] public PageInfo? PageInfo { get; set; }
    }

    internal sealed class PageInfo
    {
        [JsonPropertyName("hasNextPage")] public bool? HasNextPage { get; set; }
        [JsonPropertyName("hasPreviousPage")] public bool? HasPreviousPage { get; set; }
        [JsonPropertyName("startCursor")] public string? StartCursor { get; set; }
        [JsonPropertyName("endCursor")] public string? EndCursor { get; set; }
    }

    // ---- Query payload roots ------------------------------------------

    internal sealed class ProductsData
    {
        [JsonPropertyName("products")] public ProductConnection? Products { get; set; }
    }

    internal sealed class ProductConnection : Connection<Product>
    {
        [JsonPropertyName("totalCount")] public int? TotalCount { get; set; }
    }

    internal sealed class CollectionsData
    {
        [JsonPropertyName("collections")] public Connection<Collection>? Collections { get; set; }
    }

    internal sealed class ProductByHandleData
    {
        [JsonPropertyName("product")] public Product? Product { get; set; }
    }
}
