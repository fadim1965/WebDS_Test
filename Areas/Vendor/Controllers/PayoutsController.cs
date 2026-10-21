using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebDS_Test.Areas.Vendor.Models;
using WebDS_Test.Services;

namespace WebDS_Test.Areas.Vendor.Controllers;

/// <summary>
/// A vendor's own settlements, read-only. Vendors see exactly what the marketplace owes them and the
/// payout that cleared it, but they cannot create a payout — that is the marketplace's action.
/// </summary>
[Area("Vendor")]
[Authorize(Policy = "VendorOnly")]
public class PayoutsController : Controller
{
    private readonly IShopService _shops;
    private readonly IPayoutService _payouts;

    public PayoutsController(IShopService shops, IPayoutService payouts)
    {
        _shops = shops;
        _payouts = payouts;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null)
        {
            return Challenge();
        }

        var shop = await _shops.GetPrimaryShopForUserAsync(userId, ct);
        if (shop is null)
        {
            return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });
        }

        ViewData["Title"] = "Payouts";
        ViewData["Shop"] = shop;

        return View(new VendorPayoutViewModel
        {
            Shop = shop,
            Outstanding = await _payouts.GetOutstandingAsync(shop.Id, ct),
            Payouts = await _payouts.GetPayoutsAsync(shop.Id, 50, ct),
            Ledger = await _payouts.GetLedgerAsync(shop.Id, 25, ct)
        });
    }

    /// <summary>
    /// Updates the shop's payout destination. Registrations collect it too; this keeps an approved
    /// shop's bank details theirs to maintain rather than a support ticket.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBank(
        string? bankAccountName, string? bankName, string? bankIban, CancellationToken ct)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null)
        {
            return Challenge();
        }

        var shop = await _shops.GetPrimaryShopForUserAsync(userId, ct);
        if (shop is null)
        {
            return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });
        }

        var result = await _shops.UpdateSettingsAsync(shop.Id, s =>
        {
            s.BankAccountName = Truncate(bankAccountName, 120);
            s.BankName = Truncate(bankName, 120);
            s.BankIban = Truncate(bankIban, 40);
        }, ct);

        TempData["Message"] = result.Success
            ? "Payout details saved."
            : result.Message;
        TempData["MessageType"] = result.Success ? "success" : "danger";

        return RedirectToAction(nameof(Index));
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null)
        {
            return Challenge();
        }

        var shop = await _shops.GetPrimaryShopForUserAsync(userId, ct);
        if (shop is null)
        {
            return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });
        }

        var payout = await _payouts.GetPayoutAsync(id, ct);

        // Payout ids are sequential and guessable, so the shop check is the authorisation — a vendor
        // asking for another vendor's payout must get nothing, not a redacted something.
        if (payout is null || payout.ShopId != shop.Id)
        {
            return NotFound();
        }

        ViewData["Title"] = $"Payout {payout.Reference}";
        ViewData["Shop"] = shop;

        return View(new VendorPayoutDetailViewModel
        {
            Shop = shop,
            Payout = payout,
            Lines = await _payouts.GetStatementLinesAsync(id, ct)
        });
    }
}
