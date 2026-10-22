using Microsoft.EntityFrameworkCore;
using WebDS_Test.Domain;

namespace WebDS_Test.Data;

/// <summary>
/// Imports the shipped <c>App_Data/i18n/*.txt</c> catalogues into the <c>Translations</c> table on a
/// store that has none, so the text files become the seed rather than the authority.
/// </summary>
/// <remarks>
/// <para>
/// Before translations were editable the translator read these files every time it started, which
/// meant a string lived in a file that was gitignored, machine-local, and replaced on every rebuild.
/// Copying them into the store once fixes that: from then on what staff edit is a row, and the file
/// is only the starting point.
/// </para>
/// <para>
/// The import is per-culture and only when that culture holds no row at all. A culture the admin has
/// started editing is therefore never clobbered by a later start — deleting every row of a culture
/// is the one way to ask for the shipped strings back, which keeps the rule easy to state.
/// </para>
/// </remarks>
public static class TranslationSeeder
{
    public static async Task SeedAsync(ApplicationDbContext db, CancellationToken ct = default)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "App_Data", "i18n");
        if (!Directory.Exists(folder))
        {
            return;
        }

        // Which cultures already hold strings decides what to import. Reading the set once keeps the
        // loop from issuing a "does this culture have rows" query per file.
        var populated = (await db.Translations
                .Select(t => t.Culture)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(folder, "*.txt"))
        {
            var culture = Path.GetFileNameWithoutExtension(file);
            if (populated.Contains(culture))
            {
                continue;
            }

            var entries = Parse(file);
            if (entries.Count == 0)
            {
                continue;
            }

            var now = DateTime.UtcNow;
            db.Translations.AddRange(entries.Select(e => new Translation
            {
                Culture = culture,
                Key = e.Key,
                Value = e.Value,
                UpdatedUtc = now,
                UpdatedBy = "seed"
            }));
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Reads one catalogue: a <c>key=value</c> per line, blank lines and <c>#</c> comments skipped,
    /// and a later duplicate key winning. This is the same parse the file-based translator used, kept
    /// identical so the seed is exactly what the store served before it had a table.
    /// </summary>
    private static List<(string Key, string Value)> Parse(string file)
    {
        var entries = new List<(string Key, string Value)>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

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

                var key = line[..split].Trim();
                var value = line[(split + 1)..].Trim();

                if (seen.TryGetValue(key, out var index))
                {
                    entries[index] = (key, value);
                }
                else
                {
                    seen[key] = entries.Count;
                    entries.Add((key, value));
                }
            }
        }
        catch (IOException)
        {
            // A catalogue that cannot be read is an empty catalogue, not a failed startup.
            return new List<(string, string)>();
        }

        return entries;
    }
}
