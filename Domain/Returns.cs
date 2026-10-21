using System.ComponentModel.DataAnnotations;

namespace WebDS_Test.Domain;

/// <summary>
/// A shopper's request to send one or more order lines back and be refunded. One request per
/// order at a time; each line records the quantity being returned.
/// </summary>
/// <remarks>
/// <para>
/// The request is deliberately its own aggregate rather than flags on <see cref="OrderLine"/>:
/// a refund has to be explainable as a document ("these lines, these quantities, decided by this
/// person") the same way an order and a payout are. The lifecycle is
/// <c>Requested → Approved → Received → Refunded</c>, with <c>Rejected</c> and customer
/// <c>Cancelled</c> as the exits — transitions are validated in <c>ReturnService</c>.
/// </para>
/// <para>
/// The refund itself moves no money in the demonstration store: a card refund is handed to the
/// configured gateway (the simulator records it), and a cash-on-delivery refund is recorded as a
/// refunded payment row with no transaction reference, because the courier's cash has no electronic
/// trail to reverse. Either way the vendor ledger and the order's refunded total are adjusted here.
/// </para>
/// </remarks>
public class ReturnRequest
{
    public int Id { get; set; }

    public int OrderId { get; set; }
    public Order? Order { get; set; }

    /// <summary>
    /// The shop the order belongs to, copied from it. Denormalised so vendor and admin queries can
    /// scope and filter without joining through the order.
    /// </summary>
    public int ShopId { get; set; }
    public Shop? Shop { get; set; }

    /// <summary>Human-facing reference, e.g. <c>RMA-2026-0001</c>. Unique.</summary>
    [Required, StringLength(32)]
    public string Reference { get; set; } = string.Empty;

    public ReturnStatus Status { get; set; } = ReturnStatus.Requested;

    /// <summary>The signed-in shopper who raised it. Guests cannot request returns.</summary>
    public string? UserId { get; set; }
    public ApplicationUser? User { get; set; }

    /// <summary>Captured from the order so the request stays contactable on its own.</summary>
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    /// <summary>Why the shopper wants to send the items back.</summary>
    [StringLength(1000)]
    public string? CustomerNote { get; set; }

    /// <summary>The shop's or staff's note attached to a decision.</summary>
    [StringLength(1000)]
    public string? DecisionNote { get; set; }

    /// <summary>Who approved, received or refunded it — a vendor or staff identity.</summary>
    [StringLength(256)]
    public string? HandledBy { get; set; }

    /// <summary>Money actually refunded when the request completed. Zero until then.</summary>
    public decimal RefundAmount { get; set; }

    public DateTime RequestedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DecidedUtc { get; set; }
    public DateTime? ReceivedUtc { get; set; }
    public DateTime? RefundedUtc { get; set; }

    public ICollection<ReturnLine> Lines { get; set; } = new List<ReturnLine>();

    /// <summary>True while the request can still be worked on by the shop or the customer.</summary>
    public bool IsOpen => Status is ReturnStatus.Requested or ReturnStatus.Approved or ReturnStatus.Received;
}

/// <summary>One order line (or part of it) being returned, with the price snapshotted from the line.</summary>
public class ReturnLine
{
    public int Id { get; set; }

    public int ReturnRequestId { get; set; }
    public ReturnRequest? ReturnRequest { get; set; }

    public int OrderLineId { get; set; }
    public OrderLine? OrderLine { get; set; }

    /// <summary>Units being returned — at least 1, never more than the line's quantity.</summary>
    public int Quantity { get; set; }

    /// <summary>Copied from the order line when the request is raised; the refund uses this.</summary>
    public decimal UnitPrice { get; set; }

    public decimal LineTotal => Quantity * UnitPrice;
}

/// <summary>Lifecycle of a return request. Transitions are validated in <c>ReturnService</c>.</summary>
public enum ReturnStatus
{
    /// <summary>Waiting for the shop to approve or reject.</summary>
    Requested = 0,
    /// <summary>The shop agreed; waiting for the goods to come back.</summary>
    Approved = 1,
    /// <summary>The shop refused, with a reason.</summary>
    Rejected = 2,
    /// <summary>Goods received and restocked; waiting for the refund.</summary>
    Received = 3,
    /// <summary>Refund issued. Final.</summary>
    Refunded = 4,
    /// <summary>The shopper withdrew the request. Final.</summary>
    Cancelled = 5
}

public static class ReturnStatusExtensions
{
    /// <summary>Human label used by badges on the storefront, vendor area and admin area.</summary>
    public static string ToDisplay(this ReturnStatus status) => status switch
    {
        ReturnStatus.Requested => "Requested",
        ReturnStatus.Approved => "Approved",
        ReturnStatus.Rejected => "Rejected",
        ReturnStatus.Received => "Received",
        ReturnStatus.Refunded => "Refunded",
        ReturnStatus.Cancelled => "Cancelled",
        _ => status.ToString()
    };

    /// <summary>Bootstrap contextual colour for the badge.</summary>
    public static string ToCssClass(this ReturnStatus status) => status switch
    {
        ReturnStatus.Requested => "text-bg-warning",
        ReturnStatus.Approved => "text-bg-info",
        ReturnStatus.Rejected => "text-bg-danger",
        ReturnStatus.Received => "text-bg-primary",
        ReturnStatus.Refunded => "text-bg-success",
        ReturnStatus.Cancelled => "text-bg-secondary",
        _ => "text-bg-secondary"
    };

    /// <summary>Transitions the return service accepts from the current state.</summary>
    public static IReadOnlyList<ReturnStatus> AllowedTransitions(this ReturnStatus status) => status switch
    {
        ReturnStatus.Requested => new[] { ReturnStatus.Approved, ReturnStatus.Rejected, ReturnStatus.Cancelled },
        ReturnStatus.Approved => new[] { ReturnStatus.Received, ReturnStatus.Cancelled },
        ReturnStatus.Received => new[] { ReturnStatus.Refunded },
        _ => Array.Empty<ReturnStatus>()
    };
}
