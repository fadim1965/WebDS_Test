using WebDS_Test.Services.Email;
using WebDS_Test.Services.Shipping;

namespace WebDS_Test.Services;

/// <summary>Outcome of a fulfilment or tracking operation.</summary>
public sealed record FulfilmentResult(bool Success, string Message, OrderShipment? Shipment = null)
{
    public static FulfilmentResult Ok(OrderShipment shipment, string message) => new(true, message, shipment);
    public static FulfilmentResult Ok(string message) => new(true, message);
    public static FulfilmentResult Fail(string message) => new(false, message);
}

/// <summary>
/// Turns a paid order into a consignment, follows it, and records the cash the courier collects.
/// </summary>
/// <remarks>
/// This is where the marketplace meets LibanPost. It deliberately owns three things that must stay
/// consistent with each other: the shipment record, the order's status, and the vendor's ledger.
/// A tracking event that says "delivered" has to update all three, or the vendor's payable and the
/// shopper's order page will disagree.
/// </remarks>
public interface IFulfilmentService
{
    /// <summary>Books a consignment with the carrier and moves the order to Shipped.</summary>
    Task<FulfilmentResult> DispatchAsync(int orderId, int shopId, string carrierKey, int? weightGrams = null, string? notes = null, CancellationToken ct = default);

    /// <summary>Pulls the latest tracking state and applies it to the order and the ledger.</summary>
    Task<FulfilmentResult> RefreshTrackingAsync(int shipmentId, CancellationToken ct = default);

    /// <summary>Refreshes every open consignment for a shop. Used by the vendor's fulfilment page.</summary>
    Task<int> RefreshOpenShipmentsAsync(int shopId, CancellationToken ct = default);

    /// <summary>Records a status a vendor set by hand, for when a carrier cannot be reached.</summary>
    Task<FulfilmentResult> RecordManualEventAsync(int shipmentId, ShipmentStatus status, string? location, string? description, CancellationToken ct = default);

    /// <summary>Cancels a consignment that has not been delivered.</summary>
    Task<FulfilmentResult> CancelShipmentAsync(int shipmentId, CancellationToken ct = default);

    /// <summary>Recent consignments for a shop's fulfilment queue.</summary>
    Task<IReadOnlyList<OrderShipment>> GetShipmentsForShopAsync(int shopId, bool openOnly = false, CancellationToken ct = default);

    Task<OrderShipment?> GetShipmentAsync(int shipmentId, CancellationToken ct = default);

    /// <summary>Public tracking lookup by tracking number, for the storefront's tracking page.</summary>
    Task<OrderShipment?> FindByTrackingNumberAsync(string trackingNumber, CancellationToken ct = default);
}

public sealed class FulfilmentService : IFulfilmentService
{
    private readonly ApplicationDbContext _db;
    private readonly ICarrierRegistry _carriers;
    private readonly IEmailService _emails;
    private readonly IVendorLedgerService _ledger;
    private readonly ShippingCarrierOptions _carrierOptions;
    private readonly ILogger<FulfilmentService> _logger;

    public FulfilmentService(
        ApplicationDbContext db,
        ICarrierRegistry carriers,
        IEmailService emails,
        IVendorLedgerService ledger,
        IOptions<ShippingCarrierOptions> carrierOptions,
        ILogger<FulfilmentService> logger)
    {
        _db = db;
        _carriers = carriers;
        _emails = emails;
        _ledger = ledger;
        _carrierOptions = carrierOptions.Value;
        _logger = logger;
    }

    public async Task<FulfilmentResult> DispatchAsync(
        int orderId, int shopId, string carrierKey, int? weightGrams = null, string? notes = null,
        CancellationToken ct = default)
    {
        var order = await _db.Orders
            .Include(o => o.Lines)
            .Include(o => o.Shop)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.ShopId == shopId, ct);

        if (order is null)
        {
            // Scoped by shop id, so a vendor cannot dispatch another shop's order even by guessing.
            return FulfilmentResult.Fail("Order not found for this shop.");
        }

        if (order.Shipments.Any(s => s.IsOpen))
        {
            return FulfilmentResult.Fail(
                $"Order {order.OrderNumber} already has an open consignment ({order.Shipments.First(s => s.IsOpen).TrackingNumber}).");
        }

        // Cash on delivery is the marketplace's payment model, so an order is dispatched unpaid and
        // the courier collects. The amount collected equals the order grand total.
        var codAmount = order.PaymentMethod == PaymentMethod.CashOnDelivery ? order.GrandTotal : 0m;
        var weight = weightGrams ?? EstimateWeight(order);

