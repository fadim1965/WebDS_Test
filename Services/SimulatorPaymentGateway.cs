using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WebDS_Test.Services;

/// <summary>
/// A complete hosted-payment gateway that runs inside the application: it creates a payment intent,
/// sends the shopper to a payment page, and reports the result back over the same signed-webhook
/// contract a real provider uses.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the rest of the card flow — the redirect, the signed delivery, the
/// idempotency, the order state change — is exactly the same whatever provider is behind it, and
/// none of it can be exercised offline against a real processor. The simulator is both sides of that
/// exchange: it signs a body as the provider would and the webhook endpoint verifies it as it would
/// for anyone else. A production deployment swaps in an implementation of
/// <see cref="IHostedPaymentGateway"/> that talks to Stripe or Adyen; nothing else changes.
/// </para>
/// <para>
/// It moves no money: <see cref="IsSandbox"/> is true, and every payment it records is marked as a
/// test so revenue reporting can exclude it.
/// </para>
/// </remarks>
public sealed class SimulatorPaymentGateway : IHostedPaymentGateway
{
    /// <summary>The header a real provider would sign into; the simulator writes and reads the same one.</summary>
    public const string SignatureHeader = "X-Payment-Signature";

    /// <summary>The provider identifier, as a constant so callers can name it without an instance.</summary>
    public const string Name = "simulator";

    /// <summary>Serialisation shared by the simulator's writer and reader, and by its tests.</summary>
    public static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private readonly PaymentSettings _settings;
    private readonly ILogger<SimulatorPaymentGateway> _logger;

    public SimulatorPaymentGateway(IOptions<PaymentSettings> settings, ILogger<SimulatorPaymentGateway> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public string ProviderName => Name;

    public bool IsSandbox => true;

    public string WebhookSignatureHeader => SignatureHeader;

    public Task<PaymentIntent> CreateIntentAsync(Order order, string returnUrl, CancellationToken ct = default)
    {
        var url = $"/Payments/Simulator?reference={Uri.EscapeDataString(order.OrderNumber)}" +
                  $"&returnUrl={Uri.EscapeDataString(returnUrl)}";

        _logger.LogInformation("Simulator payment intent created for order {OrderNumber}", order.OrderNumber);

        return Task.FromResult(new PaymentIntent(order.OrderNumber, url));
    }

    /// <summary>
    /// Signs a body the way the provider would. Only the simulated provider needs this — a real one
    /// signs on its own side — but keeping it beside the verifier is what lets the two be tested
    /// against each other.
    /// </summary>
    public string Sign(string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_settings.WebhookSecret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    /// <summary>
    /// Constant-time comparison of the delivered signature against the one computed over the body.
    /// A length or content difference is a failure; there is no partial credit.
    /// </summary>
    public bool VerifySignature(string body, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Sign(body));
        var offered = Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant());

        // FixedTimeEquals needs equal lengths; a mismatch is a failed check, but the comparison of
        // equal-length inputs is constant-time so a guess cannot be narrowed down byte by byte.
        return expected.Length == offered.Length &&
               CryptographicOperations.FixedTimeEquals(expected, offered);
    }

    public PaymentWebhookEvent? ParseWebhook(string body)
    {
        SimulatorEvent? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<SimulatorEvent>(body, Wire);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is null ||
            string.IsNullOrWhiteSpace(parsed.EventId) ||
            string.IsNullOrWhiteSpace(parsed.OrderNumber))
        {
            return null;
        }

        return new PaymentWebhookEvent(
            parsed.EventId,
            string.IsNullOrWhiteSpace(parsed.Type) ? "payment.unknown" : parsed.Type,
            parsed.OrderNumber,
            parsed.TransactionId,
            parsed.Amount,
            string.IsNullOrWhiteSpace(parsed.Currency) ? "USD" : parsed.Currency,
            parsed.Success,
            parsed.FailureReason);
    }

    public string Serialize(SimulatorEvent payload) => JsonSerializer.Serialize(payload, Wire);

    public Task<PaymentReceipt> ChargeAsync(Order order, CancellationToken ct = default) =>
        Task.FromResult(new PaymentReceipt(true, $"sim_{Guid.NewGuid():N}", IsTest: true));

    public Task<PaymentReceipt> RefundAsync(string? transactionId, decimal amount, CancellationToken ct = default) =>
        Task.FromResult(new PaymentReceipt(true, $"simr_{Guid.NewGuid():N}", IsTest: true));

    /// <summary>The wire shape the simulator posts — deliberately the same shape a real provider sends.</summary>
    public sealed record SimulatorEvent(
        string EventId,
        string Type,
        string OrderNumber,
        string? TransactionId,
        decimal Amount,
        string Currency,
        bool Success,
        string? FailureReason = null);
}
