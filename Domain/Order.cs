using System.ComponentModel.DataAnnotations;

namespace WebDS_Test.Domain;

/// <summary>
/// A placed order. Every value a customer saw at checkout is snapshotted here so that later
/// catalogue edits never rewrite history.
/// </summary>
public class Order
{
    public int Id { get; set; }

    /// <summary>
    /// The shop that fulfils this order. Always exactly one: checkout splits a mixed cart into one
    /// order per shop, so a consignment has a single origin for the carrier to collect from.
    /// </summary>
    public int ShopId { get; set; }
    public Shop? Shop { get; set; }

    /// <summary>Human-facing reference, e.g. <c>ORD-2026-0001</c>. Unique.</summary>
    [Required, StringLength(32)]
    public string OrderNumber { get; set; } = string.Empty;

    public string? UserId { get; set; }
    public ApplicationUser? User { get; set; }

    /// <summary>Captured at checkout so a guest order can still be contacted.</summary>
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(120)]
    public string CustomerName { get; set; } = string.Empty;

    [StringLength(40)]
    public string? Phone { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.Pending;

    /// <summary>
    /// How the shopper pays. The marketplace runs on cash on delivery, so an order is not "paid"
    /// until the carrier collects — see <see cref="CollectionStatus"/>.
    /// </summary>
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.CashOnDelivery;

    /// <summary>Progress of the cash collection, updated from carrier events.</summary>
    public CollectionStatus CollectionStatus { get; set; } = CollectionStatus.NotApplicable;

    /// <summary>Amount the carrier is instructed to collect. Zero for prepaid orders.</summary>
    public decimal CodAmount { get; set; }

    /// <summary>Prevents a duplicate submission creating two orders. Unique when non-null.</summary>
    [StringLength(64)]
    public string? IdempotencyKey { get; set; }

    // ---- Money snapshot -------------------------------------------------
    public decimal Subtotal { get; set; }
    public decimal ShippingTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal GrandTotal { get; set; }

    /// <summary>
    /// Sum of everything refunded to the shopper so far — from the one-shot full refund and from
    /// completed return requests together. Kept on the order because "what can still be refunded"
    /// has to be answerable from the order alone; a stored zero is a real zero, and every refund
    /// path adds to it.
    /// </summary>
    public decimal RefundedTotal { get; set; }

    [StringLength(64)]
    public string? CouponCode { get; set; }
    public int? CouponId { get; set; }
    public Coupon? Coupon { get; set; }

    // ---- Shipping address snapshot (flat, immutable) --------------------
    [Required, StringLength(120)] public string ShippingName { get; set; } = string.Empty;
    [Required, StringLength(200)] public string ShippingLine1 { get; set; } = string.Empty;
    [StringLength(200)] public string? ShippingLine2 { get; set; }
    [Required, StringLength(100)] public string ShippingCity { get; set; } = string.Empty;
    [StringLength(100)] public string? ShippingState { get; set; }
    [Required, StringLength(20)] public string ShippingPostalCode { get; set; } = string.Empty;
    [Required, StringLength(80)] public string ShippingCountry { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? CustomerNote { get; set; }

    public DateTime PlacedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? PaidUtc { get; set; }
    public DateTime? ShippedUtc { get; set; }
    public DateTime? DeliveredUtc { get; set; }
    public DateTime? CancelledUtc { get; set; }

    [StringLength(64)]
    public string? TrackingNumber { get; set; }

    /// <summary>Carrier that took the consignment, e.g. <c>libanpost</c>.</summary>
    [StringLength(40)]
    public string? Carrier { get; set; }

    /// <summary>Carrier-side consignment identifier returned when the shipment was created.</summary>
    [StringLength(120)]
    public string? CarrierShipmentId { get; set; }

    /// <summary>
    /// Printable label produced with the consignment. No length cap: a hosted carrier returns a
    /// short URL, but the simulator embeds the label as a data URI, and truncating a label breaks
    /// printing for no benefit.
    /// </summary>
    public string? LabelUrl { get; set; }

    /// <summary>Why the last carrier interaction failed, surfaced to the vendor.</summary>
    [StringLength(500)]
    public string? CarrierError { get; set; }

    public ICollection<OrderLine> Lines { get; set; } = new List<OrderLine>();
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    public ICollection<OrderShipment> Shipments { get; set; } = new List<OrderShipment>();

    public int TotalQuantity => Lines.Sum(l => l.Quantity);

    /// <summary>Single-line summary of the shipping address for tables and emails.</summary>
    public string ShippingSummary =>
        string.Join(", ", new[] { ShippingLine1, ShippingLine2, ShippingCity, ShippingState, ShippingPostalCode, ShippingCountry }
            .Where(p => !string.IsNullOrWhiteSpace(p)));
}

/// <summary>
/// An immutable order line. Product name, SKU and unit price are copied in at purchase time —
/// they are never re-read from the catalogue.
/// </summary>
public class OrderLine
{
    public int Id { get; set; }

    public int OrderId { get; set; }
    public Order? Order { get; set; }

    /// <summary>Kept for reporting and "buy again"; the variant may later be deleted.</summary>
    public int? ProductVariantId { get; set; }
    public ProductVariant? ProductVariant { get; set; }

    public int? ProductId { get; set; }

    [Required, StringLength(160)]
    public string ProductName { get; set; } = string.Empty;

    [StringLength(80)]
    public string VariantName { get; set; } = string.Empty;

    [Required, StringLength(64)]
    public string Sku { get; set; } = string.Empty;

    /// <summary>Snapshot of the ordered variant's image; may be an inline base64 data URI.</summary>
    public string? ImageUrl { get; set; }

    public int Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal LineTotal { get; set; }
}

/// <summary>A payment attempt against an order, recorded by the gateway abstraction.</summary>
public class Payment
{
    public int Id { get; set; }

    public int OrderId { get; set; }
    public Order? Order { get; set; }

    [Required, StringLength(40)]
    public string Provider { get; set; } = string.Empty;

    /// <summary>Reference issued by the provider, unique when present.</summary>
    [StringLength(120)]
    public string? TransactionId { get; set; }

    public decimal Amount { get; set; }

    [StringLength(8)]
    public string Currency { get; set; } = "USD";

    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

    [StringLength(500)]
    public string? FailureReason { get; set; }

    /// <summary>Marks the sandbox path so real revenue reporting can exclude it.</summary>
    public bool IsTest { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
}

/// <summary>Append-only ledger of inventory changes, so stock is always explainable.</summary>
public class StockMovement
{
    public int Id { get; set; }

    public int ProductVariantId { get; set; }
    public ProductVariant? ProductVariant { get; set; }

    /// <summary>Signed delta: negative for a sale, positive for a restock.</summary>
    public int QuantityDelta { get; set; }

    /// <summary>Stock level after the movement was applied.</summary>
    public int StockAfter { get; set; }

    public StockMovementReason Reason { get; set; }

    [StringLength(200)]
    public string? Note { get; set; }

    public int? OrderId { get; set; }

    [StringLength(100)]
    public string? CreatedBy { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
