using Microsoft.EntityFrameworkCore;
using WebDS_Test.Domain;

namespace WebDS_Test.Services;

/// <summary>Outcome of a post-sale operation.</summary>
public sealed record PostSaleResult(bool Success, string Message, PostSaleRequest? Request = null)
{
    public static PostSaleResult Ok(PostSaleRequest request, string message) => new(true, message, request);
    public static PostSaleResult Fail(string message) => new(false, message);
}

/// <summary>The worklist counts the admin queue shows as badges.</summary>
public sealed record PostSaleCounts(int Pending, int InProgress, int AwaitingCustomer);

/// <summary>
/// Owns the five post-sale processes Kwikby/LibanPost runs on top of the marketplace: cancellation,
/// return, exchange, return-to-sender and warranty.
/// </summary>
/// <remarks>
/// <para>
/// <b>One document, five processes.</b> Every process produces a <see cref="PostSaleRequest"/> with
/// a fee split, a status timeline and a message thread. What differs is which rules compute the
/// split (<see cref="PostSaleRules"/>), how far the order has progressed (packed? tracking number
/// issued? delivered?) and what the resolution is (refund, promo code, replacement, repair).
/// </para>
/// <para>
/// <b>The rules decide the money, not the operator.</b> The fee split is computed once, when the
/// request is raised, and stored with it. An operator can reject the request or move it through the
/// timeline, but cannot silently change who pays: a request that needs a different split is a
/// different request, and the trail says so.
/// </para>
/// <para>
/// <b>Local return policy.</b> When the customer is at fault ("changed my mind", "wrong option"),
/// the goods are not refunded in cash: the request becomes a promo code of the goods value, per the
/// LibanPost return policy. <see cref="PostSaleRequest.IsPromoCodeRefund"/> records that decision,
/// and <see cref="ProcessAsync"/> issues a single-use code worth exactly that amount.
/// </para>
/// <para>
/// <b>What this service deliberately does not do.</b> It does not call the payment gateway or
/// reverse the vendor ledger: those move money and are recorded by the paths that already own them
/// (<c>ReturnService</c>). A post-sale request is the operational document â€” the queue, the
/// timeline, the fee split and the customer's remedy â€” and <see cref="ProcessAsync"/> records the
/// refund against the order so the amounts agree. Wiring the ledger reversal to post-sale refunds is
/// the next financial step, not a hidden side effect of moving a status.
/// </para>
/// </remarks>
public interface IPostSaleService
{
    // ---- Customer -------------------------------------------------------

    /// <summary>Raises a request against one of the caller's own orders.</summary>
    Task<PostSaleResult> CreateAsync(
        int orderId,
        string userId,
        PostSaleType type,
        PostSaleReason reason,
        int? orderLineId,
        string? detail,
        string? serialNumber = null,
        int? replacementVariantId = null,
        CancellationToken ct = default);

    /// <summary>Withdraws the caller's own request while it is still pending.</summary>
    Task<PostSaleResult> WithdrawAsync(int requestId, string userId, CancellationToken ct = default);

    /// <summary>The caller's own requests, newest first.</summary>
    Task<IReadOnlyList<PostSaleRequest>> GetForUserAsync(string userId, CancellationToken ct = default);

    /// <summary>The requests raised against one order.</summary>
    Task<IReadOnlyList<PostSaleRequest>> GetForOrderAsync(int orderId, CancellationToken ct = default);

    // ---- Operations -----------------------------------------------------

    /// <summary>Every request, newest first, optionally filtered â€” the administration queue.</summary>
    Task<IReadOnlyList<PostSaleRequest>> GetAllAsync(
        PostSaleStatus? status = null,
        PostSaleType? type = null,
        string? search = null,
        int take = 200,
        CancellationToken ct = default);

    /// <summary>A shop's requests, newest first.</summary>
    Task<IReadOnlyList<PostSaleRequest>> GetForShopAsync(
        int shopId, bool openOnly = false, CancellationToken ct = default);

    /// <summary>Loads one request with its timeline, messages and order.</summary>
    Task<PostSaleRequest?> GetAsync(int requestId, CancellationToken ct = default);

    /// <summary>Queue badge counts.</summary>
    Task<PostSaleCounts> GetCountsAsync(CancellationToken ct = default);

    /// <summary>Approves or rejects a pending request.</summary>
    Task<PostSaleResult> DecideAsync(
        int requestId, bool approve, string? note, string actor, CancellationToken ct = default);

    /// <summary>Moves an approved request to the next operational state.</summary>
    Task<PostSaleResult> TransitionAsync(
        int requestId,
        PostSaleStatus next,
        string actor,
        string? note = null,
        string? pickupTrackingNumber = null,
        CancellationToken ct = default);

    /// <summary>
    /// Completes the remedy: refund (cash or promo code), replacement or repair. Moves the request
    /// to <see cref="PostSaleStatus.Processed"/> (or <see cref="PostSaleStatus.ExchangeShipped"/>).
    /// </summary>
    Task<PostSaleResult> ProcessAsync(
        int requestId,
        string actor,
        string? note = null,
        int? replacementVariantId = null,
        CancellationToken ct = default);

    /// <summary>Adds a message to the thread. Either side may write.</summary>
    Task<PostSaleResult> AddMessageAsync(
        int requestId,
        string senderId,
        string senderName,
        bool fromCustomer,
        string content,
        CancellationToken ct = default);

