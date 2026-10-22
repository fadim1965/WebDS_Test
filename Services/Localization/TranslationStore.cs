using Microsoft.EntityFrameworkCore;
using WebDS_Test.Data;
using WebDS_Test.Domain;

namespace WebDS_Test.Services.Localization;

/// <summary>
/// Application-wide access to <see cref="Translation"/> rows: the admin translation screen writes
/// through it, and <see cref="Translator"/> reads every string from it.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton because the cache is the point: a translation lookup happens many times
/// per page render, and a database round-trip each time would be absurd. The store owns its own DI
/// scope (<see cref="IServiceScopeFactory"/>) so it never captures a request's <c>DbContext</c>.
/// </para>
/// <para>
/// The cache is loaded once at startup (the program warms it after seeding) and updated in place on
/// every save, so an administrator editing a string sees it take effect on the next page load with
/// no restart. When the cache holds no row for a key, the caller falls back — the translator to
/// English and then to the key, exactly as the file-based catalogue always did.
/// </para>
/// </remarks>
public interface ITranslationStore
{
    /// <summary>Loads every row into the cache. Idempotent; called once at startup.</summary>
    Task EnsureLoadedAsync(CancellationToken ct = default);

    /// <summary>Sync read for hot paths (Razor rendering). Null when the string is unset.</summary>
    string? Get(string culture, string key);

    /// <summary>Every key known in any culture, ordered. Used by the admin screen and health checks.</summary>
    IReadOnlyList<string> Keys();

    /// <summary>The keys present in <paramref name="culture"/>, for the "translated" count.</summary>
    IReadOnlyList<string> KeysFor(string culture);

    /// <summary>Upserts one string and refreshes the cache. <paramref name="actor"/> is recorded for audit.</summary>
    Task SetAsync(string culture, string key, string value, string? actor = null, CancellationToken ct = default);

    /// <summary>Removes one string and refreshes the cache. Returns true when a row was deleted.</summary>
    Task<bool> DeleteAsync(string culture, string key, CancellationToken ct = default);
}

public sealed class TranslationStore : ITranslationStore
{
    private readonly IServiceScopeFactory _scopes;
    private readonly object _gate = new();

    /// <summary>Culture -> (key -> value). Case-insensitive on both axes, matching the translator's history.</summary>
    private Dictionary<string, Dictionary<string, string>> _cache = new(StringComparer.OrdinalIgnoreCase);

    private bool _loaded;

    public TranslationStore(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
    }

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }
        }

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.Translations.AsNoTracking().ToListAsync(ct);

        var snapshot = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!snapshot.TryGetValue(row.Culture, out var table))
            {
                table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                snapshot[row.Culture] = table;
            }

            table[row.Key] = row.Value;
        }

        lock (_gate)
        {
            _cache = snapshot;
            _loaded = true;
        }
    }

    public string? Get(string culture, string key)
    {
        lock (_gate)
        {
            return _loaded
                && _cache.TryGetValue(culture, out var table)
                && table.TryGetValue(key, out var value)
                    ? value
                    : null;
        }
    }

    public IReadOnlyList<string> Keys()
    {
        lock (_gate)
        {
            if (!_loaded)
            {
                return Array.Empty<string>();
            }

            return _cache.Values
                .SelectMany(t => t.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public IReadOnlyList<string> KeysFor(string culture)
    {
        lock (_gate)
        {
            return _loaded && _cache.TryGetValue(culture, out var table)
                ? table.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList()
                : Array.Empty<string>();
        }
    }

    public async Task SetAsync(string culture, string key, string value, string? actor = null, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var row = await db.Translations
            .FirstOrDefaultAsync(t => t.Culture == culture && t.Key == key, ct);

        if (row is null)
        {
            db.Translations.Add(new Translation
            {
                Culture = culture,
                Key = key,
                Value = value,
                UpdatedUtc = DateTime.UtcNow,
                UpdatedBy = actor
            });
        }
        else
        {
            row.Value = value;
            row.UpdatedUtc = DateTime.UtcNow;
            row.UpdatedBy = actor;
        }

        await db.SaveChangesAsync(ct);

        // Refresh the shared cache only after the write is durable, so a failed save cannot make the
        // storefront render a string the database never accepted.
        lock (_gate)
        {
            var next = Clone(_cache);
            if (!next.TryGetValue(culture, out var table))
            {
                table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                next[culture] = table;
            }

            table[key] = value;
            _cache = next;
        }
    }

    public async Task<bool> DeleteAsync(string culture, string key, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var row = await db.Translations
            .FirstOrDefaultAsync(t => t.Culture == culture && t.Key == key, ct);

        if (row is null)
        {
            return false;
        }

        db.Translations.Remove(row);
        await db.SaveChangesAsync(ct);

        lock (_gate)
        {
            var next = Clone(_cache);
            if (next.TryGetValue(culture, out var table))
            {
                table.Remove(key);
                if (table.Count == 0)
                {
                    next.Remove(culture);
                }
            }

            _cache = next;
        }

        return true;
    }

    /// <summary>
    /// Copies the cache so a reader on the Razor path never observes a half-updated table: the whole
    /// dictionary is swapped in one assignment rather than mutated under the readers' feet.
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>> Clone(
        Dictionary<string, Dictionary<string, string>> source)
    {
        var copy = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (culture, table) in source)
        {
            copy[culture] = new Dictionary<string, string>(table, StringComparer.OrdinalIgnoreCase);
        }

        return copy;
    }
}
