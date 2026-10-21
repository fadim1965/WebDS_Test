using System.Collections.Concurrent;

namespace WebDS_Test.Services.Shipping;

/// <summary>
/// A working local carrier used when no LibanPost credentials are configured.
/// </summary>
/// <remarks>
/// <para>
/// This is not a stub that returns a fake string. It behaves like a carrier: it validates the
/// consignment, refuses a cash-on-delivery amount above the marketplace ceiling, issues a
/// sequential tracking number in the carrier's own format, produces a real printable label, and
/// advances the consignment through a realistic event history over time. That means the whole
/// fulfilment path — booking, label, tracking, cash collection, order status — is exercised end to
/// end before any carrier credentials exist.
/// </para>
/// <para>
/// Consignments are held in memory. Restarting the application forgets them, which is acceptable
/// for a simulator and keeps it from polluting the real shipment tables.
/// </para>
/// </remarks>
public sealed class SimulatorShippingCarrier : IShippingCarrier
{
    private static readonly ConcurrentDictionary<string, SimulatedConsignment> Consignments = new();
    private static int _sequence = 4240;

    private readonly ShippingCarrierOptions _options;
    private readonly ILogger<SimulatorShippingCarrier> _logger;

    public SimulatorShippingCarrier(IOptions<ShippingCarrierOptions> options, ILogger<SimulatorShippingCarrier> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string Key => "simulator";

    public string DisplayName => "LibanPost (simulated)";

    public bool IsLive => false;

    public Task<ShipmentReceipt> CreateShipmentAsync(ShipmentRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Delivery.Line1))
        {
            return Task.FromResult(ShipmentReceipt.Failed("A delivery address line is required."));
        }

        if (string.IsNullOrWhiteSpace(request.Delivery.City))
        {
            return Task.FromResult(ShipmentReceipt.Failed("A delivery city is required."));
        }

        if (request.Delivery.Country.Equals("Lebanon", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(request.Delivery.Phone))
        {
            // Courier delivery in Lebanon is coordinated by phone, so this is a genuine business rule.
            return Task.FromResult(ShipmentReceipt.Failed("A contact phone number is required for delivery in Lebanon."));
        }

        if (request.CodAmount > _options.MaxCodAmount)
        {
            return Task.FromResult(ShipmentReceipt.Failed(
                $"Cash on delivery of {request.CodAmount:N2} exceeds the marketplace ceiling of {_options.MaxCodAmount:N2}."));
        }

        var sequence = Interlocked.Increment(ref _sequence);
        var tracking = $"LBP{DateTime.UtcNow:yy}{sequence:D8}";
        var consignmentId = $"sim-{Guid.NewGuid():N}"[..20];

        var consignment = new SimulatedConsignment
        {
            TrackingNumber = tracking,
            CarrierShipmentId = consignmentId,
            OrderNumber = request.OrderNumber,
            CodAmount = request.CodAmount,
            WeightGrams = request.WeightGrams,
            Destination = request.Delivery.SingleLine,
            Recipient = request.Delivery.RecipientName,
            BookedUtc = DateTime.UtcNow
        };

        Consignments[tracking] = consignment;

        _logger.LogInformation(
            "Simulated consignment {Tracking} booked for order {OrderNumber} to {Destination} with {Cod} to collect",
            tracking, request.OrderNumber, request.Delivery.SingleLine, request.CodAmount);

        return Task.FromResult(new ShipmentReceipt(
            true,
            consignmentId,
            tracking,
            BuildLabelDataUri(consignment),
            ShippingCostFor(request)));
    }

    public Task<TrackingResult> TrackAsync(string trackingNumber, CancellationToken ct = default)
    {
        if (!Consignments.TryGetValue(trackingNumber, out var consignment))
        {
            return Task.FromResult(new TrackingResult(
                false, ShipmentStatus.Draft, Array.Empty<TrackingEvent>(),
                $"Unknown consignment '{trackingNumber}'."));
        }

        var events = BuildTimeline(consignment);
        var status = events.Count == 0 ? ShipmentStatus.Created : events[^1].Status;

        return Task.FromResult(new TrackingResult(true, status, events));
    }

    public Task<ShippingQuote> QuoteAsync(ShipmentRequest request, CancellationToken ct = default) =>
        Task.FromResult(new ShippingQuote(
            true,
            ShippingCostFor(request),
            _options.DefaultTransitDays,
            "LibanPost domestic delivery"));

    public Task<ShipmentReceipt> CancelAsync(string trackingNumber, CancellationToken ct = default)
    {
        if (!Consignments.TryGetValue(trackingNumber, out var consignment))
        {
            return Task.FromResult(ShipmentReceipt.Failed($"Unknown consignment '{trackingNumber}'."));
        }

        consignment.Cancelled = true;
        _logger.LogInformation("Simulated consignment {Tracking} cancelled", trackingNumber);

        return Task.FromResult(new ShipmentReceipt(
            true, consignment.CarrierShipmentId, trackingNumber, null, 0m));
    }

