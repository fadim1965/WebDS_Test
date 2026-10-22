using System.ComponentModel.DataAnnotations;

namespace WebDS_Test.Domain;

/// <summary>
/// A key/value row of store-wide settings that administration can change without a deployment —
/// the exchange rate and alternate-currency labels being the first of them.
/// </summary>
/// <remarks>
/// <para>
/// Configuration (<c>appsettings</c>) stays the bootstrap: a fresh clone runs on the defaults bound
/// into <see cref="StoreSettings"/> with no rows at all. A row exists only once somebody has
/// deliberately overridden that default, and its presence is what makes the value authoritative —
/// so an admin deleting the row reverts the store to configuration rather than leaving a
/// half-configured state behind.
/// </para>
/// <para>
/// Values are strings so the table never needs a migration to add a key; parsing belongs to the
/// reader that knows what the key means.
/// </para>
/// </remarks>
public class SiteSetting
{
    public int Id { get; set; }

    /// <summary>Stable identifier, e.g. <c>currency.alternateRate</c>. Unique.</summary>
    [Required, StringLength(64)]
    public string Key { get; set; } = string.Empty;

    [Required, StringLength(512)]
    public string Value { get; set; } = string.Empty;

    /// <summary>When the value was last written, for the settings screen's audit column.</summary>
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Who wrote it — the administrator's display name.</summary>
    [StringLength(128)]
    public string? UpdatedBy { get; set; }
}

/// <summary>The settings this build knows how to read.</summary>
public static class SiteSettingKeys
{
    /// <summary>ISO code of the alternate display currency, e.g. <c>LBP</c>.</summary>
    public const string AlternateCurrency = "currency.alternateCode";

    /// <summary>How the alternate currency is abbreviated on price tags, e.g. <c>LL</c>.</summary>
    public const string AlternateCurrencySymbol = "currency.alternateSymbol";

    /// <summary>
    /// How many alternate units one base (settlement) unit buys — <c>1 USD = this many LBP</c>.
    /// A value of zero or less switches the alternate figure off entirely.
    /// </summary>
    public const string AlternateRate = "currency.alternateRate";
}
