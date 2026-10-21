using System.ComponentModel.DataAnnotations;

namespace WebDS_Test.Domain;

/// <summary>
/// One webhook delivery received from a payment provider, recorded whether or not it was acted on.
/// </summary>
/// <remarks>
/// <para>
/// The provider is the one caller the store cannot authenticate with a cookie, so this table is the
/// audit trail for everything it said: the raw body, whether the signature checked out, what the
/// event was mapped to, and how it was applied. When a shopper insists they paid and the order is
/// still pending, the answer is on this row rather than in a log that has since rolled over.
/// </para>
/// <para>
/// <see cref="EventId"/> is the idempotency key. Providers retry a delivery until they get a 2xx, so
/// the same event will arrive more than once; the unique index on
/// (<see cref="Provider"/>, <see cref="EventId"/>) makes a replay a no-op rather than a second
/// payment application.
/// </para>
/// </remarks>
public class PaymentEvent
{
    public int Id { get; set; }

    /// <summary>The gateway that received it, e.g. <c>simulator</c>.</summary>
    [Required, StringLength(40)]
    public string Provider { get; set; } = string.Empty;

    /// <summary>Provider-issued event identifier. Unique per provider.</summary>
    [Required, StringLength(120)]
    public string EventId { get; set; } = string.Empty;

    /// <summary>Provider event type, e.g. <c>payment.succeeded</c>.</summary>
    [Required, StringLength(60)]
    public string Type { get; set; } = string.Empty;

    /// <summary>Resolved order, when the payload named one the store knows.</summary>
    public int? OrderId { get; set; }
    public Order? Order { get; set; }

    /// <summary>The order reference as it arrived, kept even when no order matched it.</summary>
    [StringLength(32)]
    public string? OrderNumber { get; set; }

    [StringLength(120)]
    public string? TransactionId { get; set; }

    public decimal Amount { get; set; }

    [StringLength(8)]
    public string Currency { get; set; } = "USD";

    /// <summary>The body exactly as it was delivered, so a disputed event can be replayed by hand.</summary>
    [Required]
    public string Payload { get; set; } = string.Empty;

    /// <summary>False when the delivery failed signature verification and was refused.</summary>
    public bool SignatureValid { get; set; }

    /// <summary>
    /// What happened: <c>paid</c>, <c>payment-failed</c>, <c>already-paid</c>, <c>duplicate</c>,
    /// <c>order-not-found</c> or <c>rejected</c>.
    /// </summary>
    [Required, StringLength(40)]
    public string Outcome { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Note { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
