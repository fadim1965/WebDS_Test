using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WebDS_Test.Models;

namespace WebDS_Test.Controllers;

/// <summary>
/// The payment surfaces that are not part of the shopping flow: the provider's webhook, the
/// in-application stand-in for a provider's payment page, and where the shopper lands afterwards.
/// </summary>
/// <remarks>
/// Anonymous on purpose. The webhook is called by the provider, which has no cookie; the simulated
/// payment page is reached by a guest who is paying; and the return page is the tail end of that
/// same guest checkout. Each action authorises what it can on its own terms rather than with an
/// authentication requirement the caller could never satisfy.
/// </remarks>
[AllowAnonymous]
public class PaymentsController : Controller
{
    private readonly IPaymentWebhookHandler _webhooks;
    private readonly IOrderService _orders;
    private readonly IReadOnlyList<IPaymentGateway> _gateways;
    private readonly StoreSettings _settings;
    private readonly ILogger<PaymentsController> _logger;

    public PaymentsController(
        IPaymentWebhookHandler webhooks,
        IOrderService orders,
        IEnumerable<IPaymentGateway> gateways,
        IOptions<StoreSettings> settings,
        ILogger<PaymentsController> logger)
    {
        _webhooks = webhooks;
        _orders = orders;
        _gateways = gateways.ToList();
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Receives a provider delivery.
    /// </summary>
    /// <remarks>
    /// The body is read raw rather than model-bound: a signature covers the exact bytes that were
    /// sent, and reshaping them through the binder would produce a different string to check against.
    /// The provider is a route segment (<c>POST /Payments/Webhook/{provider}</c>) because the default
    /// route only names <c>id</c>, which would leave the parameter unbound.
    /// </remarks>
    [HttpPost("/Payments/Webhook/{provider}")]
    [IgnoreAntiforgeryToken]
    [EnableRateLimiting(RateLimitPolicies.Webhook)]
    public async Task<IActionResult> Webhook(string provider, CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);

        var gateway = Hosted(provider);
        var signature = gateway is null ? null : Request.Headers[gateway.WebhookSignatureHeader].FirstOrDefault();

        var result = await _webhooks.HandleAsync(provider, body, signature, ct);

        return StatusCode(result.StatusCode, new { outcome = result.Outcome, message = result.Message });
    }

    /// <summary>
    /// Starts (or restarts) payment for an order and sends the shopper to the provider. Used both for
    /// the hand-off after checkout and for the "try again" link when a payment is declined.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Pay(int id, CancellationToken ct)
    {
        var order = await _orders.GetOrderAsync(id, null, ct);
        if (order is null || !CanView(order))
        {
            return NotFound();
        }

        if (order.Status == OrderStatus.Pending && order.PaymentMethod != PaymentMethod.CashOnDelivery)
        {
            var gateway = Hosted(SimulatorPaymentGateway.Name) ?? _gateways.OfType<IHostedPaymentGateway>().FirstOrDefault();
            if (gateway is not null)
            {
                var returnUrl = Url.Action(nameof(Return), new { id = order.Id })!;
                var intent = await gateway.CreateIntentAsync(order, returnUrl, ct);
                return Redirect(intent.RedirectUrl);
            }
        }

        return RedirectToAction(nameof(Return), new { id = order.Id });
    }

    /// <summary>Where the provider sends the shopper once they are done on its page.</summary>
    [HttpGet]
    public async Task<IActionResult> Return(int id, CancellationToken ct)
    {
        var order = await _orders.GetOrderAsync(id, null, ct);
        if (order is null || !CanView(order))
        {
            return NotFound();
        }

        ViewData["Title"] = $"Payment for {order.OrderNumber}";
        return View(order);
    }

    /// <summary>
    /// The simulated provider's payment page. A real provider hosts its own; this one stands in for
    /// it so the redirect-and-webhook flow can be run end to end without a network or an account.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Simulator(string reference, string? returnUrl, CancellationToken ct)
    {
        var order = await _orders.GetByNumberAsync(reference, null, ct);
        if (order is null)
        {
            return NotFound();
        }

        ViewData["Title"] = "Secure payment";
        return View(new SimulatorPaymentViewModel
        {
            Order = order,
            CurrencySymbol = _settings.CurrencySymbol,
            ReturnUrl = returnUrl
        });
    }

    /// <summary>
    /// Settles the simulated payment by posting a **signed** webhook into the very handler a real
    /// provider reaches over HTTP. The shopper's click never touches the order directly: it produces
    /// a provider event and lets the webhook path decide what that means.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SimulatorComplete(
        string reference, string outcome, string? returnUrl, CancellationToken ct)
    {
        if (Hosted(SimulatorPaymentGateway.Name) is not SimulatorPaymentGateway simulator)
        {
            return NotFound();
        }

        var order = await _orders.GetByNumberAsync(reference, null, ct);
        if (order is null)
        {
            return NotFound();
        }

        var success = string.Equals(outcome, "succeed", StringComparison.OrdinalIgnoreCase);

        var payload = new SimulatorPaymentGateway.SimulatorEvent(
            EventId: Guid.NewGuid().ToString("N"),
            Type: success ? "payment.succeeded" : "payment.failed",
            OrderNumber: order.OrderNumber,
            TransactionId: success ? $"sim_{Guid.NewGuid():N}" : null,
            Amount: order.GrandTotal,
            Currency: _settings.Currency,
            Success: success,
            FailureReason: success ? null : "The card was declined by the issuer.");

        var body = simulator.Serialize(payload);
        var result = await _webhooks.HandleAsync(simulator.ProviderName, body, simulator.Sign(body), ct);

        _logger.LogInformation(
            "Simulator settled {OrderNumber} as {Outcome}: {Message}",
            order.OrderNumber, result.Outcome, result.Message);

        var target = !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? returnUrl
            : Url.Action(nameof(Return), new { id = order.Id })!;

        return Redirect(target);
    }

    /// <summary>The hosted gateway with this name, or null when the configured provider is not hosted.</summary>
    private IHostedPaymentGateway? Hosted(string provider) =>
        _gateways.OfType<IHostedPaymentGateway>()
            .FirstOrDefault(g => string.Equals(g.ProviderName, provider, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether this browser may see the order. Either it belongs to the signed-in shopper, or it is
    /// one this session placed — which is what lets a guest return from the provider to their order.
    /// </summary>
    private bool CanView(Order order)
    {
        if (order.UserId is not null && order.UserId == CurrentUserId)
        {
            return true;
        }

        var raw = HttpContext.Session.GetString(OrdersController.ViewableOrdersSessionKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var id) ? id : 0)
            .Contains(order.Id);
    }

    private string? CurrentUserId =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
}
