using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebDS_Test.Areas.Admin.Models;
using WebDS_Test.Services;
using WebDS_Test.Services.Export;

namespace WebDS_Test.Areas.Admin.Controllers;

/// <summary>
/// Vendor settlements: what each shop is owed, and the record of paying it.
/// </summary>
/// <remarks>
/// A payout is a settlement, not a transfer of money — the store has no banking integration, so
/// "Pay out" means an administrator has moved the funds by whatever means and is recording it here.
/// The page is built around that: it shows the balance, asks for the method, and keeps the statement.
/// </remarks>
public class PayoutsController : AdminControllerBase
{
    private readonly IPayoutService _payouts;
    private readonly IAuditTrail _audit;

    public PayoutsController(
        ApplicationDbContext db,
        IOptions<StoreSettings> settings,
        IPayoutService payouts,
        IAuditTrail audit)
        : base(db, settings)
    {
        _payouts = payouts;
        _audit = audit;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Payouts";

        var balances = await _payouts.GetOutstandingByShopAsync(ct);
        var history = await _payouts.GetPayoutsAsync(null, 50, ct);

        return View(new AdminPayoutIndexViewModel
        {
            Balances = balances,
            History = history,
            Currency = Settings.CurrencySymbol
        });
    }

    /// <summary>
    /// Settles everything a shop is currently owed in one payout. The service refuses when there is
    /// nothing (or a non-positive balance) to settle, so a double-submit cannot create an empty one.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(int shopId, string? method, string? note, CancellationToken ct)
    {
        var result = await _payouts.CreatePayoutAsync(shopId, method, note, CurrentUserName, ct);

        Flash(result.Message, result.Success);

        if (result is { Success: true, Payout: { } created })
        {
            await _audit.RecordAsync(CurrentUserName, "payout.created", "VendorPayout", created.Id,
                $"Created {created.Reference} for shop {created.Shop?.Name ?? shopId.ToString()} — net {created.NetAmount:N2}, {created.EntryCount} ledger entries, via {created.Method}.");

            return RedirectToAction(nameof(Details), new { id = created.Id });
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// The bank refused the transfer. The payout is marked failed and every entry it covered goes
    /// back to unsettled, so the shop's payable re-opens — the statement keeps its totals.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkFailed(int id, string reason, CancellationToken ct)
    {
        var payout = await _payouts.GetPayoutAsync(id, ct);
        if (payout is null)
        {
            return NotFound();
        }

        var result = await _payouts.MarkFailedAsync(id, reason, CurrentUserName, ct);
        Flash(result.Message, result.Success);

        if (result.Success)
        {
            await _audit.RecordAsync(CurrentUserName, "payout.failed", "VendorPayout", id,
                $"{payout.Reference} ({payout.NetAmount:N2} to {payout.Shop?.Name ?? "(shop removed)"}) marked failed: {reason}.");
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// The payout was recorded in error. Same release of the covered entries as a failure; the row
    /// is marked Reversed rather than Failed because the reason is bookkeeping, not the bank.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reverse(int id, string? note, CancellationToken ct)
    {
        var payout = await _payouts.GetPayoutAsync(id, ct);
        if (payout is null)
        {
            return NotFound();
        }

        var result = await _payouts.ReverseAsync(id, note, CurrentUserName, ct);
        Flash(result.Message, result.Success);

        if (result.Success)
        {
            await _audit.RecordAsync(CurrentUserName, "payout.reversed", "VendorPayout", id,
                $"{payout.Reference} ({payout.NetAmount:N2} to {payout.Shop?.Name ?? "(shop removed)"}) reversed"
                + (string.IsNullOrWhiteSpace(note) ? ": recorded in error." : $": {note}"));
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var payout = await _payouts.GetPayoutAsync(id, ct);
        if (payout is null)
        {
            return NotFound();
        }

        ViewData["Title"] = $"Payout {payout.Reference}";

        return View(new AdminPayoutDetailViewModel
        {
            Payout = payout,
            Lines = await _payouts.GetStatementLinesAsync(id, ct),
            Currency = Settings.CurrencySymbol
        });
    }

    /// <summary>Downloads the settlement history, honouring the same query the list page shows.</summary>
    [HttpGet]
    public async Task<IActionResult> Export(string? format, CancellationToken ct)
    {
        var history = await _payouts.GetPayoutsAsync(null, 500, ct);
        var currency = Settings.CurrencySymbol;

        var rows = history.Select(p => new object?[]
        {
            p.Reference,
            p.Shop?.Name ?? "(shop removed)",
            p.CreatedUtc,
            p.PeriodStartUtc,
            p.PeriodEndUtc,
            p.EntryCount,
            p.GrossAmount,
            p.CommissionAmount,
            p.NetAmount,
            currency,
            p.Method,
            p.Note,
            p.CreatedBy
        }).ToList();

        var data = new TabularData(
            "Payouts",
            "payouts",
            new[]
            {
                "Reference", "Shop", "Paid (UTC)", "Period start", "Period end", "Entries",
                "Gross", "Commission", "Net", "Currency", "Method", "Note", "Created by"
            },
            rows);

        return ExportFile(data, format);
    }

    /// <summary>Downloads a single payout's statement — the ledger entries it settled.</summary>
    [HttpGet]
    public async Task<IActionResult> Statement(int id, string? format, CancellationToken ct)
    {
        var payout = await _payouts.GetPayoutAsync(id, ct);
        if (payout is null)
        {
            return NotFound();
        }

        var lines = await _payouts.GetStatementLinesAsync(id, ct);

        var rows = lines.Select(l => new object?[]
        {
            l.Id,
            l.CreatedUtc,
            l.Type.ToString(),
            l.OrderId,
            l.GrossAmount,
            l.CommissionAmount,
            l.NetAmount,
            l.Note
        }).ToList();

        var data = new TabularData(
            "Statement",
            $"payout-{payout.Reference}",
            new[] { "Entry", "Earned (UTC)", "Type", "Order id", "Gross", "Commission", "Net", "Note" },
            rows);

        return ExportFile(data, format);
    }
}
