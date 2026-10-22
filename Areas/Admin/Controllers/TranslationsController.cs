using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebDS_Test.Domain;
using WebDS_Test.Services;
using WebDS_Test.Services.Localization;

namespace WebDS_Test.Areas.Admin.Controllers;

/// <summary>
/// The strings the storefront shows in each language. Administration edits them here, and the
/// storefront reads them from the same table, so a change takes effect on the next page load.
/// </summary>
/// <remarks>
/// <para>
/// Strings used to live in <c>App_Data/i18n/*.txt</c> only. Those files are gitignored and
/// machine-local, so a fresh clone had no catalogue at all and an edit was lost the next time the
/// folder was rebuilt. They are now the seed: the first start copies them into the
/// <c>Translations</c> table, and everything after that is a row staff can change.
/// </para>
/// <para>
/// The screen edits strings for the languages the store already offers; it deliberately does not
/// manage the language list itself. Adding a language is a configuration entry, and the list is
/// read from there on every request, so a language edited here would be a second source of truth.
/// </para>
/// </remarks>
public class TranslationsController : AdminControllerBase
{
    /// <summary>
    /// A key is a dotted identifier such as <c>controller.cart</c>. Restricting the shape keeps a
    /// typo from creating a string nothing will ever ask for.
    /// </summary>
    private static readonly Regex KeyPattern =
        new("^[A-Za-z][A-Za-z0-9]*(\\.[A-Za-z0-9]+)*$", RegexOptions.Compiled);

    private const int MaxKeyLength = 128;
    private const int MaxValueLength = 2000;

    private readonly ITranslationStore _store;
    private readonly IAuditTrail _audit;
    private readonly LanguageOptions _languages;

    public TranslationsController(
        ApplicationDbContext db,
        IOptions<StoreSettings> settings,
        IOptions<LanguageOptions> languages,
        ITranslationStore store,
        IAuditTrail audit)
        : base(db, settings)
    {
        _store = store;
        _audit = audit;
        _languages = languages.Value;
    }

    /// <summary>
    /// The catalogue for one language, with the English text beside it as the reference and a
    /// marker for the keys that language has not translated yet.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Index(string? culture, string? q, CancellationToken ct)
    {
        var supported = _languages.Supported;
        var selected = Normalise(culture, supported);

        // English is the fallback every other language degrades to, so its keys are the definition
        // of "the keys this store has". A key that exists only in, say, French is still listed —
        // it is just not something English falls back to.
        var english = await Db.Translations.AsNoTracking()
            .Where(t => t.Culture == LanguageOptions.DefaultCulture)
            .OrderBy(t => t.Key)
            .ToListAsync(ct);

        var rows = await Db.Translations.AsNoTracking()
            .Where(t => t.Culture == selected)
            .OrderBy(t => t.Key)
            .ToListAsync(ct);

        var byKey = rows.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);
        var englishByKey = english.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

        // Every key the store knows about: the English ones plus any that exist in the language
        // being edited but not in English.
        IEnumerable<string> keys = englishByKey.Keys
            .Concat(byKey.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            keys = keys.Where(k =>
                k.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (byKey.TryGetValue(k, out var r) && r.Value.Contains(needle, StringComparison.OrdinalIgnoreCase))
                || (englishByKey.TryGetValue(k, out var e) && e.Value.Contains(needle, StringComparison.OrdinalIgnoreCase)));
        }

        var model = new TranslationListViewModel
        {
            Culture = selected,
            Search = q,
            TotalKeys = englishByKey.Count + byKey.Keys.Count(k => !englishByKey.ContainsKey(k))
        };

        model.Languages.AddRange(supported.Select(l => new LanguageTab
        {
            Code = l.Code,
            Name = l.Name,
            NativeName = l.NativeName,
            IsRightToLeft = l.IsRightToLeft,
            IsSelected = string.Equals(l.Code, selected, StringComparison.OrdinalIgnoreCase)
        }));

        foreach (var key in keys)
        {
            byKey.TryGetValue(key, out var row);
            englishByKey.TryGetValue(key, out var reference);

            model.Rows.Add(new TranslationRow
            {
                Key = key,
                Value = row?.Value ?? string.Empty,
                EnglishValue = reference?.Value ?? string.Empty,
                IsTranslated = row is not null,
                UpdatedBy = row?.UpdatedBy,
                UpdatedUtc = row?.UpdatedUtc
            });
        }

        model.TranslatedCount = model.Rows.Count(r => r.IsTranslated);
        model.MissingCount = model.Rows.Count - model.TranslatedCount;

