namespace WebDS_Test.Domain;

/// <summary>
/// Storefront policy knobs bound from configuration (<c>Store</c> section in appsettings).
/// </summary>
public sealed class StoreSettings
{
    public const string SectionName = "Store";

    /// <summary>Shown across the storefront chrome and used as the sender name.</summary>
    public string Name { get; set; } = "LibanPost eShop";

    public string Tagline { get; set; } = "Postal services and supplies, delivered across Lebanon.";

    public string SupportEmail { get; set; } = "support@libanpost.example";

    /// <summary>Country the store primarily ships to; shown in address defaults and copy.</summary>
    public string DefaultCountry { get; set; } = "Lebanon";

    public string CurrencySymbol { get; set; } = "$";

    /// <summary>ISO code recorded against payments.</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>
    /// Alternate display currency shown beside base prices as an indicative conversion
    /// (today: Lebanese pounds). The base currency stays the money of record — charged,
    /// refunded and settled amounts; the alternate figure is display-only arithmetic.
    /// </summary>
    public string AlternateCurrency { get; set; } = "LBP";

    /// <summary>Abbreviation shown after the alternate figure, e.g. LL for Lebanese pounds.</summary>
    public string AlternateCurrencySymbol { get; set; } = "LL";

    /// <summary>
    /// How many alternate units one base (USD) unit buys — the bootstrap default only. The
    /// authoritative value lives in the site-settings table and is maintained on the admin
    /// settings screen; a rate of zero or less switches the alternate figure off everywhere.
    /// </summary>
    public decimal AlternateRate { get; set; } = 89_500m;

    /// <summary>Flat delivery rate. Lebanon-wide delivery is a realistic default for this brand.</summary>
    public decimal FlatShippingRate { get; set; } = 4.50m;

    /// <summary>Order subtotal at or above which shipping is free.</summary>
    public decimal FreeShippingThreshold { get; set; } = 60.00m;

    /// <summary>VAT applied to the subtotal, as a fraction (0.11 = 11%).</summary>
    public decimal TaxRate { get; set; } = 0.11m;

    /// <summary>Banker's rounding would surprise shoppers here, so money uses away-from-zero.</summary>
    public MidpointRounding Rounding { get; set; } = MidpointRounding.AwayFromZero;

    public int ProductsPerPage { get; set; } = 12;

    public int LowStockThreshold { get; set; } = 5;

    /// <summary>Storefront theme: 1 = Amazon-style, 2 = eBay-style, 3 = Etsy-style.</summary>
    public int Theme { get; set; } = 1;
}
