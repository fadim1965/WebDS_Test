using System.Globalization;
using Microsoft.Extensions.Options;

namespace WebDS_Test.Services.Localization;

/// <summary>
/// One supported language: its code, its display name, whether it reads right to left, and its
/// strings. Strings live in a plain file per language rather than in code, so adding a language is a
/// file and one configuration entry — not a rebuild of the UI layer.
/// </summary>
public sealed class LanguageOptions
{
    public const string SectionName = "Languages";

    /// <summary>The language used when the shopper has expressed no preference. Always English.</summary>
    public const string DefaultCulture = "en";

    public string Default { get; set; } = DefaultCulture;

    /// <summary>Only these may be selected, whatever the request asks for.</summary>
    public List<LanguageDefinition> Supported { get; set; } = new()
    {
        new LanguageDefinition { Code = "en", Name = "English", NativeName = "English", IsRightToLeft = false },
        new LanguageDefinition { Code = "ar", Name = "Arabic",  NativeName = "العربية", IsRightToLeft = true },
        new LanguageDefinition { Code = "fr", Name = "French",  NativeName = "Français", IsRightToLeft = false }
    };

    public bool Enabled { get; set; } = true;
}

public sealed class LanguageDefinition
{
    public string Code { get; set; } = "en";
    public string Name { get; set; } = string.Empty;
    public string NativeName { get; set; } = string.Empty;
    public bool IsRightToLeft { get; set; }
}

/// <summary>Translates a key into the current language, falling back to English and then to the key.</summary>
public interface ITranslator
{
    /// <summary>The language actually in use, e.g. "ar".</summary>
    string Culture { get; }

    bool IsRightToLeft { get; }

    /// <summary>Reads a key such as <c>nav.shop</c>. Never throws and never returns null.</summary>
    string this[string key] { get; }

    /// <summary>Reads a key and substitutes <c>{0}</c>, <c>{1}</c>… placeholders.</summary>
    string Format(string key, params object?[] arguments);

    /// <summary>The languages the store offers, for the switcher.</summary>
    IReadOnlyList<LanguageDefinition> Supported { get; }

    /// <summary>True when the key exists in the current language (or in English).</summary>
    bool Has(string key);
}

public sealed class Translator : ITranslator
{
    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private readonly Lazy<Dictionary<string, IReadOnlyDictionary<string, string>>> _byCulture;
    private readonly LanguageOptions _options;
    private readonly string _culture;

    /// <summary>
    /// The database-backed catalogue, when one is configured. It is the authority: the file catalogue
    /// below is loaded as the seed (and as the whole catalogue when no store is injected), so a key
    /// the store does not hold still resolves from the shipped text, and a store-side edit wins.
    /// </summary>
    private readonly ITranslationStore? _store;

    public Translator(IOptions<LanguageOptions> options, IHttpContextAccessor http, ITranslationStore? store = null)
    {
        _options = options.Value;
        // The files are read only if a lookup misses the database, which is what makes the seeded
        // catalogue a fallback rather than three disk reads on every request.
        _byCulture = new Lazy<Dictionary<string, IReadOnlyDictionary<string, string>>>(LoadCatalog);
        _culture = ResolveCulture(http.HttpContext, _options);
        _store = store;
    }

    public string Culture => _culture;

    public bool IsRightToLeft =>
        _options.Supported.FirstOrDefault(s => string.Equals(s.Code, _culture, StringComparison.OrdinalIgnoreCase))
            ?.IsRightToLeft ?? false;

    public IReadOnlyList<LanguageDefinition> Supported => _options.Supported;

    public string this[string key] => Lookup(_culture, key) ?? Lookup("en", key) ?? key;

    public string Format(string key, params object?[] arguments)
    {
        var template = this[key];

        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, arguments);
        }
        catch (FormatException)
        {
            // A template with a bad placeholder must not take a page down.
            return template;
        }
    }

    public bool Has(string key) => Lookup(_culture, key) is not null || Lookup("en", key) is not null;

    private string? Lookup(string culture, string key)
    {
        // The database is the authority once it holds the string. A key with no row falls back to
        // the shipped file catalogue, so a store seeded before a new key existed still translates.
        var stored = _store?.Get(culture, key);
        if (stored is not null)
        {
            return stored;
        }

        var catalog = _byCulture.Value;

        return catalog.TryGetValue(culture, out var table) && table.TryGetValue(key, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// Reads <c>App_Data/i18n/{code}.txt</c>, one <c>key=value</c> per line. A file that is missing or
    /// unreadable yields no strings for that language, and the English fallback takes over — a missing
    /// translation degrades to English rather than failing.
    /// </summary>
    private static Dictionary<string, IReadOnlyDictionary<string, string>> LoadCatalog()
    {
        var catalog = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var folder = Path.Combine(AppContext.BaseDirectory, "App_Data", "i18n");

        if (!Directory.Exists(folder))
        {
            return catalog;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.txt"))
        {
            var code = Path.GetFileNameWithoutExtension(file);
            var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var line in File.ReadAllLines(file))
                {
                    if (line.Length == 0 || line.TrimStart().StartsWith('#'))
                    {
                        continue;
                    }

                    var split = line.IndexOf('=');
                    if (split <= 0)
                    {
                        continue;
                    }

                    table[line[..split].Trim()] = line[(split + 1)..].Trim();
                }
            }
            catch (IOException)
            {
                // A catalogue that cannot be read is an empty catalogue, not a broken request.
                continue;
            }

            catalog[code] = table;
        }

        return catalog;
    }

    /// <summary>
    /// Precedence: an explicit choice the shopper made, then their browser's preference, then the
    /// store default. Only languages the store actually offers are honoured.
    /// </summary>
    private static string ResolveCulture(HttpContext? http, LanguageOptions options)
    {
        var allowed = options.Supported
            .Select(s => s.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var fallback = allowed.Contains(options.Default) ? options.Default : LanguageOptions.DefaultCulture;

        if (http is null)
        {
            return fallback;
        }

        var chosen = http.Request.Cookies["WebDS.Culture"];
        if (!string.IsNullOrWhiteSpace(chosen) && allowed.Contains(chosen))
        {
            return chosen;
        }

        var preferred = http.Request.Headers.AcceptLanguage.ToString()
            .Split(',')
            .Select(part => part.Split(';')[0].Trim())
            .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code) && allowed.Contains(code));

        return preferred ?? fallback;
    }
}
