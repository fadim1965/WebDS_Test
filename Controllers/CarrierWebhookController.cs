using Microsoft.AspNetCore.Mvc;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services;

namespace WebDS_Test.Controllers;

/// <summary>
/// Carrier webhooks for EPOD (Electronic Proof of Delivery), RTS manifests and quality reports.
/// </summary>
/// <remarks>
/// LibanPost's RTS process involves:
/// 1. Carrier scans EPOD when delivery is attempted
/// 2. If failed (customer unreachable), carrier makes 2 call attempts
/// 3. Carrier files a manifest with the RTS record
/// 4. Quality report may be generated for damaged returns
/// 5. Package is either redelivered or returned to seller
/// This controller receives those callbacks and updates the post-sale request.
/// </remarks>
[Route("webhooks/carrier")]
[ApiController]
public class CarrierWebhookController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IPostSaleService _postSales;
    private readonly IAuditTrail _audit;
    private readonly ILogger<CarrierWebhookController> _logger;

    public CarrierWebhookController(
        ApplicationDbContext db,
        IPostSaleService postSales,
        IAuditTrail audit,
        ILogger<CarrierWebhookController> logger)
    {
        _db = db;
        _postSales = postSales;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// EPOD (Electronic Proof of Delivery) scan callback.
    /// Called by the carrier when a delivery attempt is made.
    /// </summary>
    [HttpPost("epod")]
    public async Task<IActionResult> EpodAsync(
        [FromBody] EpodPayload payload,
        CancellationToken ct = default)
    {
        _logger.LogInformation("EPOD webhook received for tracking {TrackingNumber}: {Status}",
            payload.TrackingNumber, payload.Status);

        // Find the post-sale RTS request by tracking number.
        var request = await _db.PostSaleRequests
            .Include(r => r.Order)
            .FirstOrDefaultAsync(r => r.Type == PostSaleType.RTS
                                     && r.PickupTrackingNumber == payload.TrackingNumber, ct);

        if (request is null)
        {
            _logger.LogWarning("No RTS request found for EPOD tracking {TrackingNumber}", payload.TrackingNumber);
            return NotFound(new { error = "No matching RTS request" });
        }

        // Update the request with the EPOD data.
        var wasDelivered = request.Status == PostSaleStatus.Redelivered;
        request.RedeliveryAttempted = true;

        if (payload.Status?.Equals("DELIVERED", StringComparison.OrdinalIgnoreCase) == true)
        {
            request.Status = PostSaleStatus.Redelivered;
            request.RedeliveryAttempted = true;
            request.ReceivedAtWarehouseUtc = DateTime.UtcNow;
            _logger.LogInformation("RTS {Reference} marked as redelivered via EPOD", request.Reference);
        }
        else if (payload.Status?.Equals("FAILED", StringComparison.OrdinalIgnoreCase) == true)
        {
            request.Status = PostSaleStatus.ReturnedToSeller;
            request.RedeliveryAttempted = true;
            _logger.LogInformation("RTS {Reference} marked as returned to seller via EPOD", request.Reference);
        }
        else
        {
            // Other statuses (ATTEMPTED, OUT_FOR_DELIVERY, etc.) just record the attempt
            request.RedeliveryAttempted = true;
        }

        request.AdminNotes = MergeNote(request.AdminNotes,
            $"EPOD: {payload.Status} at {payload.OccurredUtc:yyyy-MM-dd HH:mm} UTC. {payload.Notes}");
        request.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            "carrier-webhook", "carrier.epod", nameof(PostSaleRequest),
            request.Id, $"EPOD status: {payload.Status}", ct);

        return Ok(new { success = true });
    }

    /// <summary>
    /// RTS manifest callback from carrier.
    /// Called when the carrier files the return-to-sender manifest.
    /// </summary>
    [HttpPost("rts/manifest")]
    public async Task<IActionResult> RtsManifestAsync(
        [FromBody] RtsManifestPayload payload,
        CancellationToken ct = default)
    {
        _logger.LogInformation("RTS manifest webhook received for manifest {ManifestNumber}", payload.ManifestNumber);

        var request = await _db.PostSaleRequests
            .FirstOrDefaultAsync(r => r.Type == PostSaleType.RTS
                                     && r.ManifestNumber == payload.ManifestNumber, ct);

        if (request is null)
        {
            _logger.LogWarning("No RTS request found for manifest {ManifestNumber}", payload.ManifestNumber);
            return NotFound(new { error = "No matching RTS request" });
        }

        request.ManifestNumber = payload.ManifestNumber;
        request.CallAttempts = payload.CallAttempts;
        request.QualityReport = payload.QualityReport;
        request.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            "carrier-webhook", "carrier.rts.manifest", nameof(PostSaleRequest),
            request.Id, $"Manifest {payload.ManifestNumber} received with {payload.CallAttempts} call attempts", ct);

        return Ok(new { success = true });
    }

    /// <summary>
    /// Quality report callback for damaged items in RTS.
    /// </summary>
    [HttpPost("rts/quality-report")]
    public async Task<IActionResult> RtsQualityReportAsync(
        [FromBody] QualityReportPayload payload,
        CancellationToken ct = default)
    {
        _logger.LogInformation("RTS quality report webhook received for {ManifestNumber}", payload.ManifestNumber);

        var request = await _db.PostSaleRequests
            .FirstOrDefaultAsync(r => r.Type == PostSaleType.RTS
                                     && r.ManifestNumber == payload.ManifestNumber, ct);

        if (request is null)
        {
            _logger.LogWarning("No RTS request found for quality report manifest {ManifestNumber}", payload.ManifestNumber);
            return NotFound(new { error = "No matching RTS request" });
        }

        request.QualityReport = payload.Report;
        request.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            "carrier-webhook", "carrier.rts.quality", nameof(PostSaleRequest),
            request.Id, $"Quality report received: {payload.Report}", ct);

        return Ok(new { success = true });
    }

    private static string MergeNote(string? existing, string? addition) =>
        string.IsNullOrWhiteSpace(addition) ? existing ?? string.Empty
            : string.IsNullOrWhiteSpace(existing) ? addition
                : $"{existing}\n{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC: {addition}";
}

/// <summary>EPOD scan payload from carrier.</summary>
public sealed record EpodPayload(
    string TrackingNumber,
    string? Status,
    string? Location,
    string? Notes,
    DateTime? OccurredUtc);

/// <summary>RTS manifest payload from carrier.</summary>
public sealed record RtsManifestPayload(
    string ManifestNumber,
    int CallAttempts,
    string? QualityReport);

/// <summary>Quality report payload from carrier.</summary>
public sealed record QualityReportPayload(
    string ManifestNumber,
    string Report);