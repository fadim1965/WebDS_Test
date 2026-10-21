namespace WebDS_Test.Services;

/// <summary>Result returned by a payment provider for a single charge attempt.</summary>
public sealed record PaymentReceipt(
    bool Success,
    string? TransactionId,
    string? FailureReason = null,
    bool IsTest = true);

/// <summary>
/// Where a hosted gateway wants the shopper sent, and the reference it will quote back.
/// </summary>
public sealed record PaymentIntent(string Reference, string RedirectUrl);

/// <summary>
/// A provider event, already parsed out of its wire format. <see cref="Success"/> decides whether the
/// order is captured or left pending; <see cref="EventId"/> is the provider's idempotency key.
/// </summary>
public sealed record PaymentWebhookEvent(
    string EventId,
    string Type,
    string OrderNumber,
    string? TransactionId,
    decimal Amount,
    string Currency,
    bool Success,
    string? FailureReason = null);

/// <summary>
/// Payment provider abstraction. The application never talks to a provider SDK directly, so a
/// real integration (Stripe, Adyen, …) is a matter of adding one implementation and one
/// configuration switch — no changes to <c>OrderService</c> or the checkout flow.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>Provider identifier recorded on the <c>Payment</c> row.</summary>
    string ProviderName { get; }

    /// <summary>True when this provider does not move real money.</summary>
    bool IsSandbox { get; }

    Task<PaymentReceipt> ChargeAsync(Order order, CancellationToken ct = default);

    Task<PaymentReceipt> RefundAsync(string? transactionId, decimal amount, CancellationToken ct = default);
}

/// <summary>
/// A gateway that takes the money on the provider's own page: the store creates an intent, redirects
/// the shopper, and learns the outcome from an asynchronous webhook rather than a return value.
/// </summary>
/// <remarks>
/// The extra members are exactly the ones the webhook endpoint needs and no more: an implementation
/// is responsible for its own signature scheme and for turning its payload into the neutral
/// <see cref="PaymentWebhookEvent"/>, which is what keeps the endpoint itself provider-agnostic.
/// </remarks>
public interface IHostedPaymentGateway : IPaymentGateway
{
    /// <summary>Registers a payment with the provider and returns where to send the shopper.</summary>
    Task<PaymentIntent> CreateIntentAsync(Order order, string returnUrl, CancellationToken ct = default);

    /// <summary>The request header the provider puts its signature in, e.g. <c>X-Signature</c>.</summary>
    string WebhookSignatureHeader { get; }

    /// <summary>True when <paramref name="signature"/> authenticates <paramref name="body"/>.</summary>
    bool VerifySignature(string body, string? signature);

    /// <summary>Parses a delivery, or returns null when it is not a shape this gateway understands.</summary>
    PaymentWebhookEvent? ParseWebhook(string body);
}

/// <summary>
/// Development gateway. Approves every charge, unless the order total is zero or the card
/// placeholder ending in <c>0000</c> is used — which lets the failure path be exercised.
/// </summary>
public sealed class SandboxPaymentGateway : IPaymentGateway
{
    private readonly ILogger<SandboxPaymentGateway> _logger;

    public SandboxPaymentGateway(ILogger<SandboxPaymentGateway> logger) => _logger = logger;

    public string ProviderName => "sandbox";

    public bool IsSandbox => true;

    public Task<PaymentReceipt> ChargeAsync(Order order, CancellationToken ct = default)
    {
        if (order.GrandTotal <= 0m)
        {
            return Task.FromResult(new PaymentReceipt(false, null, "Order total must be greater than zero."));
        }

        // Deterministic sandbox failure hook: any card number ending 0000 is declined.
        if (order.CustomerNote?.Contains("[decline]", StringComparison.OrdinalIgnoreCase) == true)
        {
            _logger.LogInformation("Sandbox declined order {OrderNumber} via [decline] note", order.OrderNumber);
            return Task.FromResult(new PaymentReceipt(false, null, "Card declined by the sandbox gateway."));
        }

        var transactionId = $"sbx_{Guid.NewGuid():N}"[..24];
        _logger.LogInformation("Sandbox approved {Amount} for order {OrderNumber} ({TransactionId})",
            order.GrandTotal, order.OrderNumber, transactionId);

        return Task.FromResult(new PaymentReceipt(true, transactionId));
    }

    public Task<PaymentReceipt> RefundAsync(string? transactionId, decimal amount, CancellationToken ct = default)
    {
        var refundId = $"sbr_{Guid.NewGuid():N}"[..24];
        _logger.LogInformation("Sandbox refunded {Amount} for transaction {TransactionId} ({RefundId})",
            amount, transactionId ?? "(none)", refundId);

        return Task.FromResult(new PaymentReceipt(true, refundId));
    }
}