        ViewData["Title"] = "Translations";
        return View(model);
    }

    /// <summary>
    /// Saves one string. A key that does not exist yet in this language is created, so the same
    /// form both edits and adds.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(string culture, string key, string? value, CancellationToken ct)
    {
        culture = Normalise(culture, _languages.Supported);
        key = (key ?? string.Empty).Trim();
        value = (value ?? string.Empty).Trim();

        if (!KeyPattern.IsMatch(key) || key.Length > MaxKeyLength)
        {
            Flash("A key is a dotted identifier such as 'controller.cart'.", false);
            return RedirectToAction(nameof(Index), new { culture });
        }

        if (value.Length > MaxValueLength)
        {
            Flash($"A string cannot be longer than {MaxValueLength:N0} characters.", false);
            return RedirectToAction(nameof(Index), new { culture });
        }

        var existed = await Db.Translations
            .AnyAsync(t => t.Culture == culture && t.Key == key, ct);

        await _store.SetAsync(culture, key, value, CurrentUserName, ct);

        await _audit.RecordAsync(
            CurrentUserName,
            existed ? "translation.updated" : "translation.created",
            "Translation",
            null,
            $"{(existed ? "Changed" : "Added")} '{key}' for {culture}.",
            ct);

        Flash($"Saved '{key}' for {culture}.");
        return RedirectToAction(nameof(Index), new { culture });
    }

    /// <summary>
    /// Removes one string. The key stays in the catalogue if English still has it, and the
    /// storefront falls back to English for it — which is the point of deleting a translation.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string culture, string key, CancellationToken ct)
    {
        culture = Normalise(culture, _languages.Supported);
        key = (key ?? string.Empty).Trim();

        var removed = await _store.DeleteAsync(culture, key, ct);

        if (removed)
        {
            await _audit.RecordAsync(
                CurrentUserName,
                "translation.deleted",
                "Translation",
                null,
                $"Removed '{key}' for {culture}.",
                ct);

            Flash($"Removed '{key}' for {culture}.");
        }
        else
        {
            Flash($"There was no '{key}' for {culture}.", false);
        }

        return RedirectToAction(nameof(Index), new { culture });
    }

    /// <summary>
    /// Falls back to English for an unknown culture, so a hand-edited query string cannot ask for a
    /// language the store does not offer.
    /// </summary>
    private static string Normalise(string? culture, IReadOnlyList<LanguageDefinition> supported)
    {
        if (!string.IsNullOrWhiteSpace(culture))
        {
            var match = supported.FirstOrDefault(s =>
                string.Equals(s.Code, culture.Trim(), StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                return match.Code;
            }
        }

        return supported.FirstOrDefault(s =>
            string.Equals(s.Code, LanguageOptions.DefaultCulture, StringComparison.OrdinalIgnoreCase))?.Code
            ?? LanguageOptions.DefaultCulture;
    }

    /// <summary>One language's catalogue.</summary>
    public sealed class TranslationListViewModel
    {
        /// <summary>The language being edited.</summary>
        public string Culture { get; set; } = LanguageOptions.DefaultCulture;

        /// <summary>The free-text filter applied to keys and values, echoed back into the search box.</summary>
        public string? Search { get; set; }

        /// <summary>Every key the store knows about, before the search filter.</summary>
        public int TotalKeys { get; set; }

        /// <summary>How many of the listed keys this language has.</summary>
        public int TranslatedCount { get; set; }

        /// <summary>How many of the listed keys this language is still missing.</summary>
        public int MissingCount { get; set; }

        public List<LanguageTab> Languages { get; } = new();

        public List<TranslationRow> Rows { get; } = new();
    }

    /// <summary>A language in the tab strip.</summary>
    public sealed class LanguageTab
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string NativeName { get; set; } = string.Empty;
        public bool IsRightToLeft { get; set; }
        public bool IsSelected { get; set; }
    }

    /// <summary>One string, with the English text beside it as the reference.</summary>
    public sealed class TranslationRow
    {
        public string Key { get; set; } = string.Empty;

        /// <summary>The text stored for the language being edited. Empty when it has none.</summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>The English text, which is what a shopper sees when the translation is missing.</summary>
        public string EnglishValue { get; set; } = string.Empty;

        /// <summary>False when this language has no row for the key yet.</summary>
        public bool IsTranslated { get; set; }

        public string? UpdatedBy { get; set; }
        public DateTime? UpdatedUtc { get; set; }
    }
}
