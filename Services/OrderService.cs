using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebDS_Test.Areas.Admin.Models;
using WebDS_Test.Services.Email;

namespace WebDS_Test.Services;

/// <summary>
/// Owns order placement, history and fulfilment. Order creation is the one place where
/// inventory is committed, and it is written to be safe under concurrent checkouts.
/// </summary>
public sealed class OrderService : IOrderService
{
    private const int MaxStockRetries = 3;

    private readonly ApplicationDbContext _db;
    private readonly IPaymentGateway _payments;
    private readonly IEmailService _emails;
    private readonly StoreSettings _settings;
    private readonly ILogger<OrderService> _logger;
    private readonly ICouponService? _coupons;
    private readonly IVendorLedgerService? _ledger;
    private readonly ITaxCalculator _tax;

    public OrderService(
        ApplicationDbContext db,
        IPaymentGateway payments,
        IEmailService emails,
        IOptions<StoreSettings> settings,
        ILogger<OrderService> logger,
        ICouponService? coupons = null,
        IVendorLedgerService? ledger = null,
        ITaxCalculator? tax = null)
    {
        _db = db;
        _payments = payments;
        _emails = emails;
        _settings = settings.Value;
        _logger = logger;
        _coupons = coupons;
        _ledger = ledger;
        _tax = tax ?? new TaxCalculator();
    }

    public async Task<OrderResult> PlaceOrderAsync(CheckoutRequest request, Cart cart, CancellationToken ct = default)
    {
        if (cart.Items.Count == 0)
        {
            return OrderResult.Fail("Your cart is empty.");
        }

        // Idempotency: a resubmitted form returns the order that already exists rather than
        // creating a duplicate.
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var existing = await _db.Orders
                .Include(o => o.Lines)
                .FirstOrDefaultAsync(o => o.IdempotencyKey == request.IdempotencyKey, ct);

            if (existing is not null)
            {
                _logger.LogInformation("Idempotent checkout replay for order {OrderNumber}", existing.OrderNumber);
                return OrderResult.Ok(existing, "This order was already placed.");
            }
        }

        // Re-validate every line against live stock and the shop's trading status: the cart may be
        // hours old, and a shop can be suspended between add-to-cart and checkout.
        foreach (var item in cart.Items)
        {
            var variant = item.ProductVariant;
            if (variant?.Product is null)
            {
                return OrderResult.Fail("An item in your cart is no longer available. Please review your cart.");
            }

            var product = variant.Product;

            if (!variant.IsActive || product.IsDeleted || !product.IsPublished)
            {
                return OrderResult.Fail($"'{product.Name}' is no longer for sale. Please remove it from your cart.");
            }

            if (product.Shop is not { Status: ShopStatus.Approved })
            {
                return OrderResult.Fail(
                    $"'{product.Shop?.Name ?? "A shop"}' is no longer accepting orders. Please remove its items from your cart.");
            }

            var listing = product.Listings.FirstOrDefault(l => l.IsActive);
            if (listing is null)
            {
                return OrderResult.Fail($"'{product.Name}' is no longer offered by this shop.");
            }

            if (listing.StockQuantity < item.Quantity)
            {
                return OrderResult.Fail(listing.StockQuantity <= 0
                    ? $"'{product.Name}' just sold out. Please remove it from your cart."
                    : $"Only {listing.StockQuantity} × '{product.Name}' remain. Please reduce the quantity.");
            }
        }

        // One order per shop. The cart already refuses to mix shops, but the split is enforced here
        // too so the invariant holds however the cart was assembled.
        var byShop = cart.Items
            .GroupBy(i => i.ProductVariant!.Product!.ShopId)
            .ToList();

        if (byShop.Count == 0)
        {
            return OrderResult.Fail("Your cart is empty.");
        }

