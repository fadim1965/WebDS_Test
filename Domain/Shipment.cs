using System.ComponentModel.DataAnnotations;

namespace WebDS_Test.Domain;

/// <summary>
/// A consignment handed to a carrier for one order.
/// </summary>
/// <remarks>
/// Modelled separately from <see cref="Order"/> rather than as columns on it, because a delivery
/// can be re-attempted: a failed consignment is returned and a new one created, and the vendor needs
/// to see that history rather than have it overwritten.
/// </remarks>
public class OrderShipment
{
    public int Id { get; set; }

    public int OrderId { get; set; }
    public Order? Order { get; set; }

    public int ShopId { get; set; }
    public Shop? Shop { get; set; }

    /// <summary>Carrier key, e.g. <c>libanpost</c> or <c>simulator</c>.</summary>
    [Required, StringLength(40)]
    public string Carrier { get; set; } = string.Empty;

    /// <summary>Carrier-side consignment identifier.</summary>
    [StringLength(120)]
    public string? CarrierShipmentId { get; set; }

    /// <summary>Tracking number the shopper and vendor both use.</summary>
    [StringLength(120)]
    public string? TrackingNumber { get; set; }

    /// <summary>
    /// URL of the printable label. Uncapped on purpose — see <see cref="Order.LabelUrl"/>.
    /// </summary>
    public string? LabelUrl { get; set; }

    public ShipmentStatus Status { get; set; } = ShipmentStatus.Draft;

    /// <summary>Billable weight in grams, as declared to the carrier.</summary>
    public int WeightGrams { get; set; }

    /// <summary>What the carrier charges the marketplace for this consignment.</summary>
    public decimal ShippingCost { get; set; }

    /// <summary>Amount the carrier collects from the shopper on delivery.</summary>
    public decimal CodAmount { get; set; }

    // ---- Address as declared to the carrier (snapshot, independent of the order) ----
    [StringLength(120)] public string? RecipientName { get; set; }
    [StringLength(40)] public string? RecipientPhone { get; set; }
    [StringLength(400)] public string? DeliveryAddress { get; set; }

    /// <summary>Branch or counter the courier collects from.</summary>
    [StringLength(120)] public string? PickupLocation { get; set; }

    [StringLength(600)]
    public string? LastError { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DispatchedUtc { get; set; }
    public DateTime? DeliveredUtc { get; set; }

    public ICollection<ShipmentEvent> Events { get; set; } = new List<ShipmentEvent>();

    /// <summary>True while the consignment is still moving and can receive carrier updates.</summary>
    public bool IsOpen => Status is ShipmentStatus.Draft or ShipmentStatus.Created
        or ShipmentStatus.PickedUp or ShipmentStatus.InTransit or ShipmentStatus.OutForDelivery;
}

/// <summary>
/// A tracking event reported by the carrier. Append-only, so the tracking history a shopper sees is
/// exactly what the carrier told us, in the order it was told to us.
/// </summary>
public class ShipmentEvent
{
    public int Id { get; set; }

    public int ShipmentId { get; set; }
    public OrderShipment? Shipment { get; set; }

    public ShipmentStatus Status { get; set; }

    [StringLength(80)]
    public string? Location { get; set; }

    [StringLength(300)]
    public string? Description { get; set; }

    /// <summary>When the carrier says the event happened, which may precede when we heard about it.</summary>
    public DateTime OccurredUtc { get; set; } = DateTime.UtcNow;

    public DateTime RecordedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>True when the event arrived from the carrier rather than being set by a vendor.</summary>
    public bool FromCarrier { get; set; }
}

public static class ShipmentStatusExtensions
{
    public static string ToDisplay(this ShipmentStatus status) => status switch
    {
        ShipmentStatus.Draft => "Draft",
        ShipmentStatus.Created => "Label created",
        ShipmentStatus.PickedUp => "Picked up",
        ShipmentStatus.InTransit => "In transit",
        ShipmentStatus.OutForDelivery => "Out for delivery",
        ShipmentStatus.Delivered => "Delivered",
        ShipmentStatus.Failed => "Delivery failed",
        ShipmentStatus.Returned => "Returned to vendor",
        ShipmentStatus.Cancelled => "Cancelled",
        _ => status.ToString()
    };

    public static string ToCssClass(this ShipmentStatus status) => status switch
    {
        ShipmentStatus.Draft => "text-bg-secondary",
        ShipmentStatus.Created => "text-bg-info",
        ShipmentStatus.PickedUp => "text-bg-info",
        ShipmentStatus.InTransit => "text-bg-primary",
        ShipmentStatus.OutForDelivery => "text-bg-primary",
        ShipmentStatus.Delivered => "text-bg-success",
        ShipmentStatus.Failed => "text-bg-danger",
        ShipmentStatus.Returned => "text-bg-warning",
        ShipmentStatus.Cancelled => "text-bg-secondary",
        _ => "text-bg-secondary"
    };

    /// <summary>Maps the order status implied by a carrier event back onto the order.</summary>
    public static OrderStatus? ToOrderStatus(this ShipmentStatus status) => status switch
    {
        ShipmentStatus.PickedUp or ShipmentStatus.InTransit or ShipmentStatus.OutForDelivery => OrderStatus.Shipped,
        ShipmentStatus.Delivered => OrderStatus.Delivered,
        ShipmentStatus.Failed or ShipmentStatus.Returned => null,
        _ => null
    };
}

public static class CollectionStatusExtensions
{
    public static string ToDisplay(this CollectionStatus status) => status switch
    {
        CollectionStatus.NotApplicable => "Not applicable",
        CollectionStatus.AwaitingCollection => "Awaiting collection",
        CollectionStatus.Collected => "Cash collected",
        CollectionStatus.CollectionFailed => "Collection failed",
        CollectionStatus.Refunded => "Refunded",
        _ => status.ToString()
    };

    public static string ToCssClass(this CollectionStatus status) => status switch
    {
        CollectionStatus.NotApplicable => "text-bg-light",
        CollectionStatus.AwaitingCollection => "text-bg-warning",
        CollectionStatus.Collected => "text-bg-success",
        CollectionStatus.CollectionFailed => "text-bg-danger",
        CollectionStatus.Refunded => "text-bg-secondary",
        _ => "text-bg-secondary"
    };
}

public static class PaymentMethodExtensions
{
    public static string ToDisplay(this PaymentMethod method) => method switch
    {
        PaymentMethod.CashOnDelivery => "Cash on delivery",
        PaymentMethod.Card => "Card",
        PaymentMethod.CounterPayment => "Counter payment",
        _ => method.ToString()
    };
}
