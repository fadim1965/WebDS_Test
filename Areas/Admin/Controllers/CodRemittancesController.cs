using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebDS_Test.Areas.Admin.Models;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services;

namespace WebDS_Test.Areas.Admin.Controllers;

/// <summary>
/// Carrier cash-on-delivery remittances: the batch of money a carrier says it paid over, reconciled
/// line by line against the orders we expected cash from.
/// </summary>
public class CodRemittancesController : AdminControllerBase
{
    private readonly ICodRemittanceService _remittances;
    private readonly IAuditTrail _audit;

    public CodRemittancesController(
        ApplicationDbContext db,
        IOptions<StoreSettings> settings,
        ICodRemittanceService remittances,
        IAuditTrail audit)
        : base(db, settings)
    {
        _remittances = remittances;
        _audit = audit;
    }

    [HttpGet]
    public async Task<IActionResult> Index(RemittanceStatus? status, CancellationToken ct)
    {
        var rows = await _remittances.ListAsync(status, ct);

        ViewData["Title"] = "COD remittances";
        ViewData["Status"] = status;
        ViewData["OpenCount"] = rows.Count(r => r.Status is RemittanceStatus.Received or RemittanceStatus.Disputed);
        ViewData["DisputedCount"] = rows.Count(r => r.Status == RemittanceStatus.Disputed);

        return View(rows);
    }

    [HttpGet]
    public IActionResult Create()
    {
        ViewData["Title"] = "New COD remittance";
        return View(new CodRemittanceCreateForm
        {
            RemittanceDateUtc = DateTime.UtcNow.Date,
            Tolerance = 0.01m
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CodRemittanceCreateForm form, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            ViewData["Title"] = "New COD remittance";
            return View(form);
        }

        var lines = ParseLines(form.Lines);

        var result = await _remittances.CreateAsync(new CarrierRemittanceCreateInput
        {
            Carrier = form.Carrier,
            Reference = form.Reference,
            BankReference = form.BankReference,
            RemittanceDateUtc = form.RemittanceDateUtc,
            Notes = form.Notes,
            Tolerance = form.Tolerance,
            Lines = lines
        }, CurrentUserName, ct);

        Flash(result.Message, result.Success);

        if (result.Success && result.Remittance is { } created)
        {
            await _audit.RecordAsync(CurrentUserName, "cod-remittance.created", "CarrierRemittance", created.Id,
                $"Created remittance {created.Carrier}/{created.Reference} with {created.Lines.Count} line(s), actual {created.ActualAmount:N2}.");
            return RedirectToAction(nameof(Details), new { id = created.Id });
        }

        ViewData["Title"] = "New COD remittance";
        return View(form);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var remittance = await _remittances.GetAsync(id, ct);
        if (remittance is null)
        {
            return NotFound();
        }

        ViewData["Title"] = $"Remittance {remittance.Carrier}/{remittance.Reference}";
        return View(remittance);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateLine(int id, int lineId, decimal actualAmount, string? note, CancellationToken ct)
    {
        var result = await _remittances.UpdateLineAsync(id, lineId, actualAmount, note, ct);
        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AutoMatch(int id, DateTime fromUtc, DateTime toUtc, string? carrier, CancellationToken ct)
    {
        var result = await _remittances.AutoMatchAsync(id, fromUtc, toUtc, carrier, ct);
        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reconcile(int id, CancellationToken ct)
    {
        var remittance = await _remittances.GetAsync(id, ct);
        if (remittance is null)
        {
            return NotFound();
        }

        var result = await _remittances.ReconcileAsync(id, CurrentUserName, ct);
        Flash(result.Message, result.Success);

        if (result.Success)
        {
            await _audit.RecordAsync(CurrentUserName, "cod-remittance.reconciled", "CarrierRemittance", id,
                $"Reconciled {remittance.Carrier}/{remittance.Reference}: expected {result.Remittance!.ExpectedAmount:N2}, actual {result.Remittance.ActualAmount:N2}, variance {result.Remittance.Variance:N2}.");
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Dispute(int id, string note, CancellationToken ct)
    {
        var result = await _remittances.DisputeAsync(id, CurrentUserName, note, ct);
        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(int id, string? note, CancellationToken ct)
    {
        var result = await _remittances.CloseAsync(id, CurrentUserName, note, ct);
        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var result = await _remittances.DeleteAsync(id, ct);
        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Index));
    }

    private static List<CarrierRemittanceLineInput> ParseLines(string? raw)
    {
        var result = new List<CarrierRemittanceLineInput>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        foreach (var line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            var orderNumber = parts[0].Trim();
            decimal amount = 0m;
            if (parts.Length >= 2 && decimal.TryParse(parts[1], out var parsed))
            {
                amount = parsed;
            }

            string? note = parts.Length >= 3 ? string.Join(" ", parts.Skip(2)) : null;

            result.Add(new CarrierRemittanceLineInput
            {
                OrderNumber = orderNumber,
                ActualAmount = amount,
                Note = note
            });
        }

        return result;
    }
}

/// <summary>Form model for creating a remittance from a pasted carrier statement.</summary>
public sealed class CodRemittanceCreateForm
{
    [System.ComponentModel.DataAnnotations.Required]
    public string Carrier { get; set; } = "libanpost";

    [System.ComponentModel.DataAnnotations.Required]
    public string Reference { get; set; } = string.Empty;

    public string? BankReference { get; set; }

    public DateTime RemittanceDateUtc { get; set; } = DateTime.UtcNow.Date;

    public string? Notes { get; set; }

    public decimal Tolerance { get; set; } = 0.01m;

    /// <summary>One line per order: <c>ORDER-NUMBER AMOUNT NOTE...</c></summary>
    public string? Lines { get; set; }
}
