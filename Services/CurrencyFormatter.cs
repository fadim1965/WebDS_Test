using System.Globalization;
using Microsoft.Extensions.Options;
using WebDS_Test.Domain;

namespace WebDS_Test.Services;

/// <summary>
/// Renders money for shoppers: always the base (settlement) amount, and — when an alternate
/// display currency is configured — the indicative alternate figure beside it.
/// </summary>
/// <remarks>
/// <para>
/// The base amount is the money of record: it is what is charged, refunded, settled to vendors
/// and recorded in the ledger. The alternate figure (today: Lebanese pounds) is a courtesy
/// conversion at the store-wide rate an administrator maintains, rounded to whole units, and it
/// never participates in arithmetic — the storefront only ever sums in the base currency.
/// </para>
/// <para>
/// The effective settings come from <see cref="SiteSettingStore"/> (what the admin screen last
/// saved) and fall back to <see cref="StoreSettings"/> (the shipped configuration) when a key has
/// no row or the store is cold. Values are read per call, not cached here, so a rate saved in the
/// admin screen is live on the very next render.
/// </para>
/// <para>
/// A rate of zero or less — or an empty alternate code — switches the alternate figure off, and
/// every surface degrades to plain base-currency display with no code change.
/// </para>
/// </remarks>
public interface ICurrencyFormatter
{
    /// <summary>Symbol for the base currency, e.g. <c>$</c>.</summary>
    string BaseSymbol { get; }

    /// <summary>True when prices should carry the alternate figure as well.</summary>
    bool IsDual { get; }

    /// <summary>ISO code of the alternate display currency, e.g. <c>LBP</c>. Empty when off.</summary>
    string AlternateCode { get; }

    /// <summary>Abbreviation shown after the alternate figure, e.g. <c>LL</c>.</summary>
    string AlternateSymbol { get; }

    /// <summary>How many alternate units one base unit buys. Zero or less when off.</summary>
    decimal AlternateRate { get; }

    /// <summary>Converts a base amount to alternate units, rounded to whole units.</summary>
    decimal ToAlternate(decimal baseAmount);

    /// <summary>Base only — <c>$24.50</c>. For surfaces that must show the money of record alone.</summary>
    string FormatBase(decimal baseAmount);

    /// <summary>Alternate figure only — <c>2,192,750 LL</c>. Empty when the alternate is off.</summary>
    string FormatAlternate(decimal baseAmount);

    /// <summary>
    /// The standard price tag: <c>$24.50</c> when the alternate is off,
    /// <c>$24.50 · ≈2,192,750 LL</c> when it is on.
    /// </summary>
    string Format(decimal baseAmount);
}

public sealed class CurrencyFormatter : ICurrencyFormatter
{
    private readonly ISiteSettingStore _store;
    private readonly StoreSettings _defaults;

    public CurrencyFormatter(ISiteSettingStore store, IOptions<StoreSettings> defaults)
    {
        _store = store;
        _defaults = defaults.Value;
    }

    public string BaseSymbol => _defaults.CurrencySymbol;

    public string AlternateCode =>
        _store.Get(SiteSettingKeys.AlternateCurrency) ?? _defaults.AlternateCurrency;

    public string AlternateSymbol =>
        _store.Get(SiteSettingKeys.AlternateCurrencySymbol) ?? _defaults.AlternateCurrencySymbol;

    public decimal AlternateRate
    {
        get
        {
            var raw = _store.Get(SiteSettingKeys.AlternateRate);
            if (raw is null)
            {
                return _defaults.AlternateRate;
            }

            return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate)
                ? rate
                : 0m;
        }
    }

    public bool IsDual => AlternateRate > 0 && !string.IsNullOrWhiteSpace(AlternateCode);

    public decimal ToAlternate(decimal baseAmount) =>
        Math.Round(baseAmount * AlternateRate, 0, _defaults.Rounding);

    public string FormatBase(decimal baseAmount) =>
        BaseSymbol + baseAmount.ToString("N2", CultureInfo.CurrentCulture);

    public string FormatAlternate(decimal baseAmount)
    {
        if (!IsDual)
        {
            return string.Empty;
        }

        // Invariant culture so the alternate figure always groups thousands with commas the way
        // Lebanese pounds are quoted, regardless of the server's regional settings.
        return $"{ToAlternate(baseAmount).ToString("N0", CultureInfo.InvariantCulture)} {AlternateSymbol}";
    }

    public string Format(decimal baseAmount)
    {
        var formatted = FormatBase(baseAmount);
        var alternate = FormatAlternate(baseAmount);
        return alternate.Length == 0 ? formatted : $"{formatted} · ≈{alternate}";
    }
}