        var request = new ShipmentRequest
        {
            OrderNumber = order.OrderNumber,
            ShopId = shopId,
            ShopName = order.Shop?.Name ?? "Vendor",
            PickupLocation = order.Shop?.PickupLocation,
            PickupAddress = order.Shop?.AddressLine1,
            PickupPhone = order.Shop?.Phone,
            WeightGrams = weight,
            ParcelCount = 1,
            CodAmount = codAmount,
            Currency = order.Shop?.Currency ?? "USD",
            Delivery = new ShippingAddress
            {
                RecipientName = order.ShippingName,
                Phone = order.Phone,
                Line1 = order.ShippingLine1,
                Line2 = order.ShippingLine2,
                City = order.ShippingCity,
                State = order.ShippingState,
                PostalCode = order.ShippingPostalCode,
                Country = order.ShippingCountry
            },
            Notes = notes
        };

        var carrier = _carriers.Resolve(carrierKey) ?? _carriers.Default;
        var receipt = await carrier.CreateShipmentAsync(request, ct);

        if (!receipt.Success)
        {
            order.CarrierError = receipt.Error;
            await _db.SaveChangesAsync(ct);

            _logger.LogWarning("Dispatch of {OrderNumber} failed: {Error}", order.OrderNumber, receipt.Error);
            return FulfilmentResult.Fail(receipt.Error ?? "The carrier could not accept the consignment.");
        }

        var shipment = new OrderShipment
        {
            OrderId = order.Id,
            ShopId = shopId,
            Carrier = carrier.Key,
            CarrierShipmentId = receipt.CarrierShipmentId,
            TrackingNumber = receipt.TrackingNumber,
            LabelUrl = receipt.LabelUrl,
            Status = ShipmentStatus.Created,
            WeightGrams = weight,
            ShippingCost = receipt.ShippingCost,
            CodAmount = codAmount,
            RecipientName = order.ShippingName,
            RecipientPhone = order.Phone,
            DeliveryAddress = request.Delivery.SingleLine,
            PickupLocation = order.Shop?.PickupLocation,
            CreatedUtc = DateTime.UtcNow
        };

        shipment.Events.Add(new ShipmentEvent
        {
            Status = ShipmentStatus.Created,
            Location = "Vendor",
            Description = $"Consignment booked with {carrier.DisplayName}.",
            OccurredUtc = DateTime.UtcNow,
            RecordedUtc = DateTime.UtcNow,
            FromCarrier = true
        });

        _db.OrderShipments.Add(shipment);

        // Mirror the essentials onto the order so the shopper's pages do not need the shipment graph.
        order.Carrier = carrier.Key;
        order.CarrierShipmentId = receipt.CarrierShipmentId;
        order.TrackingNumber = receipt.TrackingNumber;
        order.LabelUrl = receipt.LabelUrl;
        order.CarrierError = null;
        order.ShippedUtc ??= DateTime.UtcNow;

        if (codAmount > 0m)
        {
            order.CodAmount = codAmount;
            order.CollectionStatus = CollectionStatus.AwaitingCollection;
        }

        var becameShipped = false;
        if (order.Status is OrderStatus.Paid or OrderStatus.Pending)
        {
            order.Status = OrderStatus.Shipped;
            becameShipped = true;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Order {OrderNumber} dispatched via {Carrier} as {Tracking}",
            order.OrderNumber, carrier.Key, shipment.TrackingNumber);

        if (becameShipped)
        {
            await TryNotifyShippedAsync(order, ct);
        }