    /// <summary>Records the carrier's return-to-sender for an undeliverable order, on the operator's behalf.</summary>
    Task<PostSaleResult> CreateRtsAsync(
        int orderId,
        string actor,
        string carrier,
        int callAttempts,
        string? note,
        CancellationToken ct = default);

    /// <summary>Sets warranty triage (repair/replacement) for an approved warranty claim.</summary>
    Task<PostSaleResult> TriageWarrantyAsync(
        int requestId, bool canBeRepaired, bool canBeReplaced, string actor, string? note = null, CancellationToken ct = default);

    // ---- Seller fulfilment ----------------------------------------------

    /// <summary>The back office's dispatch queue of seller-confirmed packages.</summary>
    Task<IReadOnlyList<SellerFulfilment>> GetFulfilmentsAsync(bool openOnly = false, CancellationToken ct = default);

    /// <summary>A shop's fulfilment for one order, or null until the seller confirms it.</summary>
    Task<SellerFulfilment?> GetFulfilmentAsync(int orderId, int shopId, CancellationToken ct = default);

    /// <summary>
    /// Records what the seller packed: how many packages, whether the nylon pockets are attached
    /// and whether the invoice is inside. This is the step that starts the label process.
    /// </summary>
    Task<SellerFulfilmentResult> ConfirmSellerFulfilmentAsync(
        int orderId,
        int shopId,
        int packageCount,
        bool hasNylonPockets,
        bool invoiceAttached,
        string actor,
        CancellationToken ct = default);

    /// <summary>Generates one label per confirmed package and moves the fulfilment on.</summary>
    Task<SellerFulfilmentResult> GenerateFulfilmentLabelsAsync(
        int fulfilmentId, string actor, CancellationToken ct = default);

    /// <summary>Records the shipper collecting the labelled packages.</summary>
    Task<SellerFulfilmentResult> RecordFulfilmentPickupAsync(
        int fulfilmentId, string actor, CancellationToken ct = default);
}

/// <summary>Outcome of a seller-fulfilment operation.</summary>
public sealed record SellerFulfilmentResult(bool Success, string Message, SellerFulfilment? Fulfilment = null)
{
    public static SellerFulfilmentResult Ok(SellerFulfilment fulfilment, string message) => new(true, message, fulfilment);
    public static SellerFulfilmentResult Fail(string message) => new(false, message);
}

/// <inheritdoc />
public sealed class PostSaleService : IPostSaleService
{
    /// <summary>Orders far enough along to accept a return, exchange or warranty claim.</summary>
    private static readonly OrderStatus[] ReturnableStatuses =
    {
        OrderStatus.Paid, OrderStatus.Shipped, OrderStatus.Delivered
    };

    /// <summary>States in which the goods have not yet left the seller.</summary>
    private static readonly OrderStatus[] PrePackagingStatuses =
    {
        OrderStatus.Pending, OrderStatus.Paid
    };

    private readonly ApplicationDbContext _db;
    private readonly ILogger<PostSaleService> _logger;
    private readonly IVendorLedgerService? _ledger;

    public PostSaleService(
        ApplicationDbContext db,
        ILogger<PostSaleService> logger,
        IVendorLedgerService? ledger = null)
    {
        _db = db;
        _logger = logger;
        _ledger = ledger;
    }

    // ------------------------------------------------------------------
    // Customer
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<PostSaleResult> CreateAsync(
        int orderId,
        string userId,
        PostSaleType type,
        PostSaleReason reason,
        int? orderLineId,
        string? detail,
        string? serialNumber = null,
        int? replacementVariantId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return PostSaleResult.Fail("Sign in to raise a request.");
        }

        if (type == PostSaleType.RTS)
        {
            // An RTS is raised by the carrier/operator, never by the customer.
            return PostSaleResult.Fail("A return-to-sender case is recorded by LibanPost, not requested by the customer.");
        }

        var order = await _db.Orders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

        // Treat somebody else's order exactly like a missing one, so nothing leaks.
        if (order is null || order.UserId != userId)
        {
            return PostSaleResult.Fail("Order not found.");
        }

        if (order.Status is OrderStatus.Cancelled or OrderStatus.Refunded)
        {
            return PostSaleResult.Fail(
                $"An order that is '{order.Status.ToDisplay()}' cannot be changed after the sale.");
        }

        var isOrderLevel = orderLineId is null;
        OrderLine? line = null;

        if (!isOrderLevel)
        {
            line = order.Lines.FirstOrDefault(l => l.Id == orderLineId);
            if (line is null)
            {
                return PostSaleResult.Fail("That item is not part of this order.");
            }
        }

        var afterTrackingNumber =
            !string.IsNullOrWhiteSpace(order.TrackingNumber) || order.ShippedUtc is not null;
        var beforePackaging = PrePackagingStatuses.Contains(order.Status) && !afterTrackingNumber;
        var afterDelivery = order.Status == OrderStatus.Delivered || order.DeliveredUtc is not null;

        // ---- Type-specific rules ----------------------------------------