        for (var attempt = 1; attempt <= MaxStockRetries; attempt++)
        {
            // Each attempt starts from a clean change tracker and a freshly loaded cart, so a
            // retry after a stock conflict sees current prices, stock and row versions rather
            // than the stale entities read at the start of the request.
            if (attempt > 1)
            {
                _db.ChangeTracker.Clear();

                var reloaded = await ReloadCartAsync(cart.Id, ct);
                if (reloaded is null || reloaded.Items.Count == 0)
                {
                    return OrderResult.Fail("Your cart changed while you were checking out. Please review it and try again.");
                }

                cart = reloaded;
                byShop = cart.Items.GroupBy(i => i.ProductVariant!.Product!.ShopId).ToList();
            }

            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            try
            {
                var placed = new List<Order>();
                var shopIndex = 0;

                foreach (var group in byShop)
                {
                    var items = group.ToList();
                    var shop = items[0].ProductVariant!.Product!.Shop;

                    var subtotal = items.Sum(i => i.UnitPrice * i.Quantity);
                    var shipping = CalculateShipping(subtotal, shop);
                    var tax = CalculateTax(subtotal);
                    var total = Round(subtotal + shipping + tax);

                    // The idempotency key is unique across orders, so only the first order of a
                    // multi-shop checkout carries the bare key; the rest are suffixed. Replay
                    // detection only needs to find the first one.
                    var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
                        ? null
                        : shopIndex == 0 ? request.IdempotencyKey : $"{request.IdempotencyKey}#{shopIndex}";

                    var shopSubtotal = subtotal;
                    decimal discount = 0m;
                    int? couponId = null;
                    string? couponCode = null;
                    if (!string.IsNullOrWhiteSpace(request.CouponCode) && _coupons is not null && shopIndex == 0)
                    {
                        var c = await _coupons.ValidateAndComputeAsync(request.CouponCode, cart, cart.UserId, ct);
                        if (c.Success)
                        {
                            discount = c.Discount;
                            couponId = c.CouponId;
                            couponCode = c.CouponCode;
                        }
                    }

                    subtotal = shopSubtotal - discount;
                    if (subtotal < 0m) subtotal = 0m;
                    shipping = CalculateShipping(subtotal, shop);

                    // Tax is computed per line, from each line's own rate, with the order-level
                    // discount spread across the lines in proportion to their value. That way a basket
                    // holding a zero-rated item and a standard-rated one is taxed correctly, and a
                    // single-rate catalogue still produces exactly the figure it always did.
                    tax = CalculateTaxForItems(items, shopSubtotal, discount);
                    total = Round(subtotal + shipping + tax);

                    var order = new Order
                    {
                        ShopId = group.Key,
                        OrderNumber = await GenerateOrderNumberAsync(ct),
                        UserId = cart.UserId,
                        Email = request.Email.Trim(),
                        CustomerName = request.CustomerName.Trim(),
                        Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim(),
                        Status = OrderStatus.Pending,
                        // Cash on delivery is the marketplace default and collects nothing up front;
                        // a card order is paid on the provider's page and confirmed by webhook, so it
                        // has no cash to collect.
                        PaymentMethod = request.PaymentMethod,
                        CollectionStatus = request.PaymentMethod == PaymentMethod.CashOnDelivery
                            ? CollectionStatus.AwaitingCollection
                            : CollectionStatus.NotApplicable,
                        CodAmount = request.PaymentMethod == PaymentMethod.CashOnDelivery ? total : 0m,
                        IdempotencyKey = idempotencyKey,
                        Subtotal = Round(subtotal),
                        ShippingTotal = shipping,
                        TaxTotal = tax,
                        DiscountTotal = Round(discount),
                        GrandTotal = total,
                        CouponCode = couponCode,
                        CouponId = couponId,
                        ShippingName = request.ShippingName.Trim(),
                        ShippingLine1 = request.ShippingLine1.Trim(),
                        ShippingLine2 = string.IsNullOrWhiteSpace(request.ShippingLine2) ? null : request.ShippingLine2.Trim(),
                        ShippingCity = request.ShippingCity.Trim(),
                        ShippingState = string.IsNullOrWhiteSpace(request.ShippingState) ? null : request.ShippingState.Trim(),
                        ShippingPostalCode = request.ShippingPostalCode.Trim(),
                        ShippingCountry = request.ShippingCountry.Trim(),
                        CustomerNote = string.IsNullOrWhiteSpace(request.CustomerNote) ? null : request.CustomerNote.Trim(),
                        PlacedUtc = DateTime.UtcNow
                    };

                    foreach (var item in items)
                    {
                        var variant = item.ProductVariant!;
                        var listing = variant.Product!.Listings.First(l => l.IsActive);
                        var image = variant.Product.Images
                            .OrderByDescending(i => i.IsPrimary)
                            .ThenBy(i => i.DisplayOrder)
                            .FirstOrDefault();

                        order.Lines.Add(new OrderLine
                        {
                            ProductId = variant.ProductId,
                            ProductVariantId = variant.Id,
                            ProductName = variant.Product.Name,
                            VariantName = variant.Name,
                            Sku = listing.Sku ?? variant.Sku,
                            ImageUrl = image?.Url,
                            Quantity = item.Quantity,
                            // The shop's price is what the shopper was quoted.
                            UnitPrice = listing.Price ?? variant.Price,
                            LineTotal = Round((listing.Price ?? variant.Price) * item.Quantity)
                        });
                    }

                    _db.Orders.Add(order);

                    // Commit this shop's stock. The listing carries the concurrency token, so EF
                    // includes its RowVersion in the UPDATE ... WHERE clause and a concurrent sale
                    // raises a concurrency exception rather than overselling.
                    foreach (var item in items)
                    {
                        var variant = item.ProductVariant!;
                        var listing = variant.Product!.Listings.First(l => l.IsActive);

                        listing.StockQuantity -= item.Quantity;
                        listing.UpdatedUtc = DateTime.UtcNow;

                        _db.StockMovements.Add(new StockMovement
                        {
                            ProductVariantId = variant.Id,
                            QuantityDelta = -item.Quantity,
                            StockAfter = listing.StockQuantity,
                            Reason = StockMovementReason.Sale,
                            Note = $"Order {order.OrderNumber} ({shop?.Name})",
                            CreatedBy = order.UserId ?? "guest",
                            CreatedUtc = DateTime.UtcNow
                        });
                    }

                    placed.Add(order);
                    shopIndex++;
                }

                await _db.SaveChangesAsync(ct);

                // Record the payment instruction. Cash on delivery is a pending collection — no money
                // moves here; the courier collects and FulfilmentService settles the vendor on
                // delivery. A card order records the hosted provider, which the webhook will settle.
                foreach (var order in placed)
                {
                    var cod = order.PaymentMethod == PaymentMethod.CashOnDelivery;

                    _db.Payments.Add(new Payment
                    {
                        OrderId = order.Id,
                        Provider = cod ? "cash-on-delivery" : _payments.ProviderName,
                        TransactionId = null,
                        Amount = order.GrandTotal,
                        Currency = _settings.Currency,
                        Status = PaymentStatus.Pending,
                        IsTest = cod ? false : _payments.IsSandbox,
                        CreatedUtc = DateTime.UtcNow
                    });
                }

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                if (placed.Count > 0 && !string.IsNullOrWhiteSpace(request.CouponCode) && _coupons is not null)
                {
                    var first = placed[0];
                    if (first.CouponId.HasValue)
                    {
                        await _coupons.RedeemAsync(first.CouponId.Value, first.Id, first.UserId, ct);
                    }
                }

                var summary = string.Join(", ", placed.Select(o => o.OrderNumber));

                _logger.LogInformation(
                    "Placed {Count} order(s) for {Email} totalling {Total}: {Orders}",
                    placed.Count, request.Email, placed.Sum(o => o.GrandTotal), summary);

                // The order is durable; the confirmation is queued afterwards, outside the
                // transaction. A mail problem must never roll back an order the shopper placed.
                foreach (var order in placed)
                {
                    await TryNotifyAsync(order, "order confirmation",
                        () => _emails.NotifyOrderPlacedAsync(order, ct));
                }

                var closing = placed.All(o => o.PaymentMethod == PaymentMethod.CashOnDelivery)
                    ? "Payment is collected on delivery."
                    : "Complete payment to confirm your order.";

                return OrderResult.Ok(placed,
                    placed.Count == 1
                        ? $"Order {placed[0].OrderNumber} confirmed. {closing}"
                        : $"{placed.Count} orders placed ({summary}). {closing}");
            }
            catch (DbUpdateConcurrencyException ex)
            {
                await tx.RollbackAsync(ct);
                _logger.LogWarning(ex, "Stock conflict while placing an order (attempt {Attempt})", attempt);

                if (attempt == MaxStockRetries)
                {
                    return OrderResult.Fail("Someone else bought the last of an item while you were checking out. Please review your cart.");
                }

                // The next iteration clears the change tracker and reloads the cart.
            }
            catch (DbUpdateException ex)
            {
                await tx.RollbackAsync(ct);
                _logger.LogWarning(ex, "Order insert failed (attempt {Attempt})", attempt);

                if (attempt == MaxStockRetries)
                {
                    return OrderResult.Fail("We could not place your order. Please try again.");
                }

                _db.ChangeTracker.Clear();
            }
        }

