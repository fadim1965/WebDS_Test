using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebDS_Test.Domain;
using WebDS_Test.Services;

namespace WebDS_Test.Areas.Admin.Controllers;

/// <summary>
/// The LibanPost after-sales desk: the queue of every cancellation, return, exchange, RTS and
/// warranty request on the marketplace, and the controls that move each one through its process.
/// </summary>
/// <remarks>
/// The operator approves or rejects, records the goods as picked up and received, and issues the
/// remedy. The fee split itself is not editable here: it was decided by
/// <see cref="PostSaleRules"/> when the customer raised the request, and the page shows it so the
/// operator can explain the decision rather than invent one.
/// </remarks>
public class PostSaleController : AdminControllerBase
{
    private readonly IPostSaleService _postSales;
    private readonly IAuditTrail _audit;

    public PostSaleController(
        ApplicationDbContext db,
        IOptions<StoreSettings> settings,
        IPostSaleService postSales,
        IAuditTrail audit)
        : base(db, settings)
    {
        _postSales = postSales;
        _audit = audit;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        PostSaleStatus? status, PostSaleType? type, string? search, CancellationToken ct)
    {
        var requests = await _postSales.GetAllAsync(status, type, search, 300, ct);
        var counts = await _postSales.GetCountsAsync(ct);

        ViewData["Status"] = status;
        ViewData["Type"] = type;
        ViewData["Search"] = search;
        ViewData["Counts"] = counts;
        ViewData["Title"] = "After-sales queue";

        return View(requests);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var request = await _postSales.GetAsync(id, ct);
        if (request is null)
        {
            return NotFound();
        }

        ViewData["Title"] = $"Request {request.Reference}";
        return View(request);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, string? note, CancellationToken ct)
    {
        var result = await _postSales.DecideAsync(id, approve: true, note, CurrentUserName, ct);
        await RecordAsync(result, "post-sale.approve", id, ct);
        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id, string? note, CancellationToken ct)
    {
        var result = await _postSales.DecideAsync(id, approve: false, note, CurrentUserName, ct);
        await RecordAsync(result, "post-sale.reject", id, ct);
        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Transition(
        int id, PostSaleStatus next, string? note, string? pickupTrackingNumber, CancellationToken ct)
    {
        var result = await _postSales.TransitionAsync(
            id, next, CurrentUserName, note, pickupTrackingNumber, ct);
        await RecordAsync(result, "post-sale.transition", id, ct);
        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Process(
        int id, string? note, int? replacementVariantId, CancellationToken ct)
    {
        var result = await _postSales.ProcessAsync(
            id, CurrentUserName, note, replacementVariantId, ct);
        await RecordAsync(result, "post-sale.process", id, ct);
        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TriageWarranty(int id, bool canBeRepaired, bool canBeReplaced, string? note, CancellationToken ct)
    {
        var result = await _postSales.TriageWarrantyAsync(
            id, canBeRepaired, canBeReplaced, CurrentUserName, note, ct);
        await RecordAsync(result, "post-sale.warranty.triage", id, ct);
        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Message(int id, string content, CancellationToken ct)
    {
        var result = await _postSales.AddMessageAsync(
            id, CurrentUserId ?? "admin", CurrentUserName, fromCustomer: false, content, ct);
        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>The form for recording a carrier's return-to-sender, by order number or id.</summary>
    [HttpGet]
    public async Task<IActionResult> Rts(int? orderId, string? orderNumber, CancellationToken ct)
    {
        Order? order = null;

        if (orderId.HasValue)
        {
            order = await Db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == orderId, ct);
        }
        else if (!string.IsNullOrWhiteSpace(orderNumber))
        {
            var term = orderNumber.Trim();
            order = await Db.Orders.Include(o => o.Lines)
                .FirstOrDefaultAsync(o => o.OrderNumber == term, ct);
        }

        ViewData["Title"] = "Record a return to sender";
        ViewData["OrderNumber"] = orderNumber;
        return View(order);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rts(
        int orderId, string carrier, int callAttempts, string? manifestNumber, string? qualityReport, string? note, CancellationToken ct)
    {
        var fullNote = string.IsNullOrWhiteSpace(note) ? string.Empty : note + "\n";
        if (!string.IsNullOrWhiteSpace(manifestNumber))
        {
            fullNote += $"Manifest: {manifestNumber}\n";
        }
        if (!string.IsNullOrWhiteSpace(qualityReport))
        {
            fullNote += $"Quality report: {qualityReport}\n";
        }

        var result = await _postSales.CreateRtsAsync(
            orderId, CurrentUserName, carrier, callAttempts, fullNote.Trim(), ct);

        // If manifest/quality provided, update the request directly.
        if (result.Success && result.Request is { } rtsRequest)
        {
            if (!string.IsNullOrWhiteSpace(manifestNumber))
            {
                rtsRequest.ManifestNumber = manifestNumber.Trim();
            }
            if (!string.IsNullOrWhiteSpace(qualityReport))
            {
                rtsRequest.QualityReport = qualityReport.Trim();
            }
            await Db.SaveChangesAsync(ct);
        }

        await RecordAsync(result, "post-sale.rts", result.Request?.Id, ct);
        Flash(result.Message, result.Success);

        return result is { Success: true, Request: { } req }
            ? RedirectToAction(nameof(Details), new { id = req.Id })
            : RedirectToAction(nameof(Rts), new { orderId });
    }

    /// <summary>The back office's label queue: what the sellers have packed and what is waiting.</summary>
    [HttpGet]
    public async Task<IActionResult> Fulfilments(bool openOnly = true, CancellationToken ct = default)
    {
        var rows = await _postSales.GetFulfilmentsAsync(openOnly, ct);

        ViewData["Title"] = "Packages & labels";
        ViewData["OpenOnly"] = openOnly;
        return View(rows);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateLabels(int id, CancellationToken ct)
    {
        var result = await _postSales.GenerateFulfilmentLabelsAsync(id, CurrentUserName, ct);

        if (result.Fulfilment is { } fulfilment)
        {
            await _audit.RecordAsync(
                CurrentUserName, "post-sale.fulfilment.labels", nameof(SellerFulfilment),
                fulfilment.Id, result.Message, ct);
        }

        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Fulfilments));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RecordPickup(int id, CancellationToken ct)
    {
        var result = await _postSales.RecordFulfilmentPickupAsync(id, CurrentUserName, ct);

        if (result.Fulfilment is { } fulfilment)
        {
            await _audit.RecordAsync(
                CurrentUserName, "post-sale.fulfilment.pickup", nameof(SellerFulfilment),
                fulfilment.Id, result.Message, ct);
        }

        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Fulfilments));
    }

    private async Task RecordAsync(
        PostSaleResult result, string action, int? requestId, CancellationToken ct)
    {
        if (!result.Success && result.Request is null)
        {
            return;
        }

        await _audit.RecordAsync(
            CurrentUserName,
            action,
            nameof(PostSaleRequest),
            result.Request?.Id ?? requestId,
            result.Message,
            ct);
    }
}