        switch (type)
        {
            case PostSaleType.Cancellation when afterTrackingNumber:
                // Process 3: once a tracking number has been issued the request is declined.
                return PostSaleResult.Fail(
                    "This order already has a tracking number, so it can no longer be cancelled. "
                    + "You can raise a return once it arrives.");

            case PostSaleType.Warranty when isOrderLevel:
                return PostSaleResult.Fail("A warranty claim must name the defective item.");

            case PostSaleType.Warranty when string.IsNullOrWhiteSpace(serialNumber):
                return PostSaleResult.Fail("Enter the item's serial number so the seller can verify the warranty.");

            case PostSaleType.Return or PostSaleType.Exchange when !ReturnableStatuses.Contains(order.Status):
                return PostSaleResult.Fail(
                    $"An order that is '{order.Status.ToDisplay()}' cannot be returned yet. "
                    + "Returns open once it is paid.");
        }

        if (type is PostSaleType.Return or PostSaleType.Exchange or PostSaleType.Warranty
            && await _db.PostSaleRequests.AnyAsync(
                r => r.OrderId == orderId
                     && r.Type == type
                     && r.Status != PostSaleStatus.Rejected
                     && r.Status != PostSaleStatus.Closed
                     && (orderLineId == null || r.OrderLineId == null || r.OrderLineId == orderLineId), ct))
        {
            return PostSaleResult.Fail(
                $"There is already an open {type.ToDisplay().ToLowerInvariant()} for this "
                + (isOrderLevel ? "order." : "item."));
        }

        // ---- Fee split ---------------------------------------------------

        var withinGrace = false;
        DateTime? purchaseUtc = null;
        DateTime? graceEndUtc = null;

        if (type == PostSaleType.Warranty)
        {
            purchaseUtc = order.PaidUtc ?? order.PlacedUtc;
            graceEndUtc = purchaseUtc.Value.AddDays(3);
            withinGrace = DateTime.UtcNow <= graceEndUtc.Value;
        }

        var allocation = PostSaleRules.GetAllocation(
            type,
            reason,
            isOrderLevel,
            beforePackaging,
            afterTrackingNumber,
            afterDelivery,
            withinGrace);

        var request = new PostSaleRequest
        {
            Reference = await NextReferenceAsync(ct),
            OrderId = order.Id,
            OrderLineId = orderLineId,
            ShopId = order.ShopId,
            CustomerId = userId,
            Type = type,
            Reason = reason,
            ReasonDetail = Trim(detail, 1000),
            Status = PostSaleStatus.Pending,
            IsOrderLevel = isOrderLevel,
            FeeAllocation = allocation,
            SerialNumber = Trim(serialNumber, 100),
            PurchaseDateUtc = purchaseUtc,
            GracePeriodEndUtc = graceEndUtc,
            WithinGracePeriod = withinGrace,
            ReplacementVariantId = replacementVariantId,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var (refund, promoCode) = ComputeRemedy(request, order, line, allocation);
        request.RefundAmount = refund;
        request.IsPromoCodeRefund = promoCode;

        request.StatusHistory.Add(new PostSaleStatusHistory
        {
            FromStatus = PostSaleStatus.Pending,
            ToStatus = PostSaleStatus.Pending,
            ChangedBy = userId,
            Note = "Request raised.",
            CreatedUtc = DateTime.UtcNow
        });

        _db.PostSaleRequests.Add(request);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Post-sale {Reference} ({Type}) raised on order {OrderNumber} by {User}; refund {Amount:N2}, promo code: {Promo}",
            request.Reference, type, order.OrderNumber, userId, refund, promoCode);

