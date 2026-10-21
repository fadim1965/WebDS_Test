using System.Net.Http.Json;
using System.Text.Json;

namespace WebDS_Test.Services.Shipping;

/// <summary>Where a consignment is going, and who receives it.</summary>
public sealed class ShippingAddress
{
    public string RecipientName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string Line1 { get; set; } = string.Empty;
    public string? Line2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string? State { get; set; }
    public string PostalCode { get; set; } = string.Empty;
    public string Country { get; set; } = "Lebanon";

    public string SingleLine => string.Join(", ", new[] { Line1, Line2, City, State, PostalCode, Country }
        .Where(part => !string.IsNullOrWhiteSpace(part)));
}

/// <summary>What the carrier needs in order to accept one consignment.</summary>
public sealed class ShipmentRequest
{
    /// <summary>Marketplace order number. Used as the carrier's own reference.</summary>
    public string OrderNumber { get; set; } = string.Empty;

    /// <summary>Shop the consignment originates from.</summary>
    public int ShopId { get; set; }
    public string ShopName { get; set; } = string.Empty;

    /// <summary>Branch or counter the courier collects from.</summary>
    public string? PickupLocation { get; set; }
    public string? PickupAddress { get; set; }
    public string? PickupPhone { get; set; }

    public ShippingAddress Delivery { get; set; } = new();

    /// <summary>Declared billable weight.</summary>
    public int WeightGrams { get; set; } = 500;

    /// <summary>Number of parcels in the consignment.</summary>
    public int ParcelCount { get; set; } = 1;

    /// <summary>
    /// Cash the courier must collect on delivery. Zero means nothing to collect.
    /// This is the reason the carrier integration exists at all for this marketplace.
    /// </summary>
    public decimal CodAmount { get; set; }

    [System.ComponentModel.DataAnnotations.StringLength(8)]
    public string Currency { get; set; } = "USD";

    /// <summary>Free-text handling instruction passed through to the carrier.</summary>
    public string? Notes { get; set; }
}

/// <summary>A consignment the carrier has accepted.</summary>
public sealed record ShipmentReceipt(
    bool Success,
    string? CarrierShipmentId,
    string? TrackingNumber,
    string? LabelUrl,
    decimal ShippingCost,
    string? Error = null)
{
    public static ShipmentReceipt Failed(string error) => new(false, null, null, null, 0m, error);
}

/// <summary>One tracking event as reported by the carrier.</summary>
public sealed record TrackingEvent(
    ShipmentStatus Status,
    string? Location,
    string? Description,
    DateTime OccurredUtc);

/// <summary>A carrier's current view of a consignment.</summary>
public sealed record TrackingResult(
    bool Success,
    ShipmentStatus Status,
    IReadOnlyList<TrackingEvent> Events,
    string? Error = null);

/// <summary>Quoted cost and transit time before booking.</summary>
public sealed record ShippingQuote(
    bool Success,
    decimal Cost,
    int TransitDays,
    string? ServiceName = null,
    string? Error = null);

/// <summary>
/// A delivery carrier. Deliberately narrow: the marketplace only needs to hand a parcel over,
/// collect cash on it, and follow it. Anything provider-specific stays inside the implementation.
/// </summary>
public interface IShippingCarrier
{
    /// <summary>Stable key stored on shipments, e.g. <c>libanpost</c>.</summary>
    string Key { get; }

    /// <summary>Name shown to vendors.</summary>
    string DisplayName { get; }

    /// <summary>False when the carrier is a local simulator rather than a real service.</summary>
    bool IsLive { get; }

    /// <summary>Books a consignment and returns the tracking number and label.</summary>
    Task<ShipmentReceipt> CreateShipmentAsync(ShipmentRequest request, CancellationToken ct = default);

    /// <summary>Pulls the current tracking state and event history.</summary>
    Task<TrackingResult> TrackAsync(string trackingNumber, CancellationToken ct = default);

    /// <summary>Prices a consignment without booking it.</summary>
    Task<ShippingQuote> QuoteAsync(ShipmentRequest request, CancellationToken ct = default);

    /// <summary>Cancels a consignment that has not yet been delivered.</summary>
    Task<ShipmentReceipt> CancelAsync(string trackingNumber, CancellationToken ct = default);
}

/// <summary>Resolves carriers by key so the fulfilment code never names a concrete class.</summary>
public interface ICarrierRegistry
{
    /// <summary>The carrier used when a shop has no preference.</summary>
    IShippingCarrier Default { get; }

    IShippingCarrier? Resolve(string? key);

    IReadOnlyList<IShippingCarrier> All { get; }
}
