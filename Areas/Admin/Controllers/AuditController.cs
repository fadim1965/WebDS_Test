using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebDS_Test.Services;
using WebDS_Test.Services.Export;

namespace WebDS_Test.Areas.Admin.Controllers;

/// <summary>
/// The admin audit trail: who did what, to which record, when. Read-only — nothing here writes,
/// which is the point of a trail.
/// </summary>
public class AuditController : AdminControllerBase
{
    private readonly IAuditTrail _audit;

    public AuditController(ApplicationDbContext db, IOptions<StoreSettings> settings, IAuditTrail audit)
        : base(db, settings)
    {
        _audit = audit;
    }

    // The filter parameter is deliberately not named `action` — that name would have bound the
    // route value (the text "Index") instead of the query string, and filtered the trail to nothing.
    [HttpGet]
    public async Task<IActionResult> Index(
        string? actionName, string? entityType, int? entityId, string? actor, CancellationToken ct)
    {
        var entries = await _audit.GetRecentAsync(actionName, entityType, actor, entityId, take: 200, ct);

        // The dotted action prefixes used so far, offered as quick filters. Derived from the data
        // rather than a static list, so a future writer needs no edit here to be filterable.
        var knownActions = entries.Select(e => e.Action).Distinct().OrderBy(a => a).ToList();
        var knownTypes = entries.Select(e => e.EntityType).Distinct().OrderBy(t => t).ToList();

        ViewData["Title"] = "Audit trail";
        ViewData["ActionName"] = actionName;
        ViewData["EntityType"] = entityType;
        ViewData["EntityId"] = entityId;
        ViewData["Actor"] = actor;
        ViewData["KnownActions"] = knownActions;
        ViewData["KnownTypes"] = knownTypes;

        return View(entries);
    }

    /// <summary>Downloads the trail, honouring the same query the page shows.</summary>
    [HttpGet]
    public async Task<IActionResult> Export(
        string? actionName, string? entityType, int? entityId, string? actor, string? format, CancellationToken ct)
    {
        var entries = await _audit.GetRecentAsync(actionName, entityType, actor, entityId, take: 500, ct);

        var data = new TabularData(
            "Audit trail",
            "audit-trail",
            new[] { "Recorded (UTC)", "Actor", "Action", "Entity type", "Entity id", "Summary" },
            entries.Select(e => new object?[]
            {
                e.CreatedUtc, e.Actor, e.Action, e.EntityType, e.EntityId, e.Summary
            }).ToList());

        return ExportFile(data, format);
    }
}
