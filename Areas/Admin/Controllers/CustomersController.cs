using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebDS_Test.Areas.Admin.Models;
using WebDS_Test.Domain;
using WebDS_Test.Services.Export;

namespace WebDS_Test.Areas.Admin.Controllers;

/// <summary>Customer accounts: search, order history and activation state.</summary>
public class CustomersController : AdminControllerBase
{
    private readonly IAccountService _accounts;
    private readonly IOrderService _orders;

    public CustomersController(
        ApplicationDbContext db,
        IOptions<StoreSettings> settings,
        IAccountService accounts,
        IOrderService orders)
        : base(db, settings)
    {
        _accounts = accounts;
        _orders = orders;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? q, CancellationToken ct)
    {
        ViewData["Title"] = "Customers";

        return View(new AdminCustomerListViewModel
        {
            Customers = await _accounts.GetCustomersAsync(q, ct),
            Search = q
        });
    }

    [HttpGet]
    public async Task<IActionResult> Export(string? q, string? format, CancellationToken ct)
    {
        var customers = await _accounts.GetCustomersAsync(q, ct);

        var rows = customers
            .Select(u => new object?[]
            {
                u.DisplayName,
                u.Email,
                u.IsActive ? "Active" : "Deactivated",
                string.Join(", ", u.UserRoles.Select(r => r.RoleName).OrderBy(r => r)),
                u.CreatedUtc,
                u.LastLoginUtc,
                u.IsLockedOut
            })
            .ToList();

        var data = new TabularData(
            "Customers",
            "customers",
            new[]
            {
                "Name", "Email", "Status", "Roles", "Registered (UTC)", "Last sign-in (UTC)", "Locked out"
            },
            rows);

        return ExportFile(data, format);
    }

    [HttpGet]
    public async Task<IActionResult> Details(string id, CancellationToken ct)
    {
        var customer = await _accounts.GetByIdAsync(id, ct);
        if (customer is null)
        {
            return NotFound();
        }

        var orders = await _orders.GetOrdersForUserAsync(id, ct);

        ViewData["Title"] = customer.DisplayName;

        return View(new AdminCustomerDetailViewModel
        {
            Customer = customer,
            Roles = await _accounts.GetRolesAsync(id, ct),
            Orders = orders,
            LifetimeSpend = orders
                .Where(o => o.Status is OrderStatus.Paid or OrderStatus.Shipped or OrderStatus.Delivered)
                .Sum(o => o.GrandTotal)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetActive(string id, bool isActive, CancellationToken ct)
    {
        // Guard against an administrator locking themselves out of the store.
        if (id == CurrentUserId && !isActive)
        {
            Flash("You cannot deactivate the account you are signed in with.", success: false);
            return RedirectToAction(nameof(Details), new { id });
        }

        var result = await _accounts.SetActiveAsync(id, isActive, ct);
        Flash(result.Message, result.Success);

        return RedirectToAction(nameof(Details), new { id });
    }
}

/// <summary>Backing model for the admin customer detail page.</summary>
public sealed class AdminCustomerDetailViewModel
{
    public ApplicationUser Customer { get; init; } = null!;
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<Order> Orders { get; init; } = Array.Empty<Order>();
    public decimal LifetimeSpend { get; init; }
}
