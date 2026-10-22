using Microsoft.EntityFrameworkCore;
using WebDS_Test.Data;
using WebDS_Test.Domain;

namespace WebDS_Test.Services;

/// <summary>
/// Application-wide access to <see cref="SiteSetting"/> rows: the admin settings screen writes
/// through it, and everything that reads a setting (today, the currency formatter) reads from it.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton because the cache is the point: a settings table lookup per Razor
/// render would be a database round-trip per price tag. The store owns its own DI scope
/// (<see cref="IServiceScopeFactory"/>) so it never captures a request's <c>DbContext</c>.
/// </para>
/// <para>
/// The cache is loaded once at startup (the program warms it after seeding) and updated in place
/// on every save, so an admin changing the exchange rate sees it take effect on the next page
/// load with no restart and no TTL to reason about. When the cache is cold or a key has no row,
/// <see cref="Get"/> returns null and callers fall back to their configuration defaults — a store
/// with no rows simply behaves like the shipped configuration.
/// </para>
/// </remarks>
public interface ISiteSettingStore
{
    /// <summary>Loads every row into the cache. Idempotent; called once at startup.</summary>
    Task EnsureLoadedAsync(CancellationToken ct = default);

    /// <summary>Sync read for hot paths (Razor rendering). Null when cold or the key is unset.</summary>
    string? Get(string key);

    /// <summary>Async read; ensures the cache is loaded first.</summary>
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Upserts the row and refreshes the cache. <paramref name="actor"/> is recorded for audit.</summary>
    Task SetAsync(string key, string value, string? actor = null, CancellationToken ct = default);
}

public sealed class SiteSettingStore : ISiteSettingStore
{
    private readonly IServiceScopeFactory _scopes;
    private readonly object _gate = new();
    private Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public SiteSettingStore(IServiceScopeFactory scopes)
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

        Dictionary<string, string> snapshot;

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.SiteSettings.AsNoTracking().ToListAsync(ct);

        snapshot = rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            _cache = snapshot;
            _loaded = true;
        }
    }

    public string? Get(string key)
    {
        lock (_gate)
        {
            return _loaded && _cache.TryGetValue(key, out var value) ? value : null;
        }
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return Get(key);
    }

    public async Task SetAsync(string key, string value, string? actor = null, CancellationToken ct = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // A write into a cache that was never read would hide every row but this one, so load
        // first. It is idempotent and cheap, and it keeps "cold store" meaning exactly one thing.
        await EnsureLoadedAsync(ct);

        var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            db.SiteSettings.Add(new SiteSetting { Key = key, Value = value, UpdatedUtc = DateTime.UtcNow, UpdatedBy = actor });
        }
        else
        {
            row.Value = value;
            row.UpdatedUtc = DateTime.UtcNow;
            row.UpdatedBy = actor;
        }

        await db.SaveChangesAsync(ct);

        // Refresh the shared cache only after the write is durable, so a failed save cannot make
        // the storefront render a value the database never accepted.
        lock (_gate)
        {
            var next = new Dictionary<string, string>(_cache, StringComparer.OrdinalIgnoreCase)
            {
                [key] = value
            };
            _cache = next;
        }
    }
}
