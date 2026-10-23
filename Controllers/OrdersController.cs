using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebDS_Test.Domain;

namespace WebDS_Test.Controllers;

/// <summary>Customer order history and order detail.</summary>
[Authorize]
public class OrdersController : StoreControllerBase
{
    /// <summary>
    /// Session slot listing order ids this browser is allowed to view. Guest checkouts have no
    /// user id to match on, so without this a guest could not see their own confirmation.
    /// </summary>
    public const string ViewableOrdersSessionKey = "WebDS.ViewableOrders";

    private readonly IOrderService _orders;
    private readonly IReturnService _returns;
    private readonly IPostSaleService _postSales;

    public OrdersController(
        ICatalogService catalog,
        IOptions<StoreSettings> settings,
        ICartService cart,
        IOrderService orders,
        IReturnService returns,
        IPostSaleService postSales)
        : base(catalog, settings, cart)
    {
        _orders = orders;
        _returns = returns;
        _postSales = postSales;
    }

    /// <summary>The signed-in shopper, or null for a guest session.</summary>
    private string? CurrentUserId =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        await LoadCartBadgeAsync(ct);

        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var orders = userId is null
            ? Array.Empty<Order>()
            : (await _orders.GetOrdersForUserAsync(userId, ct)).ToArray();

        ViewData["Title"] = "Your orders";
        return View(orders);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        await LoadCartBadgeAsync(ct);

        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var order = await _orders.GetOrderAsync(id, userId, ct);

        // A guest order placed in this browser is still readable from the same session.
        if (order is null && IsViewableInSession(id))
        {
            order = await _orders.GetOrderAsync(id, null, ct);
        }

        if (order is null)
        {
            return NotFound();
        }

        // Returns are only shown on an account's own order — a guest session has no account to
        // request one with, so the panel would only ever render empty for them anyway.
        if (order.UserId is not null && order.UserId == userId)
        {
            ViewData["Returns"] = await _returns.GetForOrderAsync(order.Id, ct);
            ViewData["PostSales"] = await _postSales.GetForOrderAsync(order.Id, ct);
        }

