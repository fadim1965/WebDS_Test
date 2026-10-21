namespace WebDS_Test.Services;

/// <summary>
/// Throttling for the endpoints worth protecting — sign-in, checkout, review posting, messaging a
/// shop and the payment webhook. Bound from the <c>RateLimiting</c> configuration section so the
/// limits can be tuned without a deployment.
/// </summary>
public sealed class RateLimitSettings
{
    public const string SectionName = "RateLimiting";

    /// <summary>When false the limiter is left out of the pipeline entirely.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Sign-in, registration and password reset — the brute-force surfaces.</summary>
    public RateLimitRule Auth { get; set; } = new() { PermitLimit = 30, WindowSeconds = 60 };

    /// <summary>Placing an order.</summary>
    public RateLimitRule Checkout { get; set; } = new() { PermitLimit = 30, WindowSeconds = 60 };

    /// <summary>Posting a review or a review vote.</summary>
    public RateLimitRule Review { get; set; } = new() { PermitLimit = 30, WindowSeconds = 60 };

    /// <summary>Starting a conversation with a shop and replying to one.</summary>
    public RateLimitRule Messaging { get; set; } = new() { PermitLimit = 20, WindowSeconds = 60 };

    /// <summary>
    /// Payment-provider webhooks. Generous, because a provider retrying a delivery must not be
    /// throttled into giving up; the signature, not the rate limit, is what protects this endpoint.
    /// </summary>
    public RateLimitRule Webhook { get; set; } = new() { PermitLimit = 120, WindowSeconds = 60 };
}

/// <summary>One fixed-window rule: at most <see cref="PermitLimit"/> requests per window.</summary>
public sealed class RateLimitRule
{
    public int PermitLimit { get; set; } = 30;

    public int WindowSeconds { get; set; } = 60;
}

/// <summary>The names the controller attributes and the registration must agree on.</summary>
public static class RateLimitPolicies
{
    public const string Auth = "auth";
    public const string Checkout = "checkout";
    public const string Review = "review";
    public const string Messaging = "messaging";
    public const string Webhook = "webhook";
}
