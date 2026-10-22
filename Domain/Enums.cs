namespace WebDS_Test.Domain;

/// <summary>
/// Where a vendor's listing sits in the marketplace's review process.
/// </summary>
/// <remarks>
/// <para>
/// A vendor may not put a listing on sale by itself: the listing is submitted, an administrator
/// approves or turns it down, and only an approved listing is visible to shoppers. The state a
/// product had before moderation existed was "published", so that is the zero value — a store that
/// predates this feature keeps every listing on sale, and a newly created listing has to be
/// submitted.
/// </para>
/// <para>
/// The visible-on-the-storeshop flag on the product and this state are kept in step by
/// <c>ListingModerationService</c>: a listing is live exactly when it is published.
/// </para>
/// </remarks>
public enum ListingStatus
{
    /// <summary>Live in the catalogue. The state every listing had before moderation existed.</summary>
    Published = 0,

    /// <summary>The vendor is still working on it. Not submitted, not visible.</summary>
    Draft = 1,

    /// <summary>Submitted for review. Waiting for an administrator. Not visible.</summary>
    PendingReview = 2,

    /// <summary>Turned down. The vendor sees the reason and may fix it and submit again.</summary>
    Rejected = 3
}

public static class ListingStatusExtensions
{
    /// <summary>Human label used by badges in the vendor and admin areas.</summary>
    public static string ToDisplay(this ListingStatus status) => status switch
    {
        ListingStatus.Published => "Published",
        ListingStatus.Draft => "Draft",
        ListingStatus.PendingReview => "Awaiting review",
        ListingStatus.Rejected => "Rejected",
        _ => status.ToString()
    };

    /// <summary>Bootstrap contextual colour for the label above.</summary>
    public static string ToCssClass(this ListingStatus status) => status switch
    {
        ListingStatus.Published => "text-bg-success",
        ListingStatus.Draft => "text-bg-secondary",
        ListingStatus.PendingReview => "text-bg-warning",
        ListingStatus.Rejected => "text-bg-danger",
        _ => "text-bg-secondary"
    };

    /// <summary>
    /// True while the listing is waiting on an administrator, so the vendor can be told it is in the
    /// queue and cannot be edited into a sale in the meantime.
    /// </summary>
    public static bool AwaitingReview(this ListingStatus status) =>
        status == ListingStatus.PendingReview;
}

/// <summary>Lifecycle of an order. Transitions are validated in <c>OrderService</c>.</summary>
public enum OrderStatus
{
    Pending = 0,
    Paid = 1,
    Shipped = 2,
    Delivered = 3,
    Cancelled = 4,
    Refunded = 5
}

/// <summary>Outcome of a payment attempt recorded against an order.</summary>
public enum PaymentStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
    Refunded = 3
}

/// <summary>Why a stock level changed. Keeps the inventory ledger auditable.</summary>
public enum StockMovementReason
{
    InitialStock = 0,
    Restock = 1,
    Sale = 2,
    Return = 3,
    Adjustment = 4,
    Cancellation = 5
}

/// <summary>How the shopper pays for an order.</summary>
public enum PaymentMethod
{
    /// <summary>Cash on delivery: the carrier collects, and the marketplace settles with the vendor.</summary>
    CashOnDelivery = 0,
    /// <summary>Paid online before dispatch.</summary>
    Card = 1,
    /// <summary>Settled at a LibanPost counter before dispatch.</summary>
    CounterPayment = 2
}

/// <summary>Progress of a cash-on-delivery collection.</summary>
public enum CollectionStatus
{
    /// <summary>Nothing to collect — a prepaid order.</summary>
    NotApplicable = 0,
    AwaitingCollection = 1,
    Collected = 2,
    CollectionFailed = 3,
    /// <summary>Money returned to the shopper after a failed delivery.</summary>
    Refunded = 4
}

/// <summary>State of a consignment handed to a carrier.</summary>
public enum ShipmentStatus
{
    /// <summary>Consignment drafted locally, not yet registered with the carrier.</summary>
    Draft = 0,
    /// <summary>Carrier has accepted the consignment and issued a tracking number.</summary>
    Created = 1,
    PickedUp = 2,
    InTransit = 3,
    OutForDelivery = 4,
    Delivered = 5,
    Failed = 6,
    Returned = 7,
    Cancelled = 8
}

public enum DiscountType
{
    Percentage = 0,
    FixedAmount = 1
}

public static class OrderStatusExtensions
{
    /// <summary>Human label used by badges in the storefront and admin area.</summary>
    public static string ToDisplay(this OrderStatus status) => status switch
    {
        OrderStatus.Pending => "Pending payment",
        OrderStatus.Paid => "Paid",
        OrderStatus.Shipped => "Shipped",
        OrderStatus.Delivered => "Delivered",
        OrderStatus.Cancelled => "Cancelled",
        OrderStatus.Refunded => "Refunded",
        _ => status.ToString()
    };

    /// <summary>Bootstrap contextual colour for the label above.</summary>
    public static string ToCssClass(this OrderStatus status) => status switch
    {
        OrderStatus.Pending => "text-bg-warning",
        OrderStatus.Paid => "text-bg-info",
        OrderStatus.Shipped => "text-bg-primary",
        OrderStatus.Delivered => "text-bg-success",
        OrderStatus.Cancelled => "text-bg-secondary",
        OrderStatus.Refunded => "text-bg-danger",
        _ => "text-bg-secondary"
    };

    /// <summary>Statuses an administrator may move an order to from its current status.</summary>
    public static IReadOnlyList<OrderStatus> AllowedTransitions(this OrderStatus status) => status switch
    {
        OrderStatus.Pending => new[] { OrderStatus.Paid, OrderStatus.Cancelled },
        OrderStatus.Paid => new[] { OrderStatus.Shipped, OrderStatus.Refunded, OrderStatus.Cancelled },
        OrderStatus.Shipped => new[] { OrderStatus.Delivered, OrderStatus.Refunded },
        OrderStatus.Delivered => new[] { OrderStatus.Refunded },
        _ => Array.Empty<OrderStatus>()
    };

    /// <summary>True while stock is still committed to the order and should be returned on cancel.</summary>
    public static bool HoldsStock(this OrderStatus status) =>
        status is OrderStatus.Pending or OrderStatus.Paid or OrderStatus.Shipped or OrderStatus.Delivered;
}
