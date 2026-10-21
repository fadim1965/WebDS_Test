using System.ComponentModel.DataAnnotations;

namespace WebDS_Test.Domain;

/// <summary>Where a transactional email has got to in the outbox.</summary>
public enum EmailStatus
{
    /// <summary>Accepted into the outbox, not yet handed to a transport.</summary>
    Queued = 0,

    /// <summary>The transport accepted it without error.</summary>
    Sent = 1,

    /// <summary>Every attempt failed. Kept for an administrator to inspect or retry.</summary>
    Failed = 2,

    /// <summary>Recorded but deliberately not sent, because mail is switched off.</summary>
    Skipped = 3
}

/// <summary>The reasons the store sends mail. Stored on <see cref="EmailMessage.Kind"/>.</summary>
public static class EmailKind
{
    public const string Welcome = "welcome";
    public const string OrderConfirmation = "order-confirmation";
    public const string OrderShipped = "order-shipped";
    public const string OrderCancelled = "order-cancelled";
    public const string RefundIssued = "refund-issued";
    public const string ReturnDecided = "return-decided";
    public const string ReturnRefunded = "return-refunded";
    public const string PayoutIssued = "payout-issued";
    public const string ShopStatus = "shop-status";
    public const string PasswordReset = "password-reset";
    public const string Test = "test";
}

/// <summary>
/// One message in the transactional email outbox.
/// </summary>
/// <remarks>
/// <para>
/// Nothing sends mail inline. A request that needs to notify somebody writes a row here (in the
/// same <see cref="ApplicationDbContext"/> the request is already using) and returns; a background
/// dispatcher (<c>Services/Email/EmailDispatcher.cs</c>) hands rows to the configured transport and
/// records the outcome. That keeps a slow or unavailable mail server off the request path, and it
/// means every message the store *intended* to send is auditable — including the ones that failed.
/// </para>
/// <para>
/// The recipient is deliberately stored as a plain column rather than a foreign key to
/// <c>Users</c>: an email is a permanent record of what was sent to whom, and deleting a customer
/// must never delete that history. <see cref="UserId"/> and <see cref="OrderId"/> are therefore
/// loose references, indexed but not constrained.
/// </para>
/// </remarks>
public class EmailMessage
{
    public int Id { get; set; }

    [Required, StringLength(256)]
    public string ToAddress { get; set; } = string.Empty;

    [StringLength(120)]
    public string? ToName { get; set; }

    [Required, StringLength(200)]
    public string Subject { get; set; } = string.Empty;

    /// <summary>One of the <see cref="EmailKind"/> constants; what produced this message.</summary>
    [Required, StringLength(60)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>The rendered HTML alternative of the message.</summary>
    [Required]
    public string BodyHtml { get; set; } = string.Empty;

    /// <summary>The plain-text alternative, for clients that will not render HTML.</summary>
    [Required]
    public string BodyText { get; set; } = string.Empty;

    public EmailStatus Status { get; set; } = EmailStatus.Queued;

    /// <summary>How many times a transport has been asked to send this message.</summary>
    public int Attempts { get; set; }

    [StringLength(1000)]
    public string? LastError { get; set; }

    /// <summary>
    /// A stable key that makes a notification idempotent ("order-confirmation:123"). Checked before
    /// a message is queued, so a retried request cannot send the same mail twice.
    /// </summary>
    [StringLength(120)]
    public string? DedupeKey { get; set; }

    /// <summary>Loose reference to the recipient, kept even if the account is later deleted.</summary>
    [StringLength(450)]
    public string? UserId { get; set; }

    /// <summary>Loose reference to the order that triggered the message, when there is one.</summary>
    public int? OrderId { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? SentUtc { get; set; }

    /// <summary>When a failed attempt may be retried; null for a message that has never failed.</summary>
    public DateTime? NextAttemptUtc { get; set; }
}
