using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace WebDS_Test.Services;

/// <summary>
/// The admin audit trail: who did what, to which record, when.
/// </summary>
/// <remarks>
/// <para>
/// Writes are best-effort by design: a failing audit row is logged and swallowed rather than allowed
/// to fail a request whose real work has already been committed — an audit trail documents actions,
/// and holding a refund hostage to a logging failure inverts that. Like the ledger, a write commits
/// the caller's pending changes with it, because EF Core persists the whole change tracker at once.
/// </para>
/// <para>
/// Callers record the action themselves rather than an interceptor inferring it: the trail should
/// read like a history book — short, deliberate sentences — not like a diff of the change tracker.
/// </para>
/// </remarks>
public interface IAuditTrail
{
    /// <summary>Records one action. Never throws; failures are logged, not propagated.</summary>
    Task RecordAsync(
        string actor, string action, string entityType, int? entityId, string? summary,
        CancellationToken ct = default);

    /// <summary>The trail, newest first, optionally filtered. Bounded — it is a browsing view.</summary>
    Task<IReadOnlyList<AuditLog>> GetRecentAsync(
        string? action = null, string? entityType = null, string? actor = null, int? entityId = null,
        int take = 200, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class AuditTrail : IAuditTrail
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<AuditTrail> _logger;

    public AuditTrail(ApplicationDbContext db, ILogger<AuditTrail> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RecordAsync(
        string actor, string action, string entityType, int? entityId, string? summary,
        CancellationToken ct = default)
    {
        try
        {
            _db.AuditLogs.Add(new AuditLog
            {
                Actor = Truncate(actor.Trim(), 256),
                Action = Truncate(action.Trim(), 80),
                EntityType = Truncate(entityType.Trim(), 40),
                EntityId = entityId,
                Summary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim(),
                CreatedUtc = DateTime.UtcNow
            });

            await _db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed audit write must never undo or fail the action it was recording.
            _logger.LogError(ex,
                "Audit trail: failed to record {Action} on {EntityType} {EntityId} by {Actor}",
                action, entityType, entityId, actor);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditLog>> GetRecentAsync(
        string? action = null, string? entityType = null, string? actor = null, int? entityId = null,
        int take = 200, CancellationToken ct = default)
    {
        var query = _db.AuditLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(l => l.Action == action.Trim());
        }

        if (!string.IsNullOrWhiteSpace(entityType))
        {
            query = query.Where(l => l.EntityType == entityType.Trim());
        }

        if (!string.IsNullOrWhiteSpace(actor))
        {
            query = query.Where(l => l.Actor.Contains(actor.Trim()));
        }

        if (entityId is { } id)
        {
            query = query.Where(l => l.EntityId == id);
        }

        return await query
            .OrderByDescending(l => l.CreatedUtc)
            .ThenByDescending(l => l.Id)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
