using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebDS_Test.Domain;
using WebDS_Test.Services;

namespace WebDS_Test.Areas.Admin.Controllers;

/// <summary>
/// Store settings that administration can change without a deployment. The first of them is the
/// alternate display currency: the code, the abbreviation and the rate at which the indicative
/// Lebanese-pound figure is quoted beside every USD price.
/// </summary>
/// <remarks>
/// The base currency is deliberately not editable here. USD is the money of record — charges,
/// refunds, the settlement ledger and payouts all speak it — so changing it would be a data
/// migration, not a setting. What this screen controls is only the display conversion.
/// </remarks>
public class SettingsController : AdminControllerBase
{
    /// <summary>Sanity bound on the rate: beyond this, the admin almost certainly dropped a zero.</summary>
    private const decimal MaxAlternateRate = 10_000_000m;

    private static readonly Regex CodePattern = new("^[A-Z]{3}$", RegexOptions.Compiled);

    private readonly ISiteSettingStore _store;
    private readonly IAuditTrail _audit;

    public SettingsController(
        ApplicationDbContext db,
        Microsoft.Extensions.Options.IOptions<StoreSettings> settings,
        ISiteSettingStore store,
        IAuditTrail audit)
        : base(db, settings)
    {
        _store = store;
        _audit = audit;
    }

    /// <summary>The settings form, prefilled with the effective values (database override or configuration default).</summary>
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var model = new CurrencySettingsViewModel
        {
            AlternateCurrency = await _store.GetAsync(SiteSettingKeys.AlternateCurrency, ct) ?? Settings.AlternateCurrency,
            AlternateCurrencySymbol = await _store.GetAsync(SiteSettingKeys.AlternateCurrencySymbol, ct) ?? Settings.AlternateCurrencySymbol,
            AlternateRate = await _store.GetAsync(SiteSettingKeys.AlternateRate, ct)
                ?? Settings.AlternateRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            IsOverridden = _store.Get(SiteSettingKeys.AlternateRate) is not null,
        };

        var stamp = await Db.SiteSettings.AsNoTracking()
            .Where(s => s.Key == SiteSettingKeys.AlternateRate)
            .Select(s => new { s.UpdatedBy, s.UpdatedUtc })
            .FirstOrDefaultAsync(ct);

        model.LastUpdatedBy = stamp?.UpdatedBy;
        model.LastUpdatedUtc = stamp?.UpdatedUtc;

        model.RefreshPreview(Settings);
        ViewData["Title"] = "Store settings";
        return View(model);
    }

    /// <summary>Saves the alternate-currency triple. A rate of zero switches the figure off.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(CurrencySettingsViewModel model, CancellationToken ct)
    {
        model.AlternateCurrency = (model.AlternateCurrency ?? string.Empty).Trim().ToUpperInvariant();
        model.AlternateCurrencySymbol = (model.AlternateCurrencySymbol ?? string.Empty).Trim();

        if (!CodePattern.IsMatch(model.AlternateCurrency))
        {
            ModelState.AddModelError(nameof(model.AlternateCurrency), "Use a three-letter ISO code, e.g. LBP.");
        }

        if (model.AlternateCurrencySymbol.Length is < 1 or > 8)
        {
            ModelState.AddModelError(nameof(model.AlternateCurrencySymbol), "The abbreviation shown on price tags (1–8 characters), e.g. LL.");
        }

        if (!decimal.TryParse(model.AlternateRate, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var rate))
        {
            ModelState.AddModelError(nameof(model.AlternateRate), "Enter a number, e.g. 89500. Use 0 to turn the figure off.");
        }
        else if (rate < 0)
        {
            ModelState.AddModelError(nameof(model.AlternateRate), "The rate cannot be negative. Use 0 to turn the figure off.");
        }
        else if (rate > MaxAlternateRate)
        {
            ModelState.AddModelError(nameof(model.AlternateRate), $"That rate looks wrong — the limit is {MaxAlternateRate:N0}. Check the number of zeros.");
        }

        if (!ModelState.IsValid)
        {
            model.RefreshPreview(Settings);
            ViewData["Title"] = "Store settings";
            return View(model);
        }

        var rateText = rate.ToString(System.Globalization.CultureInfo.InvariantCulture);

        await _store.SetAsync(SiteSettingKeys.AlternateCurrency, model.AlternateCurrency, CurrentUserName, ct);
        await _store.SetAsync(SiteSettingKeys.AlternateCurrencySymbol, model.AlternateCurrencySymbol, CurrentUserName, ct);
        await _store.SetAsync(SiteSettingKeys.AlternateRate, rateText, CurrentUserName, ct);

        var summary = rate > 0
            ? $"The indicative alternate figure is now quoted at 1 USD = {rateText} {model.AlternateCurrency} ({model.AlternateCurrencySymbol}). USD remains the settlement currency."
            : "The alternate-currency figure was switched off; prices now show USD only.";

        await _audit.RecordAsync(CurrentUserName, "settings.currency.updated", "Settings", null, summary, ct);

        Flash(rate > 0
            ? $"Saved: prices now show ≈ {rateText:N0} {model.AlternateCurrencySymbol} per USD."
            : "Saved: the alternate-currency figure is off.");

        return RedirectToAction(nameof(Index));
    }

    /// <summary>The currency form. Submitted as a whole so a half-typed rate cannot be saved alone.</summary>
    public sealed class CurrencySettingsViewModel
    {
        [Required]
        [Display(Name = "Currency code")]
        public string AlternateCurrency { get; set; } = "LBP";

        [Required]
        [Display(Name = "Abbreviation on price tags")]
        public string AlternateCurrencySymbol { get; set; } = "LL";

        [Required]
        [Display(Name = "Rate (alternate units per 1 USD)")]
        public string AlternateRate { get; set; } = "89500";

        /// <summary>True once an administrator has saved a value that now overrides configuration.</summary>
        public bool IsOverridden { get; set; }

        public string? LastUpdatedBy { get; set; }
        public DateTime? LastUpdatedUtc { get; set; }

        /// <summary>What prices would look like with the values currently in the form.</summary>
        public List<PreviewRow> Preview { get; } = new();

        public void RefreshPreview(StoreSettings defaults)
        {
            Preview.Clear();

            var enabled = decimal.TryParse(AlternateRate, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var rate)
                && rate > 0
                && !string.IsNullOrWhiteSpace(AlternateCurrency);

            foreach (var sample in new[] { 4.50m, 24.50m, 60.00m, 100.00m })
            {
                var baseText = defaults.CurrencySymbol + sample.ToString("N2", System.Globalization.CultureInfo.CurrentCulture);
                var alternateText = enabled
                    ? $"{Math.Round(sample * rate, 0, defaults.Rounding).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} {AlternateCurrencySymbol}"
                    : string.Empty;

                Preview.Add(new PreviewRow(baseText, alternateText));
            }
        }

        public sealed record PreviewRow(string BaseText, string AlternateText);
    }
}