        return PostSaleResult.Ok(request, BuildRaisedMessage(request));
    }

    /// <inheritdoc />
    public async Task<PostSaleResult> WithdrawAsync(int requestId, string userId, CancellationToken ct = default)
    {
        var request = await _db.PostSaleRequests
            .Include(r => r.StatusHistory)
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);

        if (request is null || request.CustomerId != userId)
        {
            return PostSaleResult.Fail("Request not found.");
        }

        if (request.Status != PostSaleStatus.Pending)
        {
            return PostSaleResult.Fail("Only a request that has not been reviewed yet can be withdrawn.");
        }

        return await MoveAsync(request, PostSaleStatus.Closed, userId, "Withdrawn by the customer.", null, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PostSaleRequest>> GetForUserAsync(
        string userId, CancellationToken ct = default) =>
        await _db.PostSaleRequests
            .AsNoTracking()
            .Include(r => r.Order)
            .Include(r => r.OrderLine)
            .Include(r => r.Shop)
            .Where(r => r.CustomerId == userId)
            .OrderByDescending(r => r.CreatedUtc)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PostSaleRequest>> GetForOrderAsync(int orderId, CancellationToken ct = default) =>
        await _db.PostSaleRequests
            .AsNoTracking()
            .Include(r => r.OrderLine)
            .Where(r => r.OrderId == orderId)
            .OrderBy(r => r.CreatedUtc)
            .ToListAsync(ct);

    // ------------------------------------------------------------------
    // Operations
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<PostSaleRequest>> GetAllAsync(
        PostSaleStatus? status = null,
        PostSaleType? type = null,
        string? search = null,
        int take = 200,
        CancellationToken ct = default)
    {
        var query = _db.PostSaleRequests
            .AsNoTracking()
            .Include(r => r.Order)
            .Include(r => r.OrderLine)
            .Include(r => r.Shop)
            .AsQueryable();

        if (status.HasValue)
        {
            query = query.Where(r => r.Status == status.Value);
        }

        if (type.HasValue)
        {
            query = query.Where(r => r.Type == type.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(r =>
                r.Reference.Contains(term)
                || r.Order!.OrderNumber.Contains(term)
                || r.Order!.CustomerName.Contains(term)
                || r.Order!.Email.Contains(term));
        }

        return await query
            .OrderByDescending(r => r.CreatedUtc)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PostSaleRequest>> GetForShopAsync(
        int shopId, bool openOnly = false, CancellationToken ct = default)
    {
        var query = _db.PostSaleRequests
            .AsNoTracking()
            .Include(r => r.Order)
            .Include(r => r.OrderLine)
            .Where(r => r.ShopId == shopId);

        if (openOnly)
        {
            query = query.Where(r => r.Status == PostSaleStatus.Pending
                                  || r.Status == PostSaleStatus.Approved
                                  || r.Status == PostSaleStatus.PickupScheduled
                                  || r.Status == PostSaleStatus.PickedUp
                                  || r.Status == PostSaleStatus.Received);
        }

        return await query.OrderByDescending(r => r.CreatedUtc).ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<PostSaleRequest?> GetAsync(int requestId, CancellationToken ct = default) =>
        await _db.PostSaleRequests
            .Include(r => r.Order).ThenInclude(o => o!.Lines)
            .Include(r => r.OrderLine)
            .Include(r => r.Shop)
            .Include(r => r.Customer)
            .Include(r => r.Messages.OrderBy(m => m.CreatedUtc))
            .Include(r => r.StatusHistory.OrderBy(h => h.CreatedUtc))
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);

    /// <inheritdoc />
    public async Task<PostSaleCounts> GetCountsAsync(CancellationToken ct = default)
    {
        var pending = await _db.PostSaleRequests.CountAsync(r => r.Status == PostSaleStatus.Pending, ct);
        var inProgress = await _db.PostSaleRequests.CountAsync(
            r => r.Status == PostSaleStatus.Approved
              || r.Status == PostSaleStatus.PickupScheduled
              || r.Status == PostSaleStatus.PickedUp
              || r.Status == PostSaleStatus.Received, ct);

        return new PostSaleCounts(pending, inProgress, 0);
    }

    /// <inheritdoc />
    public async Task<PostSaleResult> DecideAsync(
        int requestId, bool approve, string? note, string actor, CancellationToken ct = default)
    {
        var request = await _db.PostSaleRequests
            .Include(r => r.Order)
            .Include(r => r.StatusHistory)
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);

        if (request is null)
        {
            return PostSaleResult.Fail("Request not found.");
        }

        if (request.Status != PostSaleStatus.Pending)
        {
            return PostSaleResult.Fail($"This request has already been {request.Status.ToDisplay().ToLowerInvariant()}.");
        }

        if (!approve)
        {
            // A rejection is final, but the customer's note is kept so the decision is explainable.
            request.AdminNotes = Trim(note, 2000);
            return await MoveAsync(request, PostSaleStatus.Rejected, actor, note, null, ct);
        }

        if (request.Type == PostSaleType.Cancellation)
        {
            // An approved cancellation needs no pickup: the goods never left the seller.
            request.AdminNotes = Trim(note, 2000);
            var approved = await MoveAsync(request, PostSaleStatus.Approved, actor, note, null, ct);
            return approved.Success
                ? await ProcessAsync(request.Id, actor, note, null, ct)
                : approved;
        }

        if (request.Type == PostSaleType.Exchange)
        {
            // An exchange is approved and then shipped; no pickup from the customer is needed for
            // the pre-delivery variant (process 6-A/6-B).
            var result = await MoveAsync(
                request, PostSaleStatus.Approved, actor, note, request.PickupTrackingNumber, ct);
            return result;
        }

        return await MoveAsync(
            request, PostSaleStatus.Approved, actor, note, request.PickupTrackingNumber, ct);
    }

    /// <inheritdoc />
    public async Task<PostSaleResult> TransitionAsync(
        int requestId,
        PostSaleStatus next,
        string actor,
        string? note = null,
        string? pickupTrackingNumber = null,
        CancellationToken ct = default)
    {
        var request = await _db.PostSaleRequests
            .Include(r => r.Order)
            .Include(r => r.StatusHistory)
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);

        if (request is null)
        {
            return PostSaleResult.Fail("Request not found.");
        }

        if (!request.Status.AllowedTransitions().Contains(next))
        {
            return PostSaleResult.Fail(
                $"A request that is '{request.Status.ToDisplay()}' cannot move to '{next.ToDisplay()}'.");
        }

        return await MoveAsync(request, next, actor, note, pickupTrackingNumber, ct);
    }

    /// <inheritdoc />
    public async Task<PostSaleResult> ProcessAsync(
        int requestId, string actor, string? note = null, int? replacementVariantId = null,
        CancellationToken ct = default)
    {
        var request = await _db.PostSaleRequests
            .Include(r => r.Order).ThenInclude(o => o!.Lines)
            .Include(r => r.OrderLine)
            .Include(r => r.StatusHistory)
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);

        if (request is null)
        {
            return PostSaleResult.Fail("Request not found.");
        }

        if (request.Status is PostSaleStatus.Processed or PostSaleStatus.ExchangeShipped
            or PostSaleStatus.Closed)
        {
            return PostSaleResult.Fail("This request has already been completed.");
        }

        // The remedy is issued from Approved (no goods to receive) or Received (goods came back).
        if (request.Status is not (PostSaleStatus.Approved or PostSaleStatus.Received))
        {
            return PostSaleResult.Fail(
                "Approve the request first"
                + (request.Type is PostSaleType.Return or PostSaleType.Warranty
                    ? ", then record the goods as received."
                    : "."));
        }

        var order = request.Order!;
        var now = DateTime.UtcNow;

        switch (request.Type)
        {
            case PostSaleType.Exchange:
            {
                if (replacementVariantId.HasValue)
                {
                    request.ReplacementVariantId = replacementVariantId;
                }

                request.ProcessedUtc = now;
                request.ProcessedBy = actor;
                request.AdminNotes = MergeNote(request.AdminNotes, note);
                return await MoveAsync(
                    request, PostSaleStatus.ExchangeShipped, actor,
                    note ?? "Replacement dispatched.", null, ct);
            }

            case PostSaleType.Warranty when !request.WithinGracePeriod && request.CanBeRepaired:
            {
                // Repaired under warranty: no money moves.
                request.ProcessedUtc = now;
                request.ProcessedBy = actor;
                request.AdminNotes = MergeNote(request.AdminNotes, note);
                return await MoveAsync(
                    request, PostSaleStatus.Processed, actor,
                    note ?? "Item repaired under warranty.", null, ct);
            }
        }

        // ---- Money remedy ------------------------------------------------

        var refund = request.RefundAmount;

        if (refund > 0 && request.IsPromoCodeRefund)
        {
            var code = await IssuePromoCodeAsync(request, refund, ct);
            request.PromoCode = code;
            request.AdminNotes = MergeNote(request.AdminNotes,
                $"Promo code {code} issued for {refund:N2}.");
        }
        else if (refund > 0)
        {
            // The order carries the running total every refund path adds to, so the next request
            // can never over-refund it.
            var remaining = order.GrandTotal - order.RefundedTotal;
            var applied = Math.Min(refund, Math.Max(0m, remaining));
            if (applied < refund)
            {
                refund = Math.Round(applied, 2);
                request.RefundAmount = refund;
            }

            if (refund > 0)
            {
                _db.Payments.Add(new Payment
                {
                    OrderId = order.Id,
                    Provider = "post-sale",
                    Amount = refund,
                    Currency = "USD",
                    Status = PaymentStatus.Refunded,
                    IsTest = false,
                    CreatedUtc = now,
                    CompletedUtc = now
                });

                // Reverse the vendor's credit for the goods value that is being refunded.
                // The fee split tells us who bears the item price; the seller's share is reversed.
                var reversed = false;
                if (_ledger is not null && request.FeeAllocation.ItemPriceBearer == FeeBearer.Seller)
                {
                    // Gross returned is the item price minus any discount the seller already absorbed.
                    // Use the order line's LineTotal if available, otherwise the full refund amount.
                    var grossReturned = request.OrderLine?.LineTotal ?? refund;
                    reversed = await _ledger.RecordPartialReversalAsync(
                        order,
                        returnRequestId: null,
                        postSaleRequestId: request.Id,
                        grossReturned,
                        $"Post-sale {request.Reference}: {refund:N2} refunded to the shopper.",
                        ct);
                }

                order.RefundedTotal += refund;
            }
        }

        request.ProcessedUtc = now;
        request.ProcessedBy = actor;
        request.AdminNotes = MergeNote(request.AdminNotes, note);

        var message = refund > 0
            ? $"{refund:N2} refunded"
              + (request.IsPromoCodeRefund ? $" as promo code {request.PromoCode}" : string.Empty)
            : "No money is due under the applicable rules";

        return await MoveAsync(
            request, PostSaleStatus.Processed, actor, message, null, ct);
    }

    /// <inheritdoc />
    public async Task<PostSaleResult> AddMessageAsync(
        int requestId,
        string senderId,
        string senderName,
        bool fromCustomer,
        string content,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return PostSaleResult.Fail("Enter a message.");
        }

        var request = await _db.PostSaleRequests
            .Include(r => r.Messages)
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);

        if (request is null)
        {
            return PostSaleResult.Fail("Request not found.");
        }

        request.Messages.Add(new PostSaleMessage
        {
            SenderId = senderId,
            SenderName = senderName,
            IsFromCustomer = fromCustomer,
            Content = content.Trim(),
            CreatedUtc = DateTime.UtcNow
        });
        request.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return PostSaleResult.Ok(request, "Message added.");
    }

    /// <inheritdoc />
    public async Task<PostSaleResult> CreateRtsAsync(
        int orderId,
        string actor,
        string carrier,
        int callAttempts,
        string? note,
        CancellationToken ct = default)
    {
        var order = await _db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return PostSaleResult.Fail("Order not found.");
        }

        if (await _db.PostSaleRequests.AnyAsync(
                r => r.OrderId == orderId && r.Type == PostSaleType.RTS
                     && r.Status != PostSaleStatus.Closed, ct))
        {
            return PostSaleResult.Fail("This order already has an open return-to-sender case.");
        }

        var allocation = PostSaleRules.GetRtsAllocation();

        var request = new PostSaleRequest
        {
            Reference = await NextReferenceAsync(ct),
            OrderId = order.Id,
            ShopId = order.ShopId,
            CustomerId = order.UserId ?? string.Empty,
            Type = PostSaleType.RTS,
            Reason = PostSaleReason.Unreachable,
            ReasonDetail = Trim(note, 1000),
            Status = PostSaleStatus.Pending,
            IsOrderLevel = true,
            FeeAllocation = allocation,
            Carrier = Trim(carrier, 40),
            CallAttempts = Math.Max(0, callAttempts),
            AdminNotes = Trim(note, 2000),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var (refund, _) = ComputeRemedy(request, order, null, allocation);
        request.RefundAmount = refund;

        request.StatusHistory.Add(new PostSaleStatusHistory
        {
            FromStatus = PostSaleStatus.Pending,
            ToStatus = PostSaleStatus.Pending,
            ChangedBy = actor,
            Note = $"Return to sender recorded by {actor} after {callAttempts} call attempt(s).",
            CreatedUtc = DateTime.UtcNow
        });

        _db.PostSaleRequests.Add(request);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "RTS {Reference} recorded for order {OrderNumber} after {Attempts} call attempt(s)",
            request.Reference, order.OrderNumber, callAttempts);

        return PostSaleResult.Ok(request,
            $"Return-to-sender {request.Reference} recorded for {order.OrderNumber}.");
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    /// <summary>
    /// Works out the customer's remedy from the fee split. A split where the customer carries the
    /// item price produces a promo code instead of cash, which is the local return policy.
    /// </summary>
    private static (decimal Amount, bool PromoCode) ComputeRemedy(
        PostSaleRequest request, Order order, OrderLine? line, FeeAllocation allocation)
    {
        var goods = request.IsOrderLevel
            ? order.Subtotal
            : line?.LineTotal ?? 0m;

        var shipping = request.IsOrderLevel ? order.ShippingTotal : 0m;

        // The split says who ends up out of pocket; only a seller/shipper/LibanPost-borne item is
        // refunded in cash. A customer-borne item is where the promo-code policy kicks in.
        var goodsRefunded = allocation.ItemPriceBearer != FeeBearer.Customer;
        var shippingRefunded = allocation.ShippingFeeBearer != FeeBearer.Customer
                               && allocation.ShippingFeeBearer != FeeBearer.NotApplicable;

        var amount = request.Type switch
        {
            // An exchange is a replacement, never a refund.
            PostSaleType.Exchange => 0m,

            // RTS refunds the goods; the delivery effort is not refunded.
            PostSaleType.RTS => goods,

            // A change of mind under the local policy becomes a promo code.
            PostSaleType.Return or PostSaleType.Cancellation when goodsRefunded => goods + shipping,
            PostSaleType.Return or PostSaleType.Cancellation => goods,

            // Warranty inside the grace window undoes the sale; a repair costs the seller, not us.
            PostSaleType.Warranty => allocation.ItemPriceBearer == FeeBearer.Seller
                ? goods + (shippingRefunded ? shipping : 0m)
                : 0m,

            _ => goodsRefunded ? goods + (shippingRefunded ? shipping : 0m) : 0m
        };

        var promoCode = request.Type is PostSaleType.Return or PostSaleType.Cancellation
                        && !goodsRefunded
                        && amount > 0m;

        // Never promise more than the order still holds.
        var remaining = Math.Max(0m, order.GrandTotal - order.RefundedTotal);
        amount = Math.Round(Math.Min(amount, remaining), 2);

        return (amount, promoCode);
    }

    private async Task<PostSaleResult> MoveAsync(
        PostSaleRequest request,
        PostSaleStatus next,
        string actor,
        string? note,
        string? pickupTrackingNumber,
        CancellationToken ct)
    {
        if (!request.Status.AllowedTransitions().Contains(next))
        {
            return PostSaleResult.Fail(
                $"A request that is '{request.Status.ToDisplay()}' cannot move to '{next.ToDisplay()}'.");
        }

        var now = DateTime.UtcNow;
        var from = request.Status;

        request.Status = next;
        request.UpdatedUtc = now;

        switch (next)
        {
            case PostSaleStatus.PickupScheduled:
                request.PickupScheduledUtc = now;
                request.PickupTrackingNumber = Trim(pickupTrackingNumber, 100);
                break;
            case PostSaleStatus.PickedUp:
                request.PickedUpUtc = now;
                break;
            case PostSaleStatus.Received:
                request.ReceivedAtWarehouseUtc = now;
                break;
            case PostSaleStatus.Processed:
                request.ProcessedUtc ??= now;
                break;
            case PostSaleStatus.Closed:
                request.ClosedUtc = now;
                request.ClosedBy = Trim(actor, 256);
                break;
        }

        if (!string.IsNullOrWhiteSpace(note))
        {
            request.AdminNotes = MergeNote(request.AdminNotes, note);
        }

        request.StatusHistory.Add(new PostSaleStatusHistory
        {
            FromStatus = from,
            ToStatus = next,
            ChangedBy = Trim(actor, 256),
            Note = Trim(note, 1000),
            CreatedUtc = now
        });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Post-sale {Reference} moved {From} -> {To} by {Actor}",
            request.Reference, from, next, actor);

        return PostSaleResult.Ok(request, $"{request.Reference} is now '{next.ToDisplay()}'.");
    }

    private async Task<string> IssuePromoCodeAsync(
        PostSaleRequest request, decimal amount, CancellationToken ct)
    {
        // A single-use code worth exactly the goods value. The reference keeps it traceable back to
        // the request that produced it, and the loop keeps it unique even across shops.
        var stem = "LP" + new string(request.Reference.Where(char.IsLetterOrDigit).ToArray());
        var code = stem;
        var suffix = 0;

        while (await _db.Coupons.AnyAsync(c => c.Code == code, ct))
        {
            suffix++;
            code = $"{stem}-{suffix}";
        }

        _db.Coupons.Add(new Coupon
        {
            Code = code,
            Description = $"Post-sale promo code for {request.Reference}.",
            Type = DiscountType.FixedAmount,
            Value = amount,
            MinSubtotal = 0m,
            MaxUses = 1,
            MaxUsesPerUser = 1,
            ExpiresUtc = DateTime.UtcNow.AddYears(1),
            IsActive = true,
            AppliesToAll = true,
            CreatedUtc = DateTime.UtcNow
        });

        return code;
    }

    // ------------------------------------------------------------------
    // Seller fulfilment
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<SellerFulfilment>> GetFulfilmentsAsync(
        bool openOnly = false, CancellationToken ct = default)
    {
        var query = _db.SellerFulfilments.AsNoTracking()
            .Include(f => f.Order).ThenInclude(o => o!.Lines)
            .Include(f => f.Shop)
            .AsQueryable();

        if (openOnly)
        {
            query = query.Where(f =>
                f.Status != FulfilmentStatus.Delivered && f.Status != FulfilmentStatus.RTS);
        }

        return await query
            .OrderByDescending(f => f.Status == FulfilmentStatus.AwaitingLabelGeneration)
            .ThenBy(f => f.CreatedUtc)
            .Take(200)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public Task<SellerFulfilment?> GetFulfilmentAsync(
        int orderId, int shopId, CancellationToken ct = default) =>
        _db.SellerFulfilments.AsNoTracking()
            .Include(f => f.Order)
            .FirstOrDefaultAsync(f => f.OrderId == orderId && f.ShopId == shopId, ct);

    /// <inheritdoc />
    public async Task<SellerFulfilmentResult> ConfirmSellerFulfilmentAsync(
        int orderId,
        int shopId,
        int packageCount,
        bool hasNylonPockets,
        bool invoiceAttached,
        string actor,
        CancellationToken ct = default)
    {
        if (packageCount < 1 || packageCount > 100)
        {
            return SellerFulfilmentResult.Fail("A consignment needs between 1 and 100 packages.");
        }

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null || order.ShopId != shopId)
        {
            return SellerFulfilmentResult.Fail("Order not found.");
        }

        if (order.Status is not (OrderStatus.Pending or OrderStatus.Paid))
        {
            return SellerFulfilmentResult.Fail("Packages can only be confirmed before the order ships.");
        }

        if (!hasNylonPockets)
        {
            return SellerFulfilmentResult.Fail(
                "Attach the nylon pockets first: they carry the LibanPost label on the outside of the package.");
        }

        if (!invoiceAttached)
        {
            return SellerFulfilmentResult.Fail(
                "Attach the invoice to the package before requesting labels.");
        }

        var fulfilment = await _db.SellerFulfilments
            .FirstOrDefaultAsync(f => f.OrderId == orderId && f.ShopId == shopId, ct);

        if (fulfilment is null)
        {
            fulfilment = new SellerFulfilment
            {
                OrderId = orderId,
                ShopId = shopId,
                CreatedUtc = DateTime.UtcNow
            };
            _db.SellerFulfilments.Add(fulfilment);
        }
        else if (fulfilment.LabelsGenerated > 0)
        {
            return SellerFulfilmentResult.Fail("Labels are already issued for this order.");
        }

        fulfilment.PackageCount = packageCount;
        fulfilment.HasNylonPockets = hasNylonPockets;
        fulfilment.InvoiceAttached = invoiceAttached;
        fulfilment.LabelsGenerated = 0;
        fulfilment.LabelsGeneratedUtc = null;
        fulfilment.PickedUpByShipperUtc = null;
        fulfilment.SellerConfirmedUtc ??= DateTime.UtcNow;
        fulfilment.Status = FulfilmentStatus.AwaitingLabelGeneration;
        fulfilment.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Fulfilment for order {OrderId} confirmed by {Actor}: {Packages} package(s), nylon={Nylon}, invoice={Invoice}",
            orderId, actor, packageCount, hasNylonPockets, invoiceAttached);

        return SellerFulfilmentResult.Ok(fulfilment, $"{packageCount} package(s) confirmed. Labels are queued.");
    }

    /// <inheritdoc />
    public async Task<SellerFulfilmentResult> GenerateFulfilmentLabelsAsync(
        int fulfilmentId, string actor, CancellationToken ct = default)
    {
        var fulfilment = await _db.SellerFulfilments
            .Include(f => f.Order)
            .FirstOrDefaultAsync(f => f.Id == fulfilmentId, ct);

        if (fulfilment is null)
        {
            return SellerFulfilmentResult.Fail("Fulfilment not found.");
        }

        if (fulfilment.Status != FulfilmentStatus.AwaitingLabelGeneration)
        {
            return SellerFulfilmentResult.Fail("Only a seller-confirmed package needs labels.");
        }

        if (fulfilment.PackageCount < 1)
        {
            return SellerFulfilmentResult.Fail("The seller has not confirmed the package count.");
        }

        fulfilment.LabelsGenerated = fulfilment.PackageCount;
        fulfilment.LabelsGeneratedUtc = DateTime.UtcNow;
        fulfilment.Status = FulfilmentStatus.LabelsGenerated;
        fulfilment.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Fulfilment {Id} labelled by {Actor}: {Labels} label(s)",
            fulfilmentId, actor, fulfilment.LabelsGenerated);

        return SellerFulfilmentResult.Ok(fulfilment,
            $"{fulfilment.LabelsGenerated} label(s) generated for {fulfilment.Order?.OrderNumber}.");
    }

    /// <inheritdoc />
    public async Task<SellerFulfilmentResult> RecordFulfilmentPickupAsync(
        int fulfilmentId, string actor, CancellationToken ct = default)
    {
        var fulfilment = await _db.SellerFulfilments
            .Include(f => f.Order)
            .FirstOrDefaultAsync(f => f.Id == fulfilmentId, ct);

        if (fulfilment is null)
        {
            return SellerFulfilmentResult.Fail("Fulfilment not found.");
        }

        if (fulfilment.Status != FulfilmentStatus.LabelsGenerated)
        {
            return SellerFulfilmentResult.Fail("Generate the labels before recording the pickup.");
        }

        fulfilment.PickedUpByShipperUtc = DateTime.UtcNow;
        fulfilment.Status = FulfilmentStatus.PickedUpByShipper;
        fulfilment.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Fulfilment {Id} picked up by the shipper ({Actor})", fulfilmentId, actor);

        return SellerFulfilmentResult.Ok(fulfilment,
            $"Pickup recorded for {fulfilment.Order?.OrderNumber}.");
    }

    /// <inheritdoc />
    public async Task<PostSaleResult> TriageWarrantyAsync(
        int requestId, bool canBeRepaired, bool canBeReplaced, string actor, string? note = null, CancellationToken ct = default)
    {
        var request = await _db.PostSaleRequests
            .Include(r => r.Order)
            .Include(r => r.StatusHistory)
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);

        if (request is null)
        {
            return PostSaleResult.Fail("Request not found.");
        }

        if (request.Type != PostSaleType.Warranty)
        {
            return PostSaleResult.Fail("Warranty triage only applies to warranty claims.");
        }

        if (request.Status is PostSaleStatus.Processed or PostSaleStatus.Closed or PostSaleStatus.Rejected)
        {
            return PostSaleResult.Fail("This warranty claim can no longer be triaged.");
        }

        request.CanBeRepaired = canBeRepaired;
        request.CanBeReplaced = canBeReplaced;
        request.UpdatedUtc = DateTime.UtcNow;

        // Recompute the fee split for warranty because the remedy can change (repair vs refund).
        var allocation = PostSaleRules.GetWarrantyAllocation(request.WithinGracePeriod, canBeRepaired);
        request.FeeAllocation = allocation;

        // Recompute remedy against current fee split.
        var order = request.Order!;
        var (refund, _) = ComputeRemedy(request, order, request.OrderLine, allocation);
        request.RefundAmount = refund;
        request.IsPromoCodeRefund = false;

        request.AdminNotes = MergeNote(request.AdminNotes, note);

        request.StatusHistory.Add(new PostSaleStatusHistory
        {
            FromStatus = request.Status,
            ToStatus = request.Status,
            ChangedBy = Trim(actor, 256),
            Note = Trim($"Warranty triaged: repair {(canBeRepaired ? "yes" : "no")}, replace {(canBeReplaced ? "yes" : "no")}.", 1000),
            CreatedUtc = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Warranty {Reference} triaged by {Actor}: repair={Repair}, replace={Replace}",
            request.Reference, actor, canBeRepaired, canBeReplaced);

        return PostSaleResult.Ok(request, "Warranty triage updated.");
    }

    private async Task<string> NextReferenceAsync(CancellationToken ct)
    {
        var year = DateTime.UtcNow.Year;
        var sequence = await _db.PostSaleRequests.CountAsync(ct) + 1;

        while (true)
        {
            var reference = $"PSR-{year}-{sequence:D4}";
            if (!await _db.PostSaleRequests.AnyAsync(r => r.Reference == reference, ct))
            {
                return reference;
            }

            sequence++;
        }
    }

    private static string BuildRaisedMessage(PostSaleRequest request)
    {
        var remedy = request.Type switch
        {
            PostSaleType.Exchange => "A replacement will be arranged once the request is approved.",
            PostSaleType.Warranty => request.WithinGracePeriod
                ? "Inside the three-day warranty window, so a full refund is available."
                : "The seller will inspect the item and repair or replace it.",
            _ when request.IsPromoCodeRefund =>
                $"Under the local return policy the goods value ({request.RefundAmount:N2}) is issued as a promo code.",
            _ when request.RefundAmount > 0 => $"{request.RefundAmount:N2} is refundable if approved.",
            _ => "No money is due under the applicable rules."
        };

        return $"Request {request.Reference} raised. {remedy}";
    }

    private static string? MergeNote(string? existing, string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return existing;
        }

        return string.IsNullOrWhiteSpace(existing) ? note.Trim() : $"{existing}\n{note.Trim()}";
    }

    private static string? Trim(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
}
