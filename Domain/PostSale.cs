using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WebDS_Test.Domain;

/// <summary>Reason a customer initiates a post-sale action.</summary>
public enum PostSaleReason
{
    /// <summary>Customer changed their mind.</summary>
    ChangedMind = 1,

    /// <summary>Item does not match description/expectations.</summary>
    Unsatisfied = 2,

    /// <summary>Wrong item shipped.</summary>
    WrongItem = 3,

    /// <summary>Item arrived damaged.</summary>
    Damaged = 4,

    /// <summary>Item defective under warranty.</summary>
    Defective = 5,

    /// <summary>Customer unreachable (RTS).</summary>
    Unreachable = 6,

    /// <summary>Incorrect option selected (specs/size/colour).</summary>
    IncorrectOption = 7,

    /// <summary>Damaged due to shipper handling.</summary>
    ShipperDamage = 8
}

/// <summary>Type of post-sale request.</summary>
public enum PostSaleType
{
    Cancellation = 1,
    Return = 2,
    Exchange = 3,
    RTS = 4,
    Warranty = 5
}

/// <summary>Current state of a post-sale request.</summary>
public enum PostSaleStatus
{
    /// <summary>Submitted by customer, awaiting review.</summary>
    Pending = 1,

    /// <summary>Approved by admin/operator.</summary>
    Approved = 2,

    /// <summary>Rejected by admin/operator.</summary>
    Rejected = 3,

    /// <summary>Pickup scheduled with shipper.</summary>
    PickupScheduled = 4,

    /// <summary>Item picked up from customer.</summary>
    PickedUp = 5,

    /// <summary>Item received at warehouse/distribution.</summary>
    Received = 6,

    /// <summary>Refund/replacement processed.</summary>
    Processed = 7,

    /// <summary>Exchange item shipped to customer.</summary>
    ExchangeShipped = 8,

    /// <summary>Request closed (resolved or expired).</summary>
    Closed = 9,

    /// <summary>RTS: returned to seller.</summary>
    ReturnedToSeller = 10,

    /// <summary>RTS: redelivered to customer.</summary>
    Redelivered = 11
}

/// <summary>Who bears the cost for a specific fee type.</summary>
public enum FeeBearer
{
    Customer = 1,
    Seller = 2,
    Shipper = 3,
    NotApplicable = 4,
    LibanPost = 5
}

/// <summary>
/// The fee split a post-sale action is decided under: for each cost in the transaction, who pays.
/// </summary>
/// <remarks>
/// Stored with the request rather than recomputed on read, because the rules (and the parties'
/// agreements) can change: a request must remain explainable with the split it was decided under.
/// </remarks>
public class FeeAllocation
{
    public FeeBearer ItemPriceBearer { get; set; } = FeeBearer.NotApplicable;
    public FeeBearer ShippingFeeBearer { get; set; } = FeeBearer.NotApplicable;
    public FeeBearer CreditCardFeeBearer { get; set; } = FeeBearer.NotApplicable;
    public FeeBearer PickupFeeBearer { get; set; } = FeeBearer.NotApplicable;
    public FeeBearer ReturnFeeBearer { get; set; } = FeeBearer.NotApplicable;
    public FeeBearer ReplacementFeeBearer { get; set; } = FeeBearer.NotApplicable;

    /// <summary>True when the customer carries every cost — i.e. only a promo code is offered.</summary>
    public bool IsPromoCodeOnly => ItemPriceBearer == FeeBearer.Customer &&
                                    ShippingFeeBearer == FeeBearer.Customer &&
                                    CreditCardFeeBearer == FeeBearer.Customer &&
                                    PickupFeeBearer == FeeBearer.Customer &&
                                    ReturnFeeBearer == FeeBearer.Customer &&
                                    ReplacementFeeBearer == FeeBearer.Customer;

    public FeeAllocation Clone() => (FeeAllocation)MemberwiseClone();
}