        return OrderResult.Fail("We could not place your order. Please try again.");
    }

    public async Task<IReadOnlyList<Order>> GetOrdersForUserAsync(string userId, CancellationToken ct = default) =>
        await _db.Orders.AsNoTracking()
            .Include(o => o.Lines)
            .Where(o => o.UserId == userId)
            .OrderByDescending(o => o.PlacedUtc)
            .ToListAsync(ct);

    public Task<Order?> GetOrderAsync(int id, string? userId = null, CancellationToken ct = default) =>
        _db.Orders.AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == id && (userId == null || o.UserId == userId), ct);

    public Task<Order?> GetByNumberAsync(string orderNumber, string? userId = null, CancellationToken ct = default) =>
        _db.Orders.AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber && (userId == null || o.UserId == userId), ct);

    public async Task<IReadOnlyList<Order>> GetAllAsync(
        OrderStatus? status = null, string? search = null, int take = 100, CancellationToken ct = default)
    {
        IQueryable<Order> q = _db.Orders.AsNoTracking().Include(o => o.Lines);

        if (status is { } s)
        {
            q = q.Where(o => o.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim().Replace("%", "\\%").Replace("_", "\\_")}%";
            q = q.Where(o =>
                EF.Functions.Like(o.OrderNumber, term, "\\") ||
                EF.Functions.Like(o.Email, term, "\\") ||
                EF.Functions.Like(o.CustomerName, term, "\\"));
        }

        return await q.OrderByDescending(o => o.PlacedUtc).Take(take).ToListAsync(ct);
    }

    public async Task<OrderResult> UpdateStatusAsync(
        int orderId, OrderStatus target, string? trackingNumber = null, CancellationToken ct = default)
    {
        var order = await _db.Orders
            .Include(o => o.Lines)
            .ThenInclude(l => l.ProductVariant)
            // The variant's product and its listings are needed by the cancellation restock: stock
            // is returned to the shop's listing when the product is still listed there.
            .ThenInclude(v => v!.Product)
            .ThenInclude(p => p!.Listings)
            // Payments are needed for the Refunded transition below: it walks the successful ones to
            // reverse them. Without this include the collection is empty and nothing is ever refunded.
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

        if (order is null)
        {
            return OrderResult.Fail("Order not found.");
        }

        if (order.Status == target)
        {
            return OrderResult.Ok(order, "Order is already in that state.");
        }

        if (!order.Status.AllowedTransitions().Contains(target))
        {
            return OrderResult.Fail($"An order that is '{order.Status.ToDisplay()}' cannot move to '{target.ToDisplay()}'.");
        }

        var now = DateTime.UtcNow;
        var refundedNow = 0m;

        switch (target)
        {
            case OrderStatus.Paid:
                order.PaidUtc = now;

                // Money has changed hands, so this is the moment the vendor's earning exists.
                if (_ledger is not null)
                {
                    await _ledger.RecordSaleAsync(order, "Order marked paid.", ct);
                }

                break;

            case OrderStatus.Shipped:
                order.ShippedUtc = now;
                if (!string.IsNullOrWhiteSpace(trackingNumber))
                {
                    order.TrackingNumber = trackingNumber.Trim();
                }
                break;

            case OrderStatus.Delivered:
                order.DeliveredUtc = now;
                break;

            case OrderStatus.Cancelled:
            case OrderStatus.Refunded:
                // Returning stock is only correct for a cancellation. Once goods have shipped,
                // a refund leaves inventory alone unless they come back as a return.
                if (target == OrderStatus.Cancelled && order.Status.HoldsStock())
                {
                    foreach (var line in order.Lines.Where(l => l.ProductVariant is not null))
                    {
                        var variant = line.ProductVariant!;

                        // Stock is held on the shop's listing — the same pool the sale committed
                        // from. Only when the product is no longer listed (its listing pool is
                        // gone) does the variant's legacy stock figure take the units back, which
                        // keeps the movement meaningful for a product that is still sellable
                        // somewhere rather than silently dropping them.
                        var listing = variant.Product?.Listings
                            .FirstOrDefault(l => l.IsActive && l.ShopId == order.ShopId);

                        int stockAfter;
                        if (listing is not null)
                        {
                            listing.StockQuantity += line.Quantity;
                            listing.UpdatedUtc = now;
                            stockAfter = listing.StockQuantity;
                        }
                        else
                        {
                            variant.StockQuantity += line.Quantity;
                            stockAfter = variant.StockQuantity;
                        }

                        _db.StockMovements.Add(new StockMovement
                        {
                            ProductVariantId = variant.Id,
                            QuantityDelta = line.Quantity,
                            StockAfter = stockAfter,
                            Reason = StockMovementReason.Cancellation,
                            Note = $"Order {order.OrderNumber} cancelled",
                            CreatedUtc = now
                        });
                    }
                }

                if (target == OrderStatus.Refunded)
                {
                    // Return requests may already have refunded part of this order; the manual
                    // refund covers only what is still owed, so the two paths can never
                    // double-pay the shopper.
                    var remainingRefundable = order.GrandTotal - order.RefundedTotal;
                    var succeeded = order.Payments
                        .Where(p => p.Status == PaymentStatus.Succeeded)
                        .ToList();

                    foreach (var payment in succeeded)
                    {
                        var slice = Math.Min(remainingRefundable, payment.Amount);
                        if (slice > 0m)
                        {
                            await _payments.RefundAsync(payment.TransactionId, slice, ct);
                            refundedNow += slice;
                            order.RefundedTotal += slice;
                            remainingRefundable -= slice;
                        }

                        // The charge row is marked refunded only when it is refunded in full; a
                        // partially refunded charge stays 'Succeeded' and the refund rows plus
                        // RefundedTotal carry the rest of the story.
                        if (slice == payment.Amount)
                        {
                            payment.Status = PaymentStatus.Refunded;
                            payment.CompletedUtc = now;
                        }
                    }
                }

                order.CancelledUtc = now;
                break;
        }

        order.Status = target;

        // A cancellation or refund takes back whatever the sale credited. Both are safe to call when
        // nothing was credited: a cancellation of an unpaid order simply has nothing to reverse.
        if (_ledger is not null && target is OrderStatus.Cancelled or OrderStatus.Refunded)
        {
            await _ledger.RecordReversalAsync(
                order,
                target == OrderStatus.Refunded
                    ? $"Order {order.OrderNumber} refunded to the shopper."
                    : $"Order {order.OrderNumber} cancelled.",
                ct);
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Order {OrderNumber} moved to {Status}", order.OrderNumber, target);

        // Two transitions are worth telling the shopper about. Everything else is internal.
        switch (target)
        {
            case OrderStatus.Shipped:
                await TryNotifyAsync(order, "shipping", () => _emails.NotifyOrderShippedAsync(order, ct));
                break;

            case OrderStatus.Cancelled:
                await TryNotifyAsync(order, "cancellation", () => _emails.NotifyOrderCancelledAsync(order, ct));
                break;

            case OrderStatus.Refunded:
                if (refundedNow > 0m)
                {
                    await TryNotifyAsync(order, "refund",
                        () => _emails.NotifyRefundIssuedAsync(order, refundedNow, ct));
                }
                break;
        }

        return OrderResult.Ok(order, $"Order {order.OrderNumber} is now '{target.ToDisplay()}'.");
    }

    /// <summary>
    /// Captures a hosted payment. Written to be safe to call twice: a provider's retry (or a
    /// redelivery after a timeout) must not create a second payment or re-date a paid order.
    /// </summary>
    public async Task<OrderResult> MarkPaidAsync(
        int orderId, string provider, string? transactionId, decimal amount, CancellationToken ct = default)
    {
        var order = await _db.Orders
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

        if (order is null)
        {
            return OrderResult.Fail("Order not found.");
        }

        // A cancelled or refunded order must not be quietly revived by a late capture.
        if (order.Status is OrderStatus.Cancelled or OrderStatus.Refunded)
        {
            return OrderResult.Fail(
                $"Order {order.OrderNumber} is {order.Status.ToDisplay()} and cannot be marked paid.");
        }

        // Already captured (or further along): a replayed event is a no-op, not a second payment row.
        if (order.Status != OrderStatus.Pending)
        {
            return OrderResult.Ok(order, $"Order {order.OrderNumber} is already {order.Status.ToDisplay()}.");
        }

        var payment = order.Payments.FirstOrDefault(p => p.Provider == provider && p.Status == PaymentStatus.Pending)
                      ?? order.Payments.FirstOrDefault(p => p.Status == PaymentStatus.Pending);

        if (payment is null)
        {
            // The provider reported a payment the store has no record of — record it rather than
            // lose the capture.
            payment = new Payment
            {
                OrderId = order.Id,
                Provider = provider,
                Amount = amount > 0m ? amount : order.GrandTotal,
                Currency = _settings.Currency
            };
            order.Payments.Add(payment);
        }

        payment.Provider = provider;
        payment.TransactionId = transactionId;
        payment.Status = PaymentStatus.Succeeded;
        payment.IsTest = _payments.IsSandbox;
        payment.FailureReason = null;
        payment.CompletedUtc = DateTime.UtcNow;

        order.Status = OrderStatus.Paid;
        order.PaidUtc = DateTime.UtcNow;

        // Prepaid money is collected here, so this is the moment the vendor is owed.
        if (_ledger is not null)
        {
            await _ledger.RecordSaleAsync(order, $"Order {order.OrderNumber} paid by card via {provider}.", ct);
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Order {OrderNumber} captured via {Provider} ({TransactionId})",
            order.OrderNumber, provider, transactionId ?? "no reference");

        return OrderResult.Ok(order, $"Order {order.OrderNumber} is paid.");
    }

    /// <summary>Records a declined hosted payment and leaves the order pending so it can be retried.</summary>
    public async Task<OrderResult> MarkPaymentFailedAsync(
        int orderId, string provider, string? reason, CancellationToken ct = default)
    {
        var order = await _db.Orders
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

        if (order is null)
        {
            return OrderResult.Fail("Order not found.");
        }

        if (order.Status != OrderStatus.Pending)
        {
            // Already paid or further along: a decline that arrives afterwards is stale, not news.
            return OrderResult.Ok(order, $"Order {order.OrderNumber} is already {order.Status.ToDisplay()}.");
        }

        var payment = order.Payments.FirstOrDefault(p => p.Provider == provider && p.Status == PaymentStatus.Pending)
                      ?? order.Payments.FirstOrDefault(p => p.Status == PaymentStatus.Pending);

        if (payment is not null)
        {
            payment.Status = PaymentStatus.Failed;
            payment.FailureReason = Truncate(reason, 500) ?? "The payment was declined.";
            payment.CompletedUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Payment for order {OrderNumber} was declined", order.OrderNumber);

        return OrderResult.Ok(order, $"Payment for order {order.OrderNumber} was declined.");
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    /// <summary>
    /// Queues a notification, swallowing any failure. A status change or a placed order has already
    /// been committed by the time this runs, so an email problem must be logged and moved past — not
    /// surfaced to the caller as a failed operation.
    /// </summary>
    private async Task TryNotifyAsync(Order order, string what, Func<Task> notify)
    {
        try
        {
            await notify();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Could not queue the {What} email for order {OrderNumber}", what, order.OrderNumber);
        }
    }

    public async Task<OrderDashboardStats> GetDashboardStatsAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);

        var recent = await _db.Orders.AsNoTracking()
            .Include(o => o.Lines)
            .OrderByDescending(o => o.PlacedUtc)
            .Take(8)
            .ToListAsync(ct);

        // Statuses that represent money actually collected.
        var revenueStatuses = new[] { OrderStatus.Paid, OrderStatus.Shipped, OrderStatus.Delivered };

        // Grouping server-side and casting the money column to double keeps this to a single
        // query; SQLite cannot aggregate a decimal (see CatalogService.GetPriceBoundsAsync).
        var perStatus = await _db.Orders.AsNoTracking()
            .GroupBy(o => o.Status)
            .Select(g => new
            {
                Status = g.Key,
                Count = g.Count(),
                Revenue = (double)g.Sum(o => o.GrandTotal)
            })
            .ToListAsync(ct);

        // EF.Constant keeps this a literal IN (…); a captured collection would become OPENJSON,
        // which SQL Server 2014 does not support. See CatalogService.BrowseAsync.
        var lifetime = await _db.Orders.AsNoTracking()
            .Where(o => EF.Constant(revenueStatuses).Contains(o.Status))
            .Select(o => new { o.GrandTotal, o.PlacedUtc })
            .ToListAsync(ct);

        var variants = await _db.ProductVariants.AsNoTracking()
            .Include(v => v.Product)
            .Where(v => v.IsActive)
            .Select(v => new
            {
                v.StockQuantity,
                v.LowStockThreshold,
                Price = (double)v.Price,
                ProductName = v.Product!.Name,
                v.Sku
            })
            .ToListAsync(ct);

        int CountOf(OrderStatus status) => perStatus.FirstOrDefault(s => s.Status == status)?.Count ?? 0;

        return new OrderDashboardStats
        {
            TotalOrders = perStatus.Sum(s => s.Count),
            PendingOrders = CountOf(OrderStatus.Pending),
            PaidOrders = CountOf(OrderStatus.Paid),
            ShippedOrders = CountOf(OrderStatus.Shipped),
            Revenue = lifetime.Sum(o => o.GrandTotal),
            RevenueLast30Days = lifetime.Where(o => o.PlacedUtc >= cutoff).Sum(o => o.GrandTotal),
            OrdersLast30Days = await _db.Orders.CountAsync(o => o.PlacedUtc >= cutoff, ct),
            LowStockVariants = variants.Count(v => v.StockQuantity > 0 && v.StockQuantity <= v.LowStockThreshold),
            OutOfStockVariants = variants.Count(v => v.StockQuantity <= 0),
            ProductCount = await _db.Products.CountAsync(p => !p.IsDeleted, ct),
            CustomerCount = await _db.Users.CountAsync(ct),
            InventoryValue = variants.Sum(v => (decimal)v.Price * v.StockQuantity),
            LowStock = variants
                .Where(v => v.StockQuantity <= v.LowStockThreshold)
                .OrderBy(v => v.StockQuantity)
                .Take(8)
                .Select(v => new LowStockRow
                {
                    Sku = v.Sku,
                    ProductName = v.ProductName,
                    StockQuantity = v.StockQuantity,
                    LowStockThreshold = v.LowStockThreshold
                })
                .ToList(),
            RecentOrders = recent
        };
    }

    /// <summary>
    /// Marketing-focused dashboard figures: revenue trend, best sellers, category mix, order
    /// funnel, customer acquisition, payment methods and coupon performance. All figures are
    /// computed over completed orders (Paid/Shipped/Delivered) unless noted otherwise.
    /// </summary>
    public async Task<MarketingDashboardStats> GetMarketingDashboardAsync(CancellationToken ct = default)
    {
        var cutoff30 = DateTime.UtcNow.AddDays(-30);

        // ---- Orders ---------------------------------------------------------
        // Completed orders (money actually collected) for revenue figures; all orders for the funnel.
        var completedStatuses = new[] { OrderStatus.Paid, OrderStatus.Shipped, OrderStatus.Delivered };
        var allOrders = await _db.Orders.AsNoTracking()
            .Include(o => o.Lines)
            .ThenInclude(l => l.ProductVariant!)
                .ThenInclude(v => v!.Product)
                    .ThenInclude(p => p!.Category)
            .OrderByDescending(o => o.PlacedUtc)
            .ToListAsync(ct);

        var completedOrders = allOrders.Where(o => completedStatuses.Contains(o.Status)).ToList();
        var last30Orders = completedOrders.Where(o => o.PlacedUtc >= cutoff30).ToList();

        var totalRevenue = completedOrders.Sum(o => o.GrandTotal);
        var revenueLast30 = last30Orders.Sum(o => o.GrandTotal);
        var totalOrders = allOrders.Count;
        var ordersLast30 = last30Orders.Count;

        // ---- Daily revenue trend (last 30 days, including zero days) -------
        var daily = new List<DailyRevenue>();
        for (int i = 29; i >= 0; i--)
        {
            var day = DateTime.UtcNow.Date.AddDays(-i);
            var dayOrders = completedOrders
                .Where(o => o.PlacedUtc.Date == day.Date)
                .ToList();
            daily.Add(new DailyRevenue
            {
                Date = day,
                Revenue = dayOrders.Sum(o => o.GrandTotal),
                Orders = dayOrders.Count
            });
        }

        // ---- Status breakdown ----------------------------------------------
        var statusLabels = new Dictionary<OrderStatus, string>
        {
            { OrderStatus.Pending, "Pending" },
            { OrderStatus.Paid, "Paid" },
            { OrderStatus.Shipped, "Shipped" },
            { OrderStatus.Delivered, "Delivered" },
            { OrderStatus.Cancelled, "Cancelled" },
            { OrderStatus.Refunded, "Refunded" }
        };
        var statusCss = new Dictionary<OrderStatus, string>
        {
            { OrderStatus.Pending, "text-bg-secondary" },
            { OrderStatus.Paid, "text-bg-info" },
            { OrderStatus.Shipped, "text-bg-primary" },
            { OrderStatus.Delivered, "text-bg-success" },
            { OrderStatus.Cancelled, "text-bg-danger" },
            { OrderStatus.Refunded, "text-bg-warning" }
        };
        var statusBreakdown = allOrders
            .GroupBy(o => o.Status)
            .Select(g => new OrderStatusCount
            {
                Status = g.Key,
                Count = g.Count(),
                Revenue = g.Where(o => completedStatuses.Contains(o.Status)).Sum(o => o.GrandTotal),
                Label = statusLabels.TryGetValue(g.Key, out var l) ? l : g.Key.ToString(),
                CssClass = statusCss.TryGetValue(g.Key, out var c) ? c : "text-bg-secondary"
            })
            .OrderByDescending(s => s.Count)
            .ToList();

        var cancellationRate = totalOrders > 0
            ? (decimal)allOrders.Count(o => o.Status == OrderStatus.Cancelled) / totalOrders * 100m
            : 0m;
        var refundRate = totalOrders > 0
            ? (decimal)allOrders.Count(o => o.Status == OrderStatus.Refunded) / totalOrders * 100m
            : 0m;

        // ---- Product sales ranking -----------------------------------------
        // Every line is counted, including one whose variant has since been removed: an order line is
        // a record of what was sold, and filtering it out would quietly delete real revenue from the
        // report. The name comes from the snapshot on the line, which a later catalogue edit cannot
        // rewrite, rather than from the live product.
        var productSales = completedOrders
            .SelectMany(o => o.Lines.Select(l => new
                {
                    OrderId = o.Id,
                    o.GrandTotal,
                    ProductId = l.ProductId ?? l.ProductVariant?.ProductId ?? 0,
                    ProductName = string.IsNullOrWhiteSpace(l.ProductName) ? "(removed product)" : l.ProductName,
                    l.Sku,
                    Category = l.ProductVariant?.Product?.Category?.Name ?? "Uncategorised",
                    l.Quantity,
                    LineTotal = l.LineTotal
                }))
            .GroupBy(x => new { x.ProductId, x.ProductName, x.Sku, x.Category })
            .Select(g => new ProductSalesRank
            {
                ProductId = g.Key.ProductId,
                Name = g.Key.ProductName,
                Sku = g.Key.Sku,
                Category = g.Key.Category,
                Units = g.Sum(x => x.Quantity),
                Orders = g.Select(x => x.OrderId).Distinct().Count(),
                Revenue = g.Sum(x => x.LineTotal)
            })
            .ToList();

        var topByRevenue = productSales.OrderByDescending(p => p.Revenue).ThenByDescending(p => p.Units).Take(10).ToList();
        var topByUnits = productSales.OrderByDescending(p => p.Units).ThenByDescending(p => p.Revenue).Take(10).ToList();

        // ---- Category revenue ----------------------------------------------
        // No slug: OrderLine snapshots the product, not the category tree, so the only slug
        // reachable from an order is the *product's*. Reporting that under a category name
        // would be a lie that looks like a link, so the column simply does not exist.
        var categorySales = productSales
            .GroupBy(p => new { p.Category })
            .Select(g => new CategorySales
            {
                Name = g.Key.Category,
                Products = g.Select(p => p.ProductId).Distinct().Count(),
                Orders = g.Sum(p => p.Orders),
                Units = g.Sum(p => p.Units),
                Revenue = g.Sum(p => p.Revenue)
            })
            .OrderByDescending(c => c.Revenue)
            .ToList();

        // ---- Customer acquisition ------------------------------------------
        var customers = allOrders
            .Where(o => !string.IsNullOrWhiteSpace(o.UserId))
            .GroupBy(o => new { o.UserId, o.Email, o.CustomerName })
            .Select(g => new
            {
                UserId = g.Key.UserId!,
                Email = g.Key.Email ?? string.Empty,
                Name = g.Key.CustomerName ?? string.Empty,
                Orders = g.Count(),
                Spend = g.Where(o => completedStatuses.Contains(o.Status)).Sum(o => o.GrandTotal),
                LastOrder = g.Max(o => o.PlacedUtc)
            })
            .ToList();

        var newCustomersLast30 = customers.Count(c => c.LastOrder >= cutoff30 && c.Orders == 1);
        var returningCustomersLast30 = customers.Count(c => c.LastOrder >= cutoff30 && c.Orders > 1);

        var topCustomers = customers
            .OrderByDescending(c => c.Spend)
            .Take(10)
            .Select(c => new CustomerRank
            {
                UserId = c.UserId,
                Name = c.Name,
                Email = c.Email,
                Orders = c.Orders,
                Spend = c.Spend,
                LastOrder = c.LastOrder
            })
            .ToList();

        // ---- Payment methods -----------------------------------------------
        var paymentMethods = completedOrders
            .GroupBy(o => o.PaymentMethod)
            .Select(g => new PaymentMethodBreakdown
            {
                Method = g.Key,
                Label = g.Key.ToString(),
                Orders = g.Count(),
                RevenueRaw = g.Sum(o => o.GrandTotal)
            })
            .OrderByDescending(p => p.RevenueRaw)
            .ToList();

        // ---- Coupon performance --------------------------------------------
        var coupons = await _db.Coupons.AsNoTracking().ToListAsync(ct);
        var couponUses = allOrders
            .Where(o => !string.IsNullOrWhiteSpace(o.CouponCode))
            .Select(o => new CouponUse
            {
                OrderNumber = o.OrderNumber,
                Customer = string.IsNullOrWhiteSpace(o.CustomerName) ? o.Email : o.CustomerName,
                OrderTotal = o.GrandTotal,
                Discount = o.DiscountTotal,
                PlacedUtc = o.PlacedUtc,
                CouponCode = o.CouponCode!
            })
            .ToList();

        var couponPerformance = coupons.Select(c =>
        {
            var uses = couponUses.Where(u => u.CouponCode == c.Code).ToList();
            return new CouponPerformance
            {
                CouponId = c.Id,
                Code = c.Code,
                MaxUses = c.MaxUses ?? int.MaxValue,
                // The discount actually applied, as checkout recorded it. Reconstructing it from the
                // coupon's own rules would be wrong twice over: the percentage was taken from the
                // pre-discount subtotal, and a fixed amount is an amount, not a fraction of the
                // total. Order.DiscountTotal is the one figure that cannot disagree with the order.
                DiscountGiven = uses.Sum(u => u.Discount),
                InfluencedRevenue = uses.Sum(u => u.OrderTotal),
                Uses = uses
            };
        }).Where(c => c.Uses.Count > 0 || c.MaxUses < int.MaxValue)
        .OrderByDescending(c => c.InfluencedRevenue)
        .ToList();

        // ---- Fast-moving variants ------------------------------------------
        var variants = await _db.ProductVariants.AsNoTracking()
            .Include(v => v.Product)
            .Where(v => v.IsActive)
            .ToListAsync(ct);

        var fastMoving = completedOrders
            .SelectMany(o => o.Lines.Where(l => l.ProductVariant is not null)
                .Select(l => new
                {
                    VariantId = l.ProductVariant!.Id,
                    ProductName = l.ProductVariant.Product!.Name,
                    VariantSku = l.Sku,
                    Quantity = l.Quantity,
                    OrderId = o.Id
                }))
            .GroupBy(x => new { x.VariantId, x.ProductName, x.VariantSku })
            .Select(g => new
            {
                g.Key.VariantId,
                g.Key.ProductName,
                g.Key.VariantSku,
                Units = g.Sum(x => x.Quantity),
                Orders = g.Select(x => x.OrderId).Distinct().Count()
            })
            .Join(variants, f => f.VariantId, v => v.Id, (f, v) => new FastMovingVariant
            {
                ProductId = v.ProductId,
                ProductName = f.ProductName,
                VariantSku = f.VariantSku,
                Units = f.Units,
                Orders = f.Orders,
                StockRemaining = v.StockQuantity
            })
            .OrderByDescending(f => f.Units)
            .Take(10)
            .ToList();

        return new MarketingDashboardStats
        {
            DailyRevenue = daily,
            TotalRevenue = totalRevenue,
            RevenueLast30Days = revenueLast30,
            TotalOrders = totalOrders,
            OrdersLast30Days = ordersLast30,
            AverageOrderValue = completedOrders.Count > 0 ? totalRevenue / completedOrders.Count : 0m,
            AverageOrderValueLast30Days = last30Orders.Count > 0 ? revenueLast30 / last30Orders.Count : 0m,
            StatusBreakdown = statusBreakdown,
            CancellationRate = cancellationRate,
            RefundRate = refundRate,
            TopProductsByRevenue = topByRevenue,
            TopProductsByUnits = topByUnits,
            RevenueByCategory = categorySales,
            NewCustomersLast30Days = newCustomersLast30,
            ReturningCustomersLast30Days = returningCustomersLast30,
            TopCustomers = topCustomers,
            PaymentMethods = paymentMethods,
            CouponPerformance = couponPerformance,
            FastMoving = fastMoving
        };
    }

    public decimal CalculateShipping(decimal subtotal, Shop? shop = null)
    {
        var freeThreshold = shop?.FreeShippingThreshold ?? _settings.FreeShippingThreshold;
        var flatRate = shop?.FlatShippingRate ?? _settings.FlatShippingRate;
        if (subtotal <= 0m || subtotal >= freeThreshold) return 0m;
        return flatRate;
    }

    public decimal CalculateTax(decimal subtotal) => Round(subtotal * _settings.TaxRate);

    /// <summary>
    /// Tax for a shop's basket when the products may carry different tax classes.
    /// </summary>
    /// <remarks>
    /// The discount is apportioned across lines by value rather than taxed separately, because a coupon
    /// applies to the goods subtotal as a whole: taxing the pre-discount value and then ignoring the
    /// discount would overcharge every discounted order, and taxing only the discounted total at one
    /// flat rate would undercharge a mixed basket. Each line's share is rounded per line, matching how
    /// a real invoice is produced, and the parts are then summed.
    /// </remarks>
    private decimal CalculateTaxForItems(IReadOnlyList<CartItem> items, decimal goodsSubtotal, decimal discount)
    {
        var lines = items.Select(item =>
        {
            var variant = item.ProductVariant;
            var listing = variant?.Product?.Listings.FirstOrDefault(l => l.IsActive);
            var gross = (listing?.Price ?? variant?.Price ?? 0m) * item.Quantity;

            return new TaxableLine(gross, variant?.Product?.TaxClass);
        }).ToList();

        return _tax.TaxForMixedBasket(lines, discount, _settings.TaxRate, _settings.Rounding);
    }

    /// <summary>
    /// Re-reads a cart and its lines with fresh variants after a rolled-back attempt. Uses the
    /// same eager-loading shape as the original checkout read so <c>UnitPrice</c>, product names
    /// and images are all available for building the order lines.
    /// </summary>
    private Task<Cart?> ReloadCartAsync(int cartId, CancellationToken ct) =>
        _db.Carts
            .Include(c => c.Items)
                .ThenInclude(i => i.ProductVariant)
                    .ThenInclude(v => v!.Product)
                        .ThenInclude(p => p!.Images)
            .FirstOrDefaultAsync(c => c.Id == cartId, ct);

    private decimal Round(decimal value) => Math.Round(value, 2, _settings.Rounding);

    /// <summary>
    /// Produces the next sequential order number for the current year, e.g. <c>ORD-2026-0007</c>.
    /// The unique index on <c>OrderNumber</c> is the real guard; a collision is retried.
    /// </summary>
    private async Task<string> GenerateOrderNumberAsync(CancellationToken ct)
    {
        var year = DateTime.UtcNow.Year;
        var prefix = $"ORD-{year}-";

        var last = await _db.Orders.AsNoTracking()
            .Where(o => o.OrderNumber.StartsWith(prefix))
            .OrderByDescending(o => o.OrderNumber)
            .Select(o => o.OrderNumber)
            .FirstOrDefaultAsync(ct);

        var next = 1;
        if (last is not null && int.TryParse(last[prefix.Length..], out var parsed))
        {
            next = parsed + 1;
        }

        return prefix + next.ToString("D4");
    }
}
