using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebDS_Test.Domain;
using WebDS_Test.Services;

namespace WebDS_Test.Controllers;

/// <summary>
/// The shopper's side of the LibanPost post-sale processes: cancel, return, exchange and warranty.
/// </summary>
/// <remarks>
/// The customer can raise a request and talk to the operator; every decision that moves money or
/// goods is taken in the administration (or by the shop). A request id that belongs to another
/// shopper answers <see cref="NotFoundResult"/>, exactly like a missing one.
/// </remarks>
[Authorize]
public class PostSaleController : StoreControllerBase
{
    private static readonly PostSaleType[] CustomerTypes =
    {
        PostSaleType.Cancellation, PostSaleType.Return, PostSaleType.Exchange, PostSaleType.Warranty
    };

    private readonly IOrderService _orders;
    private readonly IPostSaleService _postSales;

    public PostSaleController(
        ICatalogService catalog,
        IOptions<StoreSettings> settings,
        ICartService cart,
        IOrderService orders,
        IPostSaleService postSales)
        : base(catalog, settings, cart)
    {
        _orders = orders;
        _postSales = postSales;
    }

    private string? CurrentUserId =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    private string CurrentUserName => User.Identity?.Name ?? "Customer";

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        await LoadCartBadgeAsync(ct);

        var userId = CurrentUserId;
        var requests = userId is null
            ? Array.Empty<PostSaleRequest>()
            : (await _postSales.GetForUserAsync(userId, ct)).ToArray();

        ViewData["Title"] = "Cancellations, returns & exchanges";
        return View(requests);
    }

    /// <summary>
    /// The request form. <paramref name="type"/> selects the process and <paramref name="lineId"/>
    /// pre-selects the item (used by the buttons on the order page).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Create(int orderId, PostSaleType type = PostSaleType.Return, int? lineId = null, CancellationToken ct = default)
    {
        await LoadCartBadgeAsync(ct);

        var userId = CurrentUserId;
        if (userId is null)
        {
            return Challenge();
        }

        var order = await _orders.GetOrderAsync(orderId, userId, ct);
        if (order is null)
        {
            return NotFound();
        }

        if (!CustomerTypes.Contains(type))
        {
            Flash("That kind of request cannot be raised from the storefront.", false);
            return RedirectToAction("Details", "Orders", new { id = orderId });
        }

        return View(BuildModel(order, type, lineId));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(PostSaleCreateViewModel model, CancellationToken ct)
    {
        await LoadCartBadgeAsync(ct);

        var userId = CurrentUserId;
        if (userId is null)
        {
            return Challenge();
        }

        var order = await _orders.GetOrderAsync(model.OrderId, userId, ct);
        if (order is null)
        {
            return NotFound();
        }

        if (!CustomerTypes.Contains(model.Type))
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            var rebuilt = BuildModel(order, model.Type, model.OrderLineId);
            rebuilt.Reason = model.Reason;
            rebuilt.Detail = model.Detail;
            rebuilt.SerialNumber = model.SerialNumber;
            return View(rebuilt);
        }

        var result = await _postSales.CreateAsync(
            order.Id,
            userId,
            model.Type,
            model.Reason,
            model.OrderLineId,
            model.Detail,
            model.SerialNumber,
            ct: ct);

        Flash(result.Message, result.Success);

        if (!result.Success)
        {
            var rebuilt = BuildModel(order, model.Type, model.OrderLineId);
            rebuilt.Reason = model.Reason;
            rebuilt.Detail = model.Detail;
            rebuilt.SerialNumber = model.SerialNumber;
            return View(rebuilt);
        }

        return RedirectToAction(nameof(Details), new { id = result.Request!.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        await LoadCartBadgeAsync(ct);

        var userId = CurrentUserId;
        var request = await _postSales.GetAsync(id, ct);
        if (request is null || userId is null || request.CustomerId != userId)
        {
            return NotFound();
        }

        ViewData["Title"] = $"Request {request.Reference}";
        return View(request);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Message(int id, string content, CancellationToken ct)
    {
        var userId = CurrentUserId;
        var request = await _postSales.GetAsync(id, ct);
        if (request is null || userId is null || request.CustomerId != userId)
        {
            return NotFound();
        }

        var result = await _postSales.AddMessageAsync(
            id, userId, CurrentUserName, fromCustomer: true, content, ct);

        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(int id, CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (userId is null)
        {
            return Challenge();
        }

        var result = await _postSales.WithdrawAsync(id, userId, ct);
        Flash(result.Message, result.Success);

        return result is { Success: true, Request: { } request }
            ? RedirectToAction(nameof(Details), new { id = request.Id })
            : RedirectToAction(nameof(Index));
    }

    private static PostSaleCreateViewModel BuildModel(Order order, PostSaleType type, int? lineId)
    {
        var model = new PostSaleCreateViewModel
        {
            OrderId = order.Id,
            OrderNumber = order.OrderNumber,
            Type = type,
            OrderLineId = lineId,
            IsOrderLevel = lineId is null
        };

        if (type == PostSaleType.Cancellation)
        {
            // A cancellation of the whole order is the usual case; item-level cancellation is
            // supported too, so both options are offered.
            model.IsOrderLevel = lineId is null;
        }

        foreach (var line in order.Lines)
        {
            model.Lines.Add(new PostSaleLineOption
            {
                OrderLineId = line.Id,
                ProductName = line.ProductName,
                VariantName = line.VariantName,
                Quantity = line.Quantity,
                LineTotal = line.LineTotal
            });
        }

        return model;
    }
}

/// <summary>Form values for raising a post-sale request.</summary>
public sealed class PostSaleCreateViewModel
{
    public int OrderId { get; set; }

    public string OrderNumber { get; set; } = string.Empty;

    public PostSaleType Type { get; set; }

    /// <summary>Null means the whole order.</summary>
    public int? OrderLineId { get; set; }

    public bool IsOrderLevel { get; set; }

    public PostSaleReason Reason { get; set; } = PostSaleReason.ChangedMind;

    [StringLength(1000)]
    [Display(Name = "Tell us what happened")]
    public string? Detail { get; set; }

    [StringLength(100)]
    [Display(Name = "Serial number (warranty claims)")]
    public string? SerialNumber { get; set; }

    public List<PostSaleLineOption> Lines { get; set; } = new();
}

/// <summary>One selectable order line in the request form.</summary>
public sealed class PostSaleLineOption
{
    public int OrderLineId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? VariantName { get; set; }
    public int Quantity { get; set; }
    public decimal LineTotal { get; set; }
}