/// <summary>
/// The Kwikby / LibanPost post-sale rules: for each process and scenario, who pays what.
/// </summary>
/// <remarks>
/// This type is deliberately pure: no database, no clock, no services. The rules are the part of
/// the platform the business owns, so they are unit-testable in isolation and cannot drift into a
/// controller. The process numbers in the comments refer to
/// <c>Kwikby-processes business rules.pdf</c>.
/// </remarks>
public static class PostSaleRules
{
    /// <summary>
    /// Cancellation (processes 1–3). Before packaging everything comes back including card fees;
    /// after packaging the card fee is retained; once a tracking number exists the request is
    /// declined and nothing is refunded.
    /// </summary>
    public static FeeAllocation GetCancellationAllocation(bool beforePackaging, bool afterTrackingNumber)
    {
        var allocation = new FeeAllocation
        {
            PickupFeeBearer = FeeBearer.NotApplicable,
            ReturnFeeBearer = FeeBearer.NotApplicable,
            ReplacementFeeBearer = FeeBearer.NotApplicable
        };

        if (afterTrackingNumber)
        {
            // Process 3: after the TR# the request is declined — no refund at all.
            allocation.ItemPriceBearer = FeeBearer.Customer;
            allocation.ShippingFeeBearer = FeeBearer.Customer;
            allocation.CreditCardFeeBearer = FeeBearer.Customer;
            return allocation;
        }

        allocation.ItemPriceBearer = FeeBearer.Seller;
        allocation.ShippingFeeBearer = FeeBearer.Seller;
        // Process 1 refunds the card fee; process 2 (already packed) does not.
        allocation.CreditCardFeeBearer = beforePackaging ? FeeBearer.Seller : FeeBearer.NotApplicable;
        return allocation;
    }

    /// <summary>
    /// Return (process 4): the reason decides who carries pickup, replacement and card costs.
    /// </summary>
    public static FeeAllocation GetReturnAllocation(PostSaleReason reason, bool isOrderLevel)
    {
        var allocation = new FeeAllocation();

        // 4-A: seller's fault — the seller carries everything, at order and item level alike.
        if (reason is PostSaleReason.Unsatisfied or PostSaleReason.WrongItem or PostSaleReason.Damaged)
        {
            allocation.ItemPriceBearer = FeeBearer.Seller;
            allocation.ShippingFeeBearer = FeeBearer.NotApplicable;
            allocation.CreditCardFeeBearer = isOrderLevel ? FeeBearer.Seller : FeeBearer.NotApplicable;
            allocation.PickupFeeBearer = FeeBearer.Seller;
            allocation.ReturnFeeBearer = FeeBearer.NotApplicable;
            allocation.ReplacementFeeBearer = FeeBearer.Seller;
            return allocation;
        }

        // 4-B: changed mind — the customer carries pickup and replacement; goods come back as a
        // promo code rather than cash (the local return policy).
        if (reason is PostSaleReason.ChangedMind or PostSaleReason.IncorrectOption)
        {
            allocation.ItemPriceBearer = FeeBearer.Customer;
            allocation.ShippingFeeBearer = FeeBearer.NotApplicable;
            allocation.CreditCardFeeBearer = FeeBearer.Customer;
            allocation.PickupFeeBearer = FeeBearer.Customer;
            allocation.ReturnFeeBearer = FeeBearer.NotApplicable;
            allocation.ReplacementFeeBearer = FeeBearer.Customer;
            return allocation;
        }

        // 4-C: shipper's fault — the shipper carries everything.
        if (reason == PostSaleReason.ShipperDamage)
        {
            allocation.ItemPriceBearer = FeeBearer.Shipper;
            allocation.ShippingFeeBearer = FeeBearer.NotApplicable;
            allocation.CreditCardFeeBearer = isOrderLevel ? FeeBearer.Shipper : FeeBearer.NotApplicable;
            allocation.PickupFeeBearer = FeeBearer.Shipper;
            allocation.ReturnFeeBearer = FeeBearer.NotApplicable;
            allocation.ReplacementFeeBearer = FeeBearer.Shipper;
            return allocation;
        }

        // Anything else is treated as the customer's own choice.
        allocation.ItemPriceBearer = FeeBearer.Customer;
        allocation.ShippingFeeBearer = FeeBearer.Customer;
        allocation.CreditCardFeeBearer = FeeBearer.Customer;
        allocation.PickupFeeBearer = FeeBearer.Customer;
        allocation.ReturnFeeBearer = FeeBearer.Customer;
        allocation.ReplacementFeeBearer = FeeBearer.Customer;
        return allocation;
    }

