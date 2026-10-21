namespace WebDS_Test.Services;

/// <summary>
/// Payment configuration. <see cref="Provider"/> names the gateway the application registers at
/// startup; the secret is what the provider signs its webhook bodies with.
/// </summary>
public sealed class PaymentSettings
{
    public const string SectionName = "Payments";

    /// <summary>
    /// <c>sandbox</c> charges nothing and has no hosted page (the store runs on cash on delivery);
    /// <c>simulator</c> adds the redirect-and-webhook card flow. A production deployment names its
    /// own <c>IHostedPaymentGateway</c> implementation here.
    /// </summary>
    public string Provider { get; set; } = "sandbox";

    /// <summary>
    /// Shared secret for webhook signatures. The build-time default is deliberately obvious: a real
    /// deployment supplies this from a secret store, because anyone who knows it can post events.
    /// </summary>
    public string WebhookSecret { get; set; } = "dev-simulator-webhook-secret";
}
