using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebDS_Test.Domain;
using WebDS_Test.Services;
using WebDS_Test.Services.Export;

namespace WebDS_Test.Areas.Admin.Controllers;

/// <summary>
/// The administration's side of the returns workflow: the queue of every return on the
/// marketplace, and the refund step. Shop staff approve, reject and receive; this area finishes the
/// job by issuing the money, because a refund reverses the commission the marketplace charged as
/// much as it returns the shopper's payment.
/// </summary>
public class ReturnsController : AdminControllerBase
{
    private readonly IReturnService _returns;
    private readonly IAuditTrail _audit;

    public ReturnsController(
        ApplicationDbContext db,
        IOptions<StoreSettings> settings,
        IReturnService returns,
        IAuditTrail audit)
        : base(db, settings)
    {
        _returns = returns;
        _audit = audit;
    }

    [HttpGet]
    public async Task<IActionResult> Index(ReturnStatus? status, string? q, CancellationToken ct)
    {
        var rows = await _returns.GetAllAsync(status, q, 200, ct);

        // Queue badges: how much is waiting on this page beyond the list shown.
        var openCount = await Db.ReturnRequests
            .CountAsync(r => r.Status == ReturnStatus.Requested
                          || r.Status == ReturnStatus.Approved
                          || r.Status == ReturnStatus.Received, ct);
        var requestedCount = await Db.ReturnRequests
            .CountAsync(r => r.Status == ReturnStatus.Requested, ct);

        ViewData["Title"] = "Returns";
        ViewData["Status"] = status;
        ViewData["Search"] = q;
        ViewData["OpenCount"] = openCount;
        ViewData["RequestedCount"] = requestedCount;

        return View(rows);
    }

    /// <summary>Downloads the return queue, honouring the same query the list page shows.</summary>
    [HttpGet]
    public async Task<IActionResult> Export(ReturnStatus? status, string? q, string? format, CancellationToken ct)
    {
        var rows = await _returns.GetAllAsync(status, q, 500, ct);

        var data = new TabularData(
            "Returns",
            "returns",
            new[]
            {
                "Reference", "Order", "Shop", "Requested (UTC)", "Decided (UTC)", "Received (UTC)",
                "Refunded (UTC)", "Shopper email", "Units", "Goods value", "Refund amount", "Status",
                "Customer note", "Decision note", "Handled by"
            },
            rows.Select(r => new object?[]
            {
                r.Reference,
                r.Order?.OrderNumber,
                r.Shop?.Name,
                r.RequestedUtc,
                r.DecidedUtc,
                r.ReceivedUtc,
                r.RefundedUtc,
                r.Email,
                r.Lines.Sum(l => l.Quantity),
                r.Lines.Sum(l => l.LineTotal),
                r.Status == ReturnStatus.Refunded ? r.RefundAmount : 0m,
                r.Status.ToString(),
                r.CustomerNote,
                r.DecisionNote,
                r.HandledBy
            }).ToList());

        return ExportFile(data, format);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var request = await _returns.GetAsync(id, ct);
        if (request is null) return NotFound();

        var history = await _audit.GetRecentAsync(null, "ReturnRequest", null, id, 50, ct);

        ViewData["Title"] = $"Return {request.Reference}";
        ViewData["AuditHistory"] = history;
        return View(request);
    }

    /// <summary>Approves or rejects. Available here too, for when shop staff have not acted.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Decide(int id, bool approve, string? note, CancellationToken ct)
    {
        var request = await _returns.GetAsync(id, ct);
        if (request is null) return NotFound();

        var result = await _returns.DecideAsync(id, approve, note, CurrentUserName, ct);
        Flash(result.Message, result.Success);

        if (result.Success)
        {
            await _audit.RecordAsync(CurrentUserName,
                approve ? "return.approved" : "return.rejected",
                "ReturnRequest", id,
                $"{(approve ? "Approved" : "Rejected")} {request.Reference} on order {request.Order?.OrderNumber}"
                + (string.IsNullOrWhiteSpace(note) ? "." : $": {note}"));
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Records the goods as received and restocks them.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Receive(int id, CancellationToken ct)
    {
        var request = await _returns.GetAsync(id, ct);
        if (request is null) return NotFound();

        var result = await _returns.MarkReceivedAsync(id, CurrentUserName, ct);
        Flash(result.Message, result.Success);

        if (result.Success)
        {
            await _audit.RecordAsync(CurrentUserName, "return.received", "ReturnRequest", id,
                $"Received goods for {request.Reference} ({request.Lines.Sum(l => l.Quantity)} unit(s)) and restocked them.");
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Issues the refund — the gateway returns the shopper's money and the ledger takes back what
    /// the shop earned on it, proportionally. Only successful from the Received state.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Refund(int id, string? note, CancellationToken ct)
    {
        var request = await _returns.GetAsync(id, ct);
        if (request is null) return NotFound();

        var result = await _returns.RefundAsync(id, CurrentUserName, ct);
        Flash(result.Message, result.Success);

        if (result.Success)
        {
            await _audit.RecordAsync(CurrentUserName, "refund.issued", "ReturnRequest", id,
                $"Refunded {result.Return!.RefundAmount:N2} for {request.Reference}"
                + $" on order {request.Order?.OrderNumber}"
                + (result.Return.Status == ReturnStatus.Refunded
                    ? " — the order is fully refunded."
                    : " — partial refund.")
                + (string.IsNullOrWhiteSpace(note) ? "" : $" Note: {note}"));
        }
        else
        {
            await _audit.RecordAsync(CurrentUserName, "refund.failed", "ReturnRequest", id,
                $"Refund attempt for {request.Reference} did not go through: {result.Message}");
        }

        return RedirectToAction(nameof(Details), new { id });
    }
}