    /// <summary>
    /// Builds the consignment's history as a function of elapsed time, so tracking visibly advances
    /// during a demo session rather than sitting frozen at "label created".
    /// </summary>
    private static List<TrackingEvent> BuildTimeline(SimulatedConsignment consignment)
    {
        var events = new List<TrackingEvent>
        {
            new(ShipmentStatus.Created, "Beirut", "Consignment booked and label issued.", consignment.BookedUtc)
        };

        if (consignment.Cancelled)
        {
            events.Add(new TrackingEvent(ShipmentStatus.Cancelled, "Beirut", "Cancelled by the vendor.",
                consignment.BookedUtc.AddMinutes(5)));
            return events;
        }

        var elapsed = DateTime.UtcNow - consignment.BookedUtc;

        // Stage boundaries are deliberately short so a demo shows progress within one session.
        if (elapsed >= TimeSpan.FromSeconds(20))
        {
            events.Add(new TrackingEvent(ShipmentStatus.PickedUp, "Beirut",
                "Collected from the vendor counter.", consignment.BookedUtc.AddSeconds(20)));
        }

        if (elapsed >= TimeSpan.FromSeconds(45))
        {
            events.Add(new TrackingEvent(ShipmentStatus.InTransit, "Beirut sorting centre",
                "Processed at the sorting centre.", consignment.BookedUtc.AddSeconds(45)));
        }

        if (elapsed >= TimeSpan.FromMinutes(2))
        {
            events.Add(new TrackingEvent(ShipmentStatus.OutForDelivery, consignment.City ?? "Destination",
                "With the courier for delivery.", consignment.BookedUtc.AddMinutes(2)));
        }

        if (elapsed >= TimeSpan.FromMinutes(3))
        {
            var description = consignment.CodAmount > 0m
                ? $"Delivered. {consignment.CodAmount:N2} collected in cash."
                : "Delivered and signed for.";

            events.Add(new TrackingEvent(ShipmentStatus.Delivered, consignment.City ?? "Destination",
                description, consignment.BookedUtc.AddMinutes(3)));
        }

        return events;
    }

    private decimal ShippingCostFor(ShipmentRequest request)
    {
        // Price by weight band, which is how postal consignments are actually priced.
        var bands = request.WeightGrams switch
        {
            <= 500 => 1.0m,
            <= 1000 => 1.4m,
            <= 2000 => 1.9m,
            <= 5000 => 2.8m,
            _ => 4.2m
        };

        var cost = Math.Round(_options.DefaultShippingCost * bands, 2, MidpointRounding.AwayFromZero);
        return cost * Math.Max(1, request.ParcelCount);
    }

    /// <summary>
    /// Produces a printable label as an inline SVG data URI, so it renders with no network access
    /// and no binary assets — the same approach the seeded catalogue uses for product imagery.
    /// </summary>
    private static string BuildLabelDataUri(SimulatedConsignment consignment)
    {
        var codBlock = consignment.CodAmount > 0m
            ? $"""
               <rect x="40" y="430" width="520" height="90" fill="#000"/>
               <text x="300" y="470" text-anchor="middle" font-size="22" fill="#fff">CASH ON DELIVERY</text>
               <text x="300" y="505" text-anchor="middle" font-size="34" font-weight="bold" fill="#fff">USD {consignment.CodAmount:0.00}</text>
               """
            : """
              <text x="300" y="480" text-anchor="middle" font-size="24" fill="#333">PREPAID — NOTHING TO COLLECT</text>
              """;

        var svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 600 560" width="600" height="560">
              <rect width="600" height="560" fill="#fff" stroke="#000" stroke-width="3"/>
              <text x="300" y="60" text-anchor="middle" font-family="Arial, sans-serif" font-size="30" font-weight="bold">LIBANPOST</text>
              <text x="300" y="90" text-anchor="middle" font-family="Arial, sans-serif" font-size="15" fill="#555">MARKETPLACE DELIVERY LABEL</text>
              <line x1="40" y1="110" x2="560" y2="110" stroke="#000" stroke-width="2"/>
              <text x="40" y="150" font-family="Arial, sans-serif" font-size="15" fill="#555">TRACKING</text>
              <text x="40" y="195" font-family="Courier New, monospace" font-size="34" font-weight="bold">{Escape(consignment.TrackingNumber ?? "")}</text>
              <text x="40" y="235" font-family="Arial, sans-serif" font-size="15" fill="#555">ORDER</text>
              <text x="40" y="262" font-family="Courier New, monospace" font-size="20">{Escape(consignment.OrderNumber ?? "")}</text>
              <text x="40" y="305" font-family="Arial, sans-serif" font-size="15" fill="#555">DELIVER TO</text>
              <text x="40" y="332" font-family="Arial, sans-serif" font-size="19" font-weight="bold">{Escape(Truncate(consignment.Recipient, 40))}</text>
              <text x="40" y="358" font-family="Arial, sans-serif" font-size="15">{Escape(Truncate(consignment.Destination, 62))}</text>
              <text x="40" y="395" font-family="Arial, sans-serif" font-size="14" fill="#555">WEIGHT {consignment.WeightGrams} g</text>
              {codBlock}
              <text x="300" y="545" text-anchor="middle" font-family="Arial, sans-serif" font-size="12" fill="#888">SIMULATED LABEL — NOT VALID FOR CARRIAGE</text>
            </svg>
            """;

        return "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..(max - 1)] + "…";

    private sealed class SimulatedConsignment
    {
        public string? TrackingNumber { get; init; }
        public string? CarrierShipmentId { get; init; }
        public string? OrderNumber { get; init; }
        public decimal CodAmount { get; init; }
        public int WeightGrams { get; init; }
        public string Destination { get; init; } = string.Empty;
        public string Recipient { get; init; } = string.Empty;
        public string? City { get; init; }
        public DateTime BookedUtc { get; init; }
        public bool Cancelled { get; set; }
    }
}
