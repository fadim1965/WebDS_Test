using System.ComponentModel.DataAnnotations;

namespace WebDS_Test.Domain;

/// <summary>
/// One translated string: a key in one language.
/// </summary>
/// <remarks>
/// <para>
/// Strings used to live only in <c>App_Data/i18n/*.txt</c>, which the translator read at startup.
/// That made them machine-local and invisible to version control — a fresh clone had no catalogue
/// at all, and an edit was lost the next time the folder was rebuilt. Moving the authoritative copy
/// into the store fixes both: the text files are now only the seed, and what staff edit is a row.
/// </para>
/// <para>
/// The key is culture-independent and English is the fallback for every other language, so a key
/// with no row in a language is simply untranslated rather than broken — the same degradation the
/// file-based translator always had.
/// </para>
/// </remarks>
public class Translation
{
    public int Id { get; set; }

    /// <summary>Language code, e.g. <c>en</c>, <c>ar</c>, <c>fr</c>. One of the configured languages.</summary>
    [Required, StringLength(8)]
    public string Culture { get; set; } = string.Empty;

    /// <summary>Stable identifier such as <c>controller.cart</c>. Unique per culture.</summary>
    [Required, StringLength(128)]
    public string Key { get; set; } = string.Empty;

    /// <summary>The translated text. May contain <c>{0}</c> placeholders.</summary>
    [Required, StringLength(2000)]
    public string Value { get; set; } = string.Empty;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Who last changed it — the administrator's display name.</summary>
    [StringLength(128)]
    public string? UpdatedBy { get; set; }
}