        ViewData["Title"] = $"Order {order.OrderNumber}";
        return View(order);
    }

    // ------------------------------------------------------------------
    // Returns
    // ------------------------------------------------------------------

    /// <summary>
    /// The "ask the shop to take something back" form. The quantity inputs are prefilled with what
    /// is still returnable — earlier refunded returns have already taken their units.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> RequestReturn(int id, CancellationToken ct)
    {
        await LoadCartBadgeAsync(ct);

        var userId = CurrentUserId;
        if (userId is null)
        {
            TempData["Message"] = "Sign in to request a return.";
            return RedirectToAction("Login", "Account");
        }

        var order = await _orders.GetOrderAsync(id, userId, ct);
        if (order is null)
        {
            return NotFound();
        }

        var model = await BuildRequestReturnViewModelAsync(order, userId, ct);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestReturn(RequestReturnViewModel model, CancellationToken ct)
    {
        await LoadCartBadgeAsync(ct);

        var userId = CurrentUserId;
        if (userId is null)
        {
            return Forbid();
        }

        var order = await _orders.GetOrderAsync(model.OrderId, userId, ct);
        if (order is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            // Rebuild the reference data the form depends on, then show the posted input back.
            var rebuilt = await BuildRequestReturnViewModelAsync(order, userId, ct);
            rebuilt.Note = model.Note;
            foreach (var line in rebuilt.Lines)
            {
                line.Quantity = model.Lines.FirstOrDefault(x => x.OrderLineId == line.OrderLineId)?.Quantity ?? 0;
            }
            return View(rebuilt);
        }

        var inputs = model.Lines
            .Where(l => l.Quantity > 0)
            .Select(l => new ReturnLineInput(l.OrderLineId, l.Quantity))
            .ToList();

        var result = await _returns.CreateAsync(order.Id, userId, inputs, model.Note, ct);
        TempData["Message"] = result.Message;
        TempData["MessageType"] = result.Success ? "success" : "danger";

        if (!result.Success)
        {
            var rebuilt = await BuildRequestReturnViewModelAsync(order, userId, ct);
            rebuilt.Note = model.Note;
            foreach (var line in rebuilt.Lines)
            {
                line.Quantity = model.Lines.FirstOrDefault(x => x.OrderLineId == line.OrderLineId)?.Quantity ?? 0;
            }
            return View(rebuilt);
        }

        return RedirectToAction(nameof(Details), new { id = order.Id });
    }

    /// <summary>Withdraws one of the shopper's own open returns.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelReturn(int id, CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (userId is null)
        {
            return Forbid();
        }

        var result = await _returns.CancelAsync(id, userId, ct);
        TempData["Message"] = result.Message;
        TempData["MessageType"] = result.Success ? "success" : "danger";

        return result is { Success: true, Return: { } request }
            ? RedirectToAction(nameof(Details), new { id = request.OrderId })
            : RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Builds the request form for an order: which lines still have returnable units, and whether
    /// a request can be raised at all right now.
    /// </summary>
    private async Task<RequestReturnViewModel> BuildRequestReturnViewModelAsync(
        Order order, string userId, CancellationToken ct)
    {
        var history = await _returns.GetForOrderAsync(order.Id, ct);
        var refundedQty = history
            .Where(r => r.Status == ReturnStatus.Refunded)
            .SelectMany(r => r.Lines)
            .GroupBy(l => l.OrderLineId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        var model = new RequestReturnViewModel
        {
            OrderId = order.Id,
            OrderNumber = order.OrderNumber,
            ReturnableStatus = order.Status is OrderStatus.Paid or OrderStatus.Shipped or OrderStatus.Delivered,
            OpenReturnExists = history.Any(r => r.Status is ReturnStatus.Requested or ReturnStatus.Approved or ReturnStatus.Received)
        };

        foreach (var line in order.Lines)
        {
            var already = refundedQty.TryGetValue(line.Id, out var q) ? q : 0;
            model.Lines.Add(new RequestReturnLineViewModel
            {
                OrderLineId = line.Id,
                ProductName = line.ProductName,
                VariantName = line.VariantName,
                UnitPrice = line.UnitPrice,
                OrderedQuantity = line.Quantity,
                MaxQuantity = Math.Max(0, line.Quantity - already)
            });
        }

        return model;
    }

    /// <summary>Order lookup by order number plus the email it was placed with.</summary>
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Track() => View(new TrackOrderViewModel());

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Track(TrackOrderViewModel model, CancellationToken ct)
    {
        await LoadCartBadgeAsync(ct);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var order = await _orders.GetByNumberAsync(model.OrderNumber.Trim(), null, ct);

        // Require the matching email so order numbers cannot be enumerated.
        if (order is null || !string.Equals(order.Email, model.Email.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(string.Empty, "We could not find an order with that number and email address.");
            return View(model);
        }

        ViewData["Title"] = $"Order {order.OrderNumber}";
        return View("Details", order);
    }

    private bool IsViewableInSession(int orderId)
    {
        var raw = HttpContext.Session.GetString(ViewableOrdersSessionKey);
        return !string.IsNullOrEmpty(raw) && raw.Split(',').Select(int.Parse).Contains(orderId);
    }
}

/// <summary>Backing model for the guest order-tracking form.</summary>
public sealed class TrackOrderViewModel
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter your order number.")]
    [System.ComponentModel.DataAnnotations.Display(Name = "Order number")]
    public string OrderNumber { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter the email used for the order.")]
    [System.ComponentModel.DataAnnotations.EmailAddress]
    public string Email { get; set; } = string.Empty;
}

/// <summary>Form values for raising a return: a note and one row per order line.</summary>
public sealed class RequestReturnViewModel
{
    public int OrderId { get; set; }

    public string OrderNumber { get; set; } = string.Empty;

    /// <summary>True when the order's status allows returns at all.</summary>
    public bool ReturnableStatus { get; set; }

    public bool OpenReturnExists { get; set; }

    [System.ComponentModel.DataAnnotations.StringLength(1000)]
    [System.ComponentModel.DataAnnotations.Display(Name = "What is wrong with the items?")]
    public string? Note { get; set; }

    public List<RequestReturnLineViewModel> Lines { get; set; } = new();

    public bool CanRequest => ReturnableStatus && !OpenReturnExists;
}

/// <summary>One row of the request form: which order line, how many units.</summary>
public sealed class RequestReturnLineViewModel
{
    public int OrderLineId { get; set; }

    public string ProductName { get; set; } = string.Empty;

    public string? VariantName { get; set; }

    public decimal UnitPrice { get; set; }

    public int OrderedQuantity { get; set; }

    /// <summary>Units still returnable — ordered minus units earlier returns took.</summary>
    [System.ComponentModel.DataAnnotations.Range(0, 999)]
    public int MaxQuantity { get; set; }

    /// <summary>The posted quantity. The form's own validation also caps it to MaxQuantity.</summary>
    public int Quantity { get; set; }
}