    /// <summary>
    /// Exchange (process 6). Before dispatch there is no cost to anyone; once delivered the
    /// exchange falls back to the return rules (6-C).
    /// </summary>
    public static FeeAllocation GetExchangeAllocation(PostSaleReason reason, bool afterDelivery) =>
        afterDelivery
            ? GetReturnAllocation(PostSaleReason.IncorrectOption, isOrderLevel: false)
            : new FeeAllocation
            {
                ItemPriceBearer = FeeBearer.NotApplicable,
                ShippingFeeBearer = FeeBearer.NotApplicable,
                CreditCardFeeBearer = FeeBearer.NotApplicable,
                PickupFeeBearer = FeeBearer.NotApplicable,
                ReturnFeeBearer = FeeBearer.NotApplicable,
                ReplacementFeeBearer = FeeBearer.NotApplicable
            };

    /// <summary>
    /// RTS (process 5). The customer is unreachable, so the goods are refunded but the delivery
    /// effort is not: shipping stays with the customer.
    /// </summary>
    public static FeeAllocation GetRtsAllocation() => new()
    {
        ItemPriceBearer = FeeBearer.Customer,
        ShippingFeeBearer = FeeBearer.Customer,
        CreditCardFeeBearer = FeeBearer.NotApplicable,
        PickupFeeBearer = FeeBearer.NotApplicable,
        ReturnFeeBearer = FeeBearer.NotApplicable,
        ReplacementFeeBearer = FeeBearer.NotApplicable
    };

    /// <summary>
    /// Defect under warranty (process 7/7-A). Inside the three-day grace period the sale is undone;
    /// outside it the item is repaired or replaced at the seller's cost, or refunded if neither is
    /// possible.
    /// </summary>
    public static FeeAllocation GetWarrantyAllocation(bool withinGracePeriod, bool canBeRepaired)
    {
        var allocation = new FeeAllocation
        {
            ShippingFeeBearer = FeeBearer.NotApplicable,
            ReturnFeeBearer = FeeBearer.NotApplicable
        };

        if (withinGracePeriod || !canBeRepaired)
        {
            allocation.ItemPriceBearer = FeeBearer.Seller;
            allocation.CreditCardFeeBearer = FeeBearer.Seller;
            allocation.PickupFeeBearer = FeeBearer.Seller;
            allocation.ReplacementFeeBearer = FeeBearer.Seller;
            return allocation;
        }

        // Repaired or replaced within warranty: the seller pays for the work, not the goods.
        allocation.ItemPriceBearer = FeeBearer.NotApplicable;
        allocation.CreditCardFeeBearer = FeeBearer.NotApplicable;
        allocation.PickupFeeBearer = FeeBearer.Seller;
        allocation.ReplacementFeeBearer = FeeBearer.Seller;
        return allocation;
    }

    /// <summary>Applies the rule set for a request type in one call.</summary>
    public static FeeAllocation GetAllocation(
        PostSaleType type,
        PostSaleReason reason,
        bool isOrderLevel,
        bool beforePackaging,
        bool afterTrackingNumber,
        bool afterDelivery,
        bool withinGracePeriod = false,
        bool canBeRepaired = false) => type switch
    {
        PostSaleType.Cancellation => GetCancellationAllocation(beforePackaging, afterTrackingNumber),
        PostSaleType.Return => GetReturnAllocation(reason, isOrderLevel),
        PostSaleType.Exchange => GetExchangeAllocation(reason, afterDelivery),
        PostSaleType.RTS => GetRtsAllocation(),
        PostSaleType.Warranty => GetWarrantyAllocation(withinGracePeriod, canBeRepaired),
        _ => new FeeAllocation()
    };
}

/// <summary>
/// A post-sale request: a cancellation, return, exchange, RTS or warranty claim against an order.
/// </summary>
/// <remarks>
/// <para>
/// All five Kwikby processes share the same document shape — a reference, an order (and optionally
/// one line), a reason, a fee split, a status timeline and a message thread — so they share one
/// aggregate rather than five near-identical tables. The type-specific data (serial number, RTS
/// manifest, replacement variant) sits on the same row and is null for the types that do not use it.
/// </para>
/// <para>
/// The lifecycle is <c>Pending → Approved → PickupScheduled → PickedUp → Received → Processed →
/// Closed</c>, with <c>Rejected</c> and the customer's own withdrawal as exits. Transitions are
/// validated by <c>PostSaleService</c>; <see cref="PostSaleStatusExtensions.AllowedTransitions"/>
/// is the single table both the UI and the service read.
/// </para>
/// </remarks>
public class PostSaleRequest
{
    public int Id { get; set; }