        return FulfilmentResult.Ok(shipment,
            $"Consignment booked. Tracking number {shipment.TrackingNumber}.");
    }

    public async Task<FulfilmentResult> RefreshTrackingAsync(int shipmentId, CancellationToken ct = default)
    {
        var shipment = await _db.OrderShipments
            .Include(s => s.Events)
            .Include(s => s.Order)
            .FirstOrDefaultAsync(s => s.Id == shipmentId, ct);

        if (shipment is null)
        {
            return FulfilmentResult.Fail("Consignment not found.");
        }

        if (string.IsNullOrWhiteSpace(shipment.TrackingNumber))
        {
            return FulfilmentResult.Fail("This consignment has no tracking number yet.");
        }

        var carrier = _carriers.Resolve(shipment.Carrier) ?? _carriers.Default;
        var tracking = await carrier.TrackAsync(shipment.TrackingNumber, ct);

        if (!tracking.Success)
        {
            shipment.LastError = tracking.Error;
            await _db.SaveChangesAsync(ct);
            return FulfilmentResult.Fail(tracking.Error ?? "Tracking could not be refreshed.");
        }

        // Append only events we have not already stored — carriers commonly resend the whole history.
        var added = 0;
        foreach (var e in tracking.Events)
        {
            var duplicate = shipment.Events.Any(existing =>
                existing.Status == e.Status &&
                Math.Abs((existing.OccurredUtc - e.OccurredUtc).TotalSeconds) < 1);

            if (duplicate)
            {
                continue;
            }

            shipment.Events.Add(new ShipmentEvent
            {
                Status = e.Status,
                Location = e.Location,
                Description = e.Description,
                OccurredUtc = e.OccurredUtc,
                RecordedUtc = DateTime.UtcNow,
                FromCarrier = true
            });

            added++;
        }

        shipment.Status = tracking.Status;
        shipment.LastError = null;

        if (tracking.Status == ShipmentStatus.Delivered)
        {
            shipment.DeliveredUtc ??= DateTime.UtcNow;
        }

        if (shipment.DispatchedUtc is null && tracking.Status != ShipmentStatus.Draft)
        {
            shipment.DispatchedUtc = DateTime.UtcNow;
        }

        await ApplyToOrderAsync(shipment, ct);
        await _db.SaveChangesAsync(ct);

        return FulfilmentResult.Ok(shipment, added > 0
            ? $"Tracking updated — {added} new event(s). Current status: {shipment.Status.ToDisplay()}."
            : $"Tracking is current: {shipment.Status.ToDisplay()}.");
    }

    public async Task<int> RefreshOpenShipmentsAsync(int shopId, CancellationToken ct = default)
    {
        var open = await _db.OrderShipments
            .Where(s => s.ShopId == shopId &&
                        (s.Status == ShipmentStatus.Created || s.Status == ShipmentStatus.PickedUp ||
                         s.Status == ShipmentStatus.InTransit || s.Status == ShipmentStatus.OutForDelivery))
            .Select(s => s.Id)
            .ToListAsync(ct);

        var updated = 0;
        foreach (var id in open)
        {
            var result = await RefreshTrackingAsync(id, ct);
            if (result.Success)
            {
                updated++;
            }
        }

        return updated;
    }

    public async Task<FulfilmentResult> RecordManualEventAsync(
        int shipmentId, ShipmentStatus status, string? location, string? description, CancellationToken ct = default)
    {
        var shipment = await _db.OrderShipments
            .Include(s => s.Events)
            .Include(s => s.Order)
            .FirstOrDefaultAsync(s => s.Id == shipmentId, ct);

        if (shipment is null)
        {
            return FulfilmentResult.Fail("Consignment not found.");
        }

        shipment.Events.Add(new ShipmentEvent
        {
            Status = status,
            Location = location,
            Description = string.IsNullOrWhiteSpace(description)
                ? "Recorded manually by the vendor."
                : description,
            OccurredUtc = DateTime.UtcNow,
            RecordedUtc = DateTime.UtcNow,
            // Flagged so a manual entry is never mistaken for something the carrier told us.
            FromCarrier = false
        });

        shipment.Status = status;

        if (status == ShipmentStatus.Delivered)
        {
            shipment.DeliveredUtc ??= DateTime.UtcNow;
        }

        await ApplyToOrderAsync(shipment, ct);
        await _db.SaveChangesAsync(ct);

        return FulfilmentResult.Ok(shipment, $"Consignment marked as {status.ToDisplay()}.");
    }

    public async Task<FulfilmentResult> CancelShipmentAsync(int shipmentId, CancellationToken ct = default)
    {
        var shipment = await _db.OrderShipments
            .Include(s => s.Events)
            .Include(s => s.Order)
            .FirstOrDefaultAsync(s => s.Id == shipmentId, ct);

        if (shipment is null)
        {
            return FulfilmentResult.Fail("Consignment not found.");
        }

        if (shipment.Status == ShipmentStatus.Delivered)
        {
            return FulfilmentResult.Fail("A delivered consignment cannot be cancelled.");
        }

        if (!string.IsNullOrWhiteSpace(shipment.TrackingNumber))
        {
            var carrier = _carriers.Resolve(shipment.Carrier) ?? _carriers.Default;
            var receipt = await carrier.CancelAsync(shipment.TrackingNumber, ct);

            if (!receipt.Success)
            {
                // Surface the carrier's refusal rather than pretending the cancellation worked.
                shipment.LastError = receipt.Error;
                await _db.SaveChangesAsync(ct);
                return FulfilmentResult.Fail(receipt.Error ?? "The carrier refused the cancellation.");
            }
        }

        shipment.Status = ShipmentStatus.Cancelled;
        shipment.Events.Add(new ShipmentEvent
        {
            Status = ShipmentStatus.Cancelled,
            Description = "Consignment cancelled.",
            OccurredUtc = DateTime.UtcNow,
            RecordedUtc = DateTime.UtcNow,
            FromCarrier = true
        });

        if (shipment.Order is { } order)
        {
            order.Status = OrderStatus.Cancelled;
            order.CancelledUtc = DateTime.UtcNow;
            order.CollectionStatus = CollectionStatus.NotApplicable;
        }

        await _db.SaveChangesAsync(ct);

        return FulfilmentResult.Ok(shipment, "Consignment cancelled.");
    }

    public async Task<IReadOnlyList<OrderShipment>> GetShipmentsForShopAsync(
        int shopId, bool openOnly = false, CancellationToken ct = default)
    {
        IQueryable<OrderShipment> query = _db.OrderShipments.AsNoTracking()
            .Include(s => s.Order)
            .Include(s => s.Events)
            .Where(s => s.ShopId == shopId);

        if (openOnly)
        {
            query = query.Where(s => s.Status == ShipmentStatus.Created || s.Status == ShipmentStatus.PickedUp ||
                                     s.Status == ShipmentStatus.InTransit || s.Status == ShipmentStatus.OutForDelivery);
        }

        return await query.OrderByDescending(s => s.CreatedUtc).Take(200).ToListAsync(ct);
    }

    public Task<OrderShipment?> GetShipmentAsync(int shipmentId, CancellationToken ct = default) =>
        _db.OrderShipments.AsNoTracking()
            .Include(s => s.Order)
            .Include(s => s.Events)
            .FirstOrDefaultAsync(s => s.Id == shipmentId, ct);

    public Task<OrderShipment?> FindByTrackingNumberAsync(string trackingNumber, CancellationToken ct = default) =>
        _db.OrderShipments.AsNoTracking()
            .Include(s => s.Order)
            .ThenInclude(o => o!.Lines)
            .Include(s => s.Events)
            .FirstOrDefaultAsync(s => s.TrackingNumber == trackingNumber, ct);

    /// <summary>
    /// Queues the "your order has shipped" email, swallowing any failure. The consignment is already
    /// booked and saved by this point; a mail problem must not undo it.
    /// </summary>
    private async Task TryNotifyShippedAsync(Order order, CancellationToken ct)
    {
        try
        {
            await _emails.NotifyOrderShippedAsync(order, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not queue the shipping email for order {OrderNumber}", order.OrderNumber);
        }
    }

    /// <summary>
    /// Propagates a consignment state onto its order, and settles the vendor's ledger when the cash
    /// has actually been collected — which for cash on delivery is the moment of delivery, not order
    /// placement.
    /// </summary>
    private async Task ApplyToOrderAsync(OrderShipment shipment, CancellationToken ct)
    {
        if (shipment.Order is not { } order)
        {
            return;
        }

        var implied = shipment.Status.ToOrderStatus();
        if (implied is { } target)
        {
            // Never move an order backwards, and never resurrect a cancelled one.
            if (order.Status != OrderStatus.Cancelled && (int)target > (int)order.Status)
            {
                order.Status = target;
            }
        }

        switch (shipment.Status)
        {
            case ShipmentStatus.Delivered:
                order.DeliveredUtc ??= DateTime.UtcNow;

                if (order.PaymentMethod == PaymentMethod.CashOnDelivery &&
                    order.CollectionStatus != CollectionStatus.Collected)
                {
                    order.CollectionStatus = CollectionStatus.Collected;
                    order.PaidUtc ??= DateTime.UtcNow;

                    await _ledger.RecordSaleAsync(
                        order,
                        $"Order {order.OrderNumber} delivered; cash collected by {shipment.Carrier}.",
                        ct);
                }

                // A delivered cash-on-delivery order is paid, whatever the previous status was.
                if (order.PaymentMethod == PaymentMethod.CashOnDelivery && order.Status != OrderStatus.Cancelled)
                {
                    order.Status = OrderStatus.Delivered;
                }

                break;

            case ShipmentStatus.Failed:
            case ShipmentStatus.Returned:
                if (order.CollectionStatus == CollectionStatus.AwaitingCollection)
                {
                    order.CollectionStatus = CollectionStatus.CollectionFailed;
                }
                break;

            case ShipmentStatus.Cancelled:
                order.CollectionStatus = CollectionStatus.NotApplicable;
                break;
        }

        await Task.CompletedTask;
    }

    /// <summary>Rough per-order weight so a consignment is never booked with zero weight.</summary>
    private static int EstimateWeight(Order order)
    {
        var units = order.Lines.Sum(l => l.Quantity);
        return Math.Max(250, units * 250);
    }
}
