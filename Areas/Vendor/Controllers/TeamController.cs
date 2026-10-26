using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebDS_Test.Areas.Vendor.Models;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services;

namespace WebDS_Test.Areas.Vendor.Controllers;

/// <summary>
/// Who can operate this shop. The owner adds colleagues by the email address they registered with,
/// chooses one of the two supporting roles, and removes them again.
/// </summary>
/// <remarks>
/// <para>
/// The role model already existed on <see cref="ShopUser"/>; this is the surface that makes it
/// reachable. Without it every shop had exactly one member — the owner — and the role checks
/// elsewhere were untestable in practice.
/// </para>
/// <para>
/// Owner-only, on both the screen and the two actions. A manager can run the shop's trading but
/// cannot change who runs it, and no action here may remove or create an owner: ownership transfer
/// is a marketplace decision, not a colleague's.
/// </para>
/// </remarks>
[Area("Vendor")]
[Authorize(Policy = "VendorOnly")]
public class TeamController : Controller
{
    private readonly IShopService _shops;
    private readonly ApplicationDbContext _db;
    private readonly IAuditTrail _audit;

    public TeamController(IShopService shops, ApplicationDbContext db, IAuditTrail audit)
    {
        _shops = shops;
        _db = db;
        _audit = audit;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var (shop, access) = await ResolveAsync(ct);
        if (shop is null)
        {
            return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });
        }

        if (access?.CanManageTeam() != true)
        {
            return Forbid();
        }

        ViewData["Title"] = "Team";
        ViewData["ShopName"] = shop.Name;

        return View(new TeamIndexViewModel
        {
            Shop = shop,
            Members = await MembersAsync(shop.Id, ct)
        });
    }

    /// <summary>Grants an existing account access to this shop with the chosen role.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Invite(string? email, ShopRole role, CancellationToken ct)
    {
        var (shop, access) = await ResolveAsync(ct);
        if (shop is null)
        {
            return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });
        }

        if (access?.CanManageTeam() != true)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            Flash("Enter the email address of an existing account.", success: false);
            return RedirectToAction(nameof(Index));
        }

        if (role is not (ShopRole.Manager or ShopRole.Fulfilment))
        {
            Flash("Choose manager or fulfilment. A shop has one owner, and ownership is not granted here.", success: false);
            return RedirectToAction(nameof(Index));
        }

        var result = await _shops.AddUserAsync(shop.Id, email, role, ct);
        Flash(result.Message, result.Success);

        if (result.Success)
        {
            await _audit.RecordAsync(
                User.Identity?.Name ?? "vendor",
                "team.member.added",
                "Shop",
                shop.Id,
                $"Gave {email.Trim()} the {role} role at shop {shop.Name}.");
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>Revokes a colleague's access. The owner cannot be removed here.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(string userId, CancellationToken ct)
    {
        var (shop, access) = await ResolveAsync(ct);
        if (shop is null)
        {
            return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });
        }

        if (access?.CanManageTeam() != true)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            return RedirectToAction(nameof(Index));
        }

        var member = await _db.ShopUsers.AsNoTracking()
            .Include(u => u.User)
            .FirstOrDefaultAsync(u => u.ShopId == shop.Id && u.UserId == userId, ct);

        var result = await _shops.RemoveUserAsync(shop.Id, userId, ct);
        Flash(result.Message, result.Success);

        if (result.Success)
        {
            await _audit.RecordAsync(
                User.Identity?.Name ?? "vendor",
                "team.member.removed",
                "Shop",
                shop.Id,
                $"Removed {member?.User?.Email ?? userId} from shop {shop.Name}.");
        }

        return RedirectToAction(nameof(Index));
    }

    private async Task<List<TeamMemberRow>> MembersAsync(int shopId, CancellationToken ct)
    {
        var rows = await _db.ShopUsers.AsNoTracking()
            .Include(u => u.User)
            .Where(u => u.ShopId == shopId)
            .OrderBy(u => u.Role)
            .ThenBy(u => u.CreatedUtc)
            .ToListAsync(ct);

        return rows.Select(u => new TeamMemberRow
        {
            UserId = u.UserId,
            DisplayName = string.IsNullOrWhiteSpace(u.User?.DisplayName) ? "(account removed)" : u.User!.DisplayName,
            Email = u.User?.Email ?? string.Empty,
            Role = u.Role,
            GrantedUtc = u.CreatedUtc,
            AcceptedTermsUtc = u.AcceptedTermsUtc
        }).ToList();
    }

    private async Task<(Shop? Shop, ShopAccess? Access)> ResolveAsync(CancellationToken ct)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null)
        {
            return (null, null);
        }

        var shop = await _shops.GetPrimaryShopForUserAsync(userId, ct);
        if (shop is null)
        {
            return (null, null);
        }

        return (shop, await _shops.ResolveAccessAsync(userId, shop.Id, ct));
    }

    private void Flash(string message, bool success = true)
    {
        TempData["Message"] = message;
        TempData["MessageType"] = success ? "success" : "danger";
    }
}