    /// <summary>Human-facing reference, e.g. <c>PSR-2026-0001</c>. Unique.</summary>
    [Required, StringLength(32)]
    public string Reference { get; set; } = string.Empty;

    public int OrderId { get; set; }
    public Order? Order { get; set; }

    /// <summary>Set for item-level requests; null for order-level ones.</summary>
    public int? OrderLineId { get; set; }
    public OrderLine? OrderLine { get; set; }

    /// <summary>Copied from the order so the queue can be scoped without a join.</summary>
    public int ShopId { get; set; }
    public Shop? Shop { get; set; }

    /// <summary>The account that raised it. Guests cannot raise post-sale requests.</summary>
    public string CustomerId { get; set; } = string.Empty;
    public ApplicationUser? Customer { get; set; }

    public PostSaleType Type { get; set; }
    public PostSaleReason Reason { get; set; }

    [StringLength(1000)]
    public string? ReasonDetail { get; set; }

    public PostSaleStatus Status { get; set; } = PostSaleStatus.Pending;

    /// <summary>True when the whole order is affected; false when <see cref="OrderLineId"/> is set.</summary>
    public bool IsOrderLevel { get; set; }

    /// <summary>The fee split this request was raised under.</summary>
    public FeeAllocation FeeAllocation { get; set; } = new();

    /// <summary>Cash/card amount to refund. Zero for exchanges and customer-fault returns.</summary>
    public decimal RefundAmount { get; set; }

    /// <summary>True when the remedy is a promo code rather than money back.</summary>
    public bool IsPromoCodeRefund { get; set; }

    [StringLength(64)]
    public string? PromoCode { get; set; }

    // ---- Exchange -------------------------------------------------------

    public int? ReplacementVariantId { get; set; }
    public ProductVariant? ReplacementVariant { get; set; }

    // ---- Warranty -------------------------------------------------------

    [StringLength(100)]
    public string? SerialNumber { get; set; }

    /// <summary>Purchase date the warranty runs from (the order's placement date).</summary>
    public DateTime? PurchaseDateUtc { get; set; }

    /// <summary>End of the three-day no-questions refund window.</summary>
    public DateTime? GracePeriodEndUtc { get; set; }

    public bool WithinGracePeriod { get; set; }
    public bool CanBeRepaired { get; set; }
    public bool CanBeReplaced { get; set; }

    // ---- RTS ------------------------------------------------------------

    /// <summary>Carrier key, e.g. <c>libanpost</c>.</summary>
    [StringLength(40)]
    public string? Carrier { get; set; }

    public int CallAttempts { get; set; }

    [StringLength(50)]
    public string? ManifestNumber { get; set; }

    [StringLength(2000)]
    public string? QualityReport { get; set; }

    public bool RedeliveryAttempted { get; set; }

    // ---- Fulfilment of the resolution ----------------------------------

    [StringLength(100)]
    public string? PickupTrackingNumber { get; set; }

    public DateTime? PickupScheduledUtc { get; set; }
    public DateTime? PickedUpUtc { get; set; }
    public DateTime? ReceivedAtWarehouseUtc { get; set; }

    [StringLength(2000)]
    public string? AdminNotes { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedUtc { get; set; }
    public DateTime? ClosedUtc { get; set; }

    [StringLength(256)]
    public string? ProcessedBy { get; set; }

    [StringLength(256)]
    public string? ClosedBy { get; set; }

    public ICollection<PostSaleMessage> Messages { get; set; } = new List<PostSaleMessage>();
    public ICollection<PostSaleStatusHistory> StatusHistory { get; set; } = new List<PostSaleStatusHistory>();

    /// <summary>True while the request is still being worked on.</summary>
    public bool IsOpen => Status is PostSaleStatus.Pending or PostSaleStatus.Approved
        or PostSaleStatus.PickupScheduled or PostSaleStatus.PickedUp or PostSaleStatus.Received;

    /// <summary>What the customer asked for, in words — used by badges and emails.</summary>
    public string Summary
    {
        get
        {
            var subject = IsOrderLevel
                ? "whole order"
                : OrderLine?.ProductName is { Length: > 0 } name ? name : "one item";
            return $"{Type} — {subject}";
        }
    }
}

/// <summary>Message on a post-sale request (customer ↔ operator).</summary>
public class PostSaleMessage
{
    public int Id { get; set; }

