using System.ComponentModel.DataAnnotations;

namespace WebDS_Test.Domain;

/// <summary>
/// A shopper's conversation with a shop: one thread, many messages, started from the shop's
/// storefront page and answered from the vendor area.
/// </summary>
/// <remarks>
/// <para>
/// The thread is its own aggregate for the reason <see cref="ReturnRequest"/> is: a conversation has
/// to be explainable as a document ("this subject, these messages, this shop, this shopper") on
/// either side, and it must outlive any one message. It deliberately does not live on
/// <see cref="Order"/> — most questions are asked before an order exists — so the order link is
/// optional context when there is one.
/// </para>
/// <para>
/// Read state is a pair of per-side timestamps rather than a flag per message: each side's marker
/// says "everything before this is read", which is exactly what an inbox needs and costs one row
/// instead of one column per message. <see cref="LastAuthor"/> and
/// <see cref="Conversation.LastMessagePreview"/> are denormalised from the newest message so the
/// inbox can render a list without loading the threads' messages.
/// </para>
/// </remarks>
public class Conversation
{
    public int Id { get; set; }

    /// <summary>Human-facing reference, e.g. <c>CONV-2026-0001</c>. Unique.</summary>
    [Required, StringLength(32)]
    public string Reference { get; set; } = string.Empty;

    public int ShopId { get; set; }
    public Shop? Shop { get; set; }

    /// <summary>The order the question is about, when it is about an order at all.</summary>
    public int? OrderId { get; set; }
    public Order? Order { get; set; }

    /// <summary>The signed-in shopper who started it. Guests cannot start conversations.</summary>
    public string? UserId { get; set; }
    public ApplicationUser? User { get; set; }

    /// <summary>
    /// Captured from the account when the thread opens, so the shop's replies can still be mailed
    /// if the account later goes away — the same reasoning as <see cref="ReturnRequest.Email"/>.
    /// </summary>
    [Required, EmailAddress, StringLength(256)]
    public string CustomerEmail { get; set; } = string.Empty;

    [Required, StringLength(200)]
    public string Subject { get; set; } = string.Empty;

    public ConversationStatus Status { get; set; } = ConversationStatus.Open;

    /// <summary>Preview of the newest message, for the inbox list. Denormalised.</summary>
    [StringLength(300)]
    public string LastMessagePreview { get; set; } = string.Empty;

    /// <summary>Who wrote the newest message. Denormalised, drives the unread rule.</summary>
    public ConversationAuthor LastAuthor { get; set; } = ConversationAuthor.Customer;

    public DateTime LastMessageUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedUtc { get; set; }

    /// <summary>When each side last opened the thread; anything newer on the other side is unread.</summary>
    public DateTime? CustomerLastReadUtc { get; set; }
    public DateTime? VendorLastReadUtc { get; set; }

    public ICollection<ConversationMessage> Messages { get; set; } = new List<ConversationMessage>();

    /// <summary>True while either side may still write to the thread.</summary>
    public bool IsOpen => Status == ConversationStatus.Open;
}

/// <summary>One message inside a <see cref="Conversation"/>.</summary>
public class ConversationMessage
{
    public int Id { get; set; }

    public int ConversationId { get; set; }
    public Conversation? Conversation { get; set; }

    public ConversationAuthor Author { get; set; }

    /// <summary>Who wrote it, copied at send time so renames do not rewrite history.</summary>
    [Required, StringLength(120)]
    public string SenderName { get; set; } = string.Empty;

    /// <summary>The account behind it, when there is one.</summary>
    public string? SenderUserId { get; set; }

    [Required, StringLength(4000)]
    public string Body { get; set; } = string.Empty;

    public DateTime SentUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Lifecycle of a conversation. Either side may close; reopening is deliberately not offered.</summary>
public enum ConversationStatus
{
    /// <summary>Accepting messages from both sides.</summary>
    Open = 0,
    /// <summary>Archived by either side. Final for this build.</summary>
    Closed = 1
}

/// <summary>Which side of a conversation a message or an inbox event belongs to.</summary>
public enum ConversationAuthor
{
    /// <summary>The shopper who started the thread.</summary>
    Customer = 0,
    /// <summary>The shop's staff, answering from the vendor area.</summary>
    Vendor = 1,
    /// <summary>An administrator reading or answering on the marketplace's behalf.</summary>
    Admin = 2
}

public static class ConversationExtensions
{
    /// <summary>Human label used by badges on the storefront, vendor area and admin area.</summary>
    public static string ToDisplay(this ConversationStatus status) => status switch
    {
        ConversationStatus.Open => "Open",
        ConversationStatus.Closed => "Closed",
        _ => status.ToString()
    };

    /// <summary>Bootstrap contextual colour for the badge.</summary>
    public static string ToCssClass(this ConversationStatus status) => status switch
    {
        ConversationStatus.Open => "text-bg-success",
        ConversationStatus.Closed => "text-bg-secondary",
        _ => "text-bg-secondary"
    };

    /// <summary>Human label for the message author.</summary>
    public static string ToDisplay(this ConversationAuthor author) => author switch
    {
        ConversationAuthor.Customer => "Shopper",
        ConversationAuthor.Vendor => "Shop",
        ConversationAuthor.Admin => "Marketplace",
        _ => author.ToString()
    };
}
