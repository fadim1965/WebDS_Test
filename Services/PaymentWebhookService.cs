namespace WebDS_Test.Services;

/// <summary>What the store did with a webhook delivery. The HTTP status is derived from this.</summary>
public sealed record WebhookResult(int StatusCode, string Outcome, string Message);

/// <summary>
/// Applies a payment-provider webhook. Shared by the HTTP endpoint a real provider posts to and by
/// the simulator's payment page, so both go through the same verification and the same order change.
/// </summary>
public interface IPaymentWebhookHandler
{
    Task<WebhookResult> HandleAsync(string provider, string body, string? signature, CancellationToken ct = default);
}

/// <summary>
/// Verifies and applies a provider delivery.
/// </summary>
/// <remarks>
/// The order of operations is the security design. The signature is checked before anything in the
/// body is read, because a webhook endpoint is a public, unauthenticated URL and the payload is
/// attacker-controlled until the signature proves otherwise. The event is then recorded with its
/// provider id, and that unique key is what makes the provider's retries idempotent rather than a
/// stream of duplicate captures.
/// </remarks>
public sealed class PaymentWebhookHandler : IPaymentWebhookHandler
{
    private readonly ApplicationDbContext _db;
    private readonly IReadOnlyDictionary<string, IHostedPaymentGateway> _gateways;
    private readonly IOrderService _orders;
    private readonly ILogger<PaymentWebhookHandler> _logger;

    public PaymentWebhookHandler(
        ApplicationDbContext db,
        IEnumerable<IPaymentGateway> gateways,
        IOrderService orders,
        ILogger<PaymentWebhookHandler> logger)
    {
        _db = db;

        // Only the gateways that can report asynchronously take part; the sandbox provider has no
        // webhook and is simply not addressable here.
        _gateways = gateways
            .OfType<IHostedPaymentGateway>()
            .GroupBy(g => g.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        _orders = orders;
        _logger = logger;
    }

    public async Task<WebhookResult> HandleAsync(
        string provider, string body, string? signature, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider) || !_gateways.TryGetValue(provider, out var gateway))
        {
            return new WebhookResult(
                StatusCodes.Status404NotFound, "unknown-provider", $"No hosted gateway named '{provider}'.");
        }

        // Signature first. Nothing in an unsigned body is trusted — not even the event id used to
        // de-duplicate it.
        if (!gateway.VerifySignature(body, signature))
        {
            await RecordAsync(
                provider,
                eventId: InvalidEventKey(body),
                type: "unverified",
                orderNumber: null,
                orderId: null,
                transactionId: null,
                amount: 0m,
                currency: "USD",
                body: body,
                signatureValid: false,
                outcome: "rejected",
                note: "Signature verification failed.",
                ct: ct);

            _logger.LogWarning("Rejected a {Provider} webhook whose signature did not verify", provider);

            return new WebhookResult(StatusCodes.Status401Unauthorized, "rejected", "Invalid signature.");
        }

        var evt = gateway.ParseWebhook(body);
        if (evt is null)
        {
            await RecordAsync(
                provider,
                eventId: InvalidEventKey(body),
                type: "unparseable",
                orderNumber: null,
                orderId: null,
                transactionId: null,
                amount: 0m,
                currency: "USD",
                body: body,
                signatureValid: true,
                outcome: "rejected",
                note: "The body could not be parsed.",
                ct: ct);

            return new WebhookResult(StatusCodes.Status400BadRequest, "rejected", "Unrecognised payload.");
        }

        // Providers retry until they see a 2xx, so the same event will arrive more than once.
        if (await _db.PaymentEvents.AnyAsync(e => e.Provider == provider && e.EventId == evt.EventId, ct))
        {
            _logger.LogInformation("Ignoring replayed {Provider} event {EventId}", provider, evt.EventId);
            return new WebhookResult(StatusCodes.Status200OK, "duplicate", "Already processed.");
        }

        var order = await _db.Orders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.OrderNumber == evt.OrderNumber, ct);

        string outcome;
        string note;

        if (order is null)
        {
            // Acknowledge anyway: a permanent 4xx would only make the provider retry a delivery that
            // can never succeed. The row is the record that something arrived for an unknown order.
            outcome = "order-not-found";
            note = $"No order matches '{evt.OrderNumber}'.";
        }
        else if (evt.Success)
        {
            var applied = await _orders.MarkPaidAsync(order.Id, provider, evt.TransactionId, evt.Amount, ct);
            outcome = applied.Success ? "paid" : "rejected";
            note = applied.Message;
        }
        else
        {
            var applied = await _orders.MarkPaymentFailedAsync(order.Id, provider, evt.FailureReason, ct);
            outcome = "payment-failed";
            note = applied.Message;
        }

        await RecordAsync(
            provider,
            eventId: evt.EventId,
            type: evt.Type,
            orderNumber: evt.OrderNumber,
            orderId: order?.Id,
            transactionId: evt.TransactionId,
            amount: evt.Amount,
            currency: evt.Currency,
            body: body,
            signatureValid: true,
            outcome: outcome,
            note: note,
            ct: ct);

        return new WebhookResult(StatusCodes.Status200OK, outcome, note);
    }

    /// <summary>
    /// Records the delivery. The unique index on (provider, event) is what actually enforces
    /// idempotency, so a concurrent duplicate insert is caught and ignored rather than treated as an
    /// error — the two applications are both idempotent, and a 2xx is the right answer either way.
    /// </summary>
    private async Task RecordAsync(
        string provider,
        string eventId,
        string type,
        string? orderNumber,
        int? orderId,
        string? transactionId,
        decimal amount,
        string currency,
        string body,
        bool signatureValid,
        string outcome,
        string? note,
        CancellationToken ct)
    {
        _db.PaymentEvents.Add(new PaymentEvent
        {
            Provider = Truncate(provider, 40)!,
            EventId = Truncate(eventId, 120)!,
            Type = Truncate(type, 60)!,
            OrderNumber = Truncate(orderNumber, 32),
            OrderId = orderId,
            TransactionId = Truncate(transactionId, 120),
            Amount = amount,
            Currency = Truncate(string.IsNullOrWhiteSpace(currency) ? "USD" : currency, 8)!,
            Payload = body,
            SignatureValid = signatureValid,
            Outcome = Truncate(outcome, 40)!,
            Note = Truncate(note, 500),
            CreatedUtc = DateTime.UtcNow
        });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            _logger.LogInformation(
                "A concurrent {Provider} delivery already recorded event {EventId}", provider, eventId);
        }
    }

    /// <summary>
    /// A stable key for a body that could not be trusted or parsed. Hashing rather than storing the
    /// body as the key keeps the unique index bounded and the raw text out of an indexed column.
    /// </summary>
    private static string InvalidEventKey(string body)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body));
        return "unverified:" + Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
