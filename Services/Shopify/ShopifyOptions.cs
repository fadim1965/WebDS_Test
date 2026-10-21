namespace WebDS_Test.Services.Shopify;

/// <summary>
/// Configuration for reading the catalogue from Shopify's Storefront API.
/// Bound from the <c>Shopify</c> section of appsettings.
/// </summary>
public sealed class ShopifyOptions
{
    public const string SectionName = "Shopify";

    /// <summary>
    /// Master switch. When false the app keeps using the local EF Core catalogue, so the SQLite
    /// store remains the default and Shopify is purely opt-in.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Shop domain, e.g. <c>your-store.myshopify.com</c>. Leading scheme is stripped.</summary>
    public string ShopDomain { get; set; } = string.Empty;

    /// <summary>
    /// Storefront API access token from a <em>Headless</em> channel (or a custom app with the
    /// Storefront API enabled). This is a public, client-safe token — it is NOT the Admin API
    /// access token, which must never be used server-side in a storefront.
    /// </summary>
    public string StorefrontAccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Pinned API version in <c>YYYY-MM</c> form. Shopify ships breaking changes quarterly, so
    /// this is pinned deliberately rather than tracking <c>latest</c>.
    /// </summary>
    public string ApiVersion { get; set; } = "2026-10";

    /// <summary>
    /// Serves catalogue responses from a local fixture instead of the network. Used when no token
    /// is configured so the adapter is runnable and testable without Shopify credentials.
    /// </summary>
    public bool UseMockData { get; set; }

    /// <summary>Optional market/language context passed through the <c>@inContext</c> directive.</summary>
    public string? CountryCode { get; set; }

    public string? LanguageCode { get; set; }

    /// <summary>Catalogue page size sent to Shopify; clamped by the client.</summary>
    public int MaxPageSize { get; set; } = 24;

    /// <summary>Per-request timeout for Storefront API calls.</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Short-lived cache for catalogue lookups that would otherwise cost a call per request.</summary>
    public int CacheSeconds { get; set; } = 60;

    /// <summary>Builds the GraphQL endpoint for the configured shop and pinned version.</summary>
    public string BuildEndpoint()
    {
        var domain = ShopDomain
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        return $"https://{domain}/api/{ApiVersion}/graphql.json";
    }

    /// <summary>True when the adapter has everything it needs to talk to a real shop.</summary>
    public bool IsLive => !UseMockData &&
                          !string.IsNullOrWhiteSpace(ShopDomain) &&
                          !string.IsNullOrWhiteSpace(StorefrontAccessToken);

    /// <summary>
    /// Resolves the effective mode, failing loudly if the adapter is enabled but unusable rather
    /// than silently serving an empty catalogue.
    /// </summary>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (!UseMockData && (string.IsNullOrWhiteSpace(ShopDomain) || string.IsNullOrWhiteSpace(StorefrontAccessToken)))
        {
            throw new InvalidOperationException(
                "Shopify:Enabled is true but Shopify:ShopDomain and/or Shopify:StorefrontAccessToken are missing. " +
                "Set both, or set Shopify:UseMockData to true to run the adapter against local fixture data.");
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(ApiVersion, @"^\d{4}-\d{2}$"))
        {
            throw new InvalidOperationException(
                $"Shopify:ApiVersion '{ApiVersion}' is not in YYYY-MM form (e.g. 2026-10).");
        }
    }
}
