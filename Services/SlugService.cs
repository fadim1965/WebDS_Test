using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WebDS_Test.Services;

/// <summary>URL-segment generation for categories, products and routes.</summary>
public interface ISlugService
{
    /// <summary>Turns arbitrary text into a lowercase, hyphen-separated URL segment.</summary>
    string Slugify(string? value);
}

public sealed partial class SlugService : ISlugService
{
    private const int MaxLength = 70;

    public string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // Decompose accented characters so "Café" becomes "cafe" rather than "caf".
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
        }

        var slug = RepeatedHyphens().Replace(builder.ToString(), "-").Trim('-');

        // Bound the segment length; the domain columns allow 80-160 characters.
        return slug.Length > MaxLength ? slug[..MaxLength].Trim('-') : slug;
    }

    [GeneratedRegex("-{2,}")]
    private static partial Regex RepeatedHyphens();
}