    public int PostSaleRequestId { get; set; }
    public PostSaleRequest? PostSaleRequest { get; set; }

    [Required, StringLength(64)]
    public string SenderId { get; set; } = string.Empty;

    [StringLength(256)]
    public string SenderName { get; set; } = string.Empty;

    public bool IsFromCustomer { get; set; }

    [Required, StringLength(2000)]
    public string Content { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Status change audit trail for a post-sale request.</summary>
public class PostSaleStatusHistory
{
    public int Id { get; set; }

    public int PostSaleRequestId { get; set; }
    public PostSaleRequest? PostSaleRequest { get; set; }

    public PostSaleStatus FromStatus { get; set; }
    public PostSaleStatus ToStatus { get; set; }

    [StringLength(256)]
    public string? ChangedBy { get; set; }

    [StringLength(1000)]
    public string? Note { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Seller fulfilment of one order: the package count the seller confirmed, the labels the back
/// office generated and when the shipper collected.
/// </summary>
/// <remarks>
/// Kwikby's delivery process asks the seller to confirm a package count before a cut-off, after
/// which labels are generated and the consignment joins the pending-shipment queue. Tracking the
/// confirmation separately from <see cref="Order"/> is what lets the operator see which sellers are
/// holding up the day's collection without reading the order's whole history.
/// </remarks>
public class SellerFulfilment
{
    public int Id { get; set; }

    public int OrderId { get; set; }
    public Order? Order { get; set; }

    public int ShopId { get; set; }
    public Shop? Shop { get; set; }

    /// <summary>Number of packages the seller confirmed.</summary>
    public int PackageCount { get; set; }

    /// <summary>Whether the seller has the nylon pockets the labels go in.</summary>
    public bool HasNylonPockets { get; set; }

    /// <summary>Whether the seller attached the invoice to the package.</summary>
    public bool InvoiceAttached { get; set; }

    /// <summary>Labels generated by the back office.</summary>
    public int LabelsGenerated { get; set; }

    public DateTime? SellerConfirmedUtc { get; set; }
    public DateTime? LabelsGeneratedUtc { get; set; }
    public DateTime? PickedUpByShipperUtc { get; set; }

    public FulfilmentStatus Status { get; set; } = FulfilmentStatus.AwaitingSellerConfirmation;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public enum FulfilmentStatus
{
    AwaitingSellerConfirmation = 1,
    AwaitingLabelGeneration = 2,
    LabelsGenerated = 3,
    PickedUpByShipper = 4,
    InTransit = 5,
    Delivered = 6,
    RTS = 7
}

public static class PostSaleStatusExtensions
{
    public static string ToDisplay(this PostSaleStatus status) => status switch
    {
        PostSaleStatus.Pending => "Pending review",
        PostSaleStatus.Approved => "Approved",
        PostSaleStatus.Rejected => "Rejected",
        PostSaleStatus.PickupScheduled => "Pickup scheduled",
        PostSaleStatus.PickedUp => "Picked up",
        PostSaleStatus.Received => "Received",
        PostSaleStatus.Processed => "Processed",
        PostSaleStatus.ExchangeShipped => "Replacement shipped",
        PostSaleStatus.Closed => "Closed",
        PostSaleStatus.ReturnedToSeller => "Returned to seller",
        PostSaleStatus.Redelivered => "Redelivered",
        _ => status.ToString()
    };

    public static string ToCssClass(this PostSaleStatus status) => status switch
    {
        PostSaleStatus.Pending => "text-bg-warning",
        PostSaleStatus.Approved => "text-bg-info",
        PostSaleStatus.Rejected => "text-bg-danger",
        PostSaleStatus.PickupScheduled or PostSaleStatus.PickedUp => "text-bg-info",
        PostSaleStatus.Received => "text-bg-primary",
        PostSaleStatus.Processed or PostSaleStatus.ExchangeShipped => "text-bg-success",
        PostSaleStatus.Closed => "text-bg-secondary",
        PostSaleStatus.ReturnedToSeller => "text-bg-warning",
        PostSaleStatus.Redelivered => "text-bg-success",
        _ => "text-bg-secondary"
    };

    /// <summary>Transitions the service accepts from the current state.</summary>
    public static IReadOnlyList<PostSaleStatus> AllowedTransitions(this PostSaleStatus status) => status switch
    {
        PostSaleStatus.Pending => new[]
        {
            PostSaleStatus.Approved, PostSaleStatus.Rejected, PostSaleStatus.Closed
        },
        PostSaleStatus.Approved => new[]
        {
            PostSaleStatus.PickupScheduled, PostSaleStatus.Processed,
            PostSaleStatus.ExchangeShipped, PostSaleStatus.Closed
        },
        PostSaleStatus.PickupScheduled => new[] { PostSaleStatus.PickedUp, PostSaleStatus.Closed },
        PostSaleStatus.PickedUp => new[] { PostSaleStatus.Received, PostSaleStatus.Closed },
        PostSaleStatus.Received => new[] { PostSaleStatus.Processed, PostSaleStatus.Closed },
        PostSaleStatus.ExchangeShipped => new[] { PostSaleStatus.Closed },
        PostSaleStatus.Processed => new[] { PostSaleStatus.Closed },
        PostSaleStatus.ReturnedToSeller => new[] { PostSaleStatus.Closed },
        PostSaleStatus.Redelivered => new[] { PostSaleStatus.Closed },
        _ => Array.Empty<PostSaleStatus>()
    };

    public static string ToDisplay(this PostSaleType type) => type switch
    {
        PostSaleType.Cancellation => "Cancellation",
        PostSaleType.Return => "Return",
        PostSaleType.Exchange => "Exchange",
        PostSaleType.RTS => "Return to sender",
        PostSaleType.Warranty => "Warranty claim",
        _ => type.ToString()
    };

    public static string ToDisplay(this PostSaleReason reason) => reason switch
    {
        PostSaleReason.ChangedMind => "Changed my mind",
        PostSaleReason.Unsatisfied => "Not as described",
        PostSaleReason.WrongItem => "Wrong item received",
        PostSaleReason.Damaged => "Arrived damaged",
        PostSaleReason.Defective => "Defective",
        PostSaleReason.Unreachable => "Customer unreachable",
        PostSaleReason.IncorrectOption => "Wrong option selected",
        PostSaleReason.ShipperDamage => "Damaged in transit",
        _ => reason.ToString()
    };

    public static string ToDisplay(this FeeBearer bearer) => bearer switch
    {
        FeeBearer.Customer => "Customer",
        FeeBearer.Seller => "Seller",
        FeeBearer.Shipper => "Shipper",
        FeeBearer.LibanPost => "LibanPost",
        _ => "—"
    };

    public static string ToDisplay(this FulfilmentStatus status) => status switch
    {
        FulfilmentStatus.AwaitingSellerConfirmation => "Awaiting seller",
        FulfilmentStatus.AwaitingLabelGeneration => "Awaiting labels",
        FulfilmentStatus.LabelsGenerated => "Labels generated",
        FulfilmentStatus.PickedUpByShipper => "Picked up",
        FulfilmentStatus.InTransit => "In transit",
        FulfilmentStatus.Delivered => "Delivered",
        FulfilmentStatus.RTS => "Returning to seller",
        _ => status.ToString()
    };

    public static string ToCssClass(this FulfilmentStatus status) => status switch
    {
        FulfilmentStatus.AwaitingSellerConfirmation => "text-bg-secondary",
        FulfilmentStatus.AwaitingLabelGeneration => "text-bg-warning",
        FulfilmentStatus.LabelsGenerated => "text-bg-info",
        FulfilmentStatus.PickedUpByShipper or FulfilmentStatus.InTransit => "text-bg-primary",
        FulfilmentStatus.Delivered => "text-bg-success",
        FulfilmentStatus.RTS => "text-bg-danger",
        _ => "text-bg-secondary"
    };
}
