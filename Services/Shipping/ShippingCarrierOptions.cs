namespace WebDS_Test.Services.Shipping;

/// <summary>
/// Carrier configuration, bound from the <c>Shipping</c> section of appsettings.
/// </summary>
/// <remarks>
/// <c>Provider</c> selects the active carrier. <c>simulator</c> is the default so that fulfilment —
/// label creation, tracking, cash-on-delivery — is fully exercisable without LibanPost credentials
/// and without network access. Setting <c>libanpost</c> switches to the real client, which is
/// written against the documented contract and needs <see cref="BaseUrl"/>, <see cref="ApiKey"/>
/// and <see cref="AccountNumber"/> to be supplied.
/// </remarks>
public sealed class ShippingCarrierOptions
{
    public const string SectionName = "Shipping";

    /// <summary><c>simulator</c> or <c>libanpost</c>.</summary>
    public string Provider { get; set; } = "simulator";

    /// <summary>Base URL of the LibanPost carrier API.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>API key or bearer token issued by the carrier.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Marketplace account number with the carrier.</summary>
    public string AccountNumber { get; set; } = string.Empty;

    /// <summary>Optional additional header the carrier requires, e.g. a tenant or subscription key.</summary>
    public string? ApiKeyHeader { get; set; }

    /// <summary>Default declared weight when the catalogue carries no weight for an item.</summary>
    public int DefaultWeightGrams { get; set; } = 500;

    /// <summary>Fallback per-consignment cost used for quoting when the carrier cannot be reached.</summary>
    public decimal DefaultShippingCost { get; set; } = 4.50m;

    /// <summary>Assumed transit time in days for quoting.</summary>
    public int DefaultTransitDays { get; set; } = 2;

    /// <summary>
    /// Collect cash on delivery. Turning this off books prepaid consignments only, which is the
    /// correct setting for a shop that has already been paid online.
    /// </summary>
    public bool EnableCashOnDelivery { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 25;

    /// <summary>Highest amount the marketplace will let a courier collect, as a safety rail.</summary>
    public decimal MaxCodAmount { get; set; } = 5_000m;

    /// <summary>True when the real carrier has everything it needs.</summary>
    public bool LibanPostIsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);

    public void Validate(ILogger logger)
    {
        if (Provider.Equals("libanpost", StringComparison.OrdinalIgnoreCase) && !LibanPostIsConfigured)
        {
            // Refuse to run silently against a carrier that cannot be reached: fulfilment would
            // look like it worked while no consignment was ever booked.
            throw new InvalidOperationException(
                "Shipping:Provider is 'libanpost' but Shipping:BaseUrl and/or Shipping:ApiKey are missing. " +
                "Set both, or use Shipping:Provider 'simulator' to exercise fulfilment locally.");
        }

        if (!Provider.Equals("simulator", StringComparison.OrdinalIgnoreCase) &&
            !Provider.Equals("libanpost", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Unknown Shipping:Provider '{Provider}'; falling back to the simulator", Provider);
        }
    }
}
