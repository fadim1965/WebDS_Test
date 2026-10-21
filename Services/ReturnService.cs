using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebDS_Test.Services.Email;

namespace WebDS_Test.Services;

/// <summary>Outcome of a return operation.</summary>
public sealed record ReturnResult(bool Success, string Message, ReturnRequest? Return = null)
{
    public static ReturnResult Ok(ReturnRequest request, string message) => new(true, message, request);
    public static ReturnResult Fail(string message) => new(false, message);
}

/// <summary>One line of a return being raised: which order line, how many units.</summary>
public sealed record ReturnLineInput(int OrderLineId, int Quantity);

/// <summary>
/// Owns the return (RMA) workflow: a shopper asks to send lines back, the shop decides, the goods
/// come back and are restocked, and the money is refunded.
/// </summary>
/// <remarks>
/// <para>
/// The lifecycle is <c>Requested → Approved → Received → Refunded</c>, with <c>Rejected</c> and the
/// shopper's own <c>Cancelled</c> as the exits. Transitions are validated here, the way
/// <c>OrderService</c> validates order transitions.
/// </para>
/// <para>
/// <b>Who does what.</b> The shop's staff approve, reject and receive returns; the refund itself is
/// recorded by an administrator, because it moves the marketplace's money (a card refund through the
/// gateway, a cash-on-delivery refund as a bookkeeping row) and reverses the vendor's ledger credit.
/// A vendor decision is therefore always one step away from money: nothing is refunded automatically
/// and no role can refund on its own say-so without the trail showing it.
/// </para>
/// <para>
/// <b>The refund amount.</b> Returned goods are refunded at the line's snapshotted price, with the
/// order-level discount spread across the returned goods in proportion to their value — the same
/// apportionment <c>OrderService</c> applies to tax at checkout — and tax refunded on the same base.
/// Shipping is refunded only when the whole order comes back. The total is capped at what the order
/// still owes (<c>GrandTotal − RefundedTotal</c>) and the goods part at what the ledger still credits,
/// so no sequence of returns can over-refund.
/// </para>
/// <para>
/// <b>Idempotency.</b> The ledger reversal is keyed per return request, so a repeated call or a
/// double-click cannot take a second bite; the refund itself is gated on the return's status, which
/// leaves <c>Received</c> exactly once.
/// </para>
/// </remarks>
public interface IReturnService
{
    // ---- Shopper --------------------------------------------------------

    /// <summary>Raises a return for the shopper's own order. One open request per order.</summary>
    Task<ReturnResult> CreateAsync(
        int orderId, string userId, IReadOnlyList<ReturnLineInput> lines, string? note,
        CancellationToken ct = default);

    /// <summary>Withdraws the shopper's own open return.</summary>
    Task<ReturnResult> CancelAsync(int returnId, string userId, CancellationToken ct = default);

    /// <summary>The shopper's return history, newest first.</summary>
    Task<IReadOnlyList<ReturnRequest>> GetForUserAsync(string userId, CancellationToken ct = default);

    /// <summary>The returns on one order, oldest first.</summary>
    Task<IReadOnlyList<ReturnRequest>> GetForOrderAsync(int orderId, CancellationToken ct = default);

    // ---- Shop and staff -------------------------------------------------

    /// <summary>A shop's returns, newest first; <paramref name="openOnly"/> narrows to the worklist.</summary>
    Task<IReadOnlyList<ReturnRequest>> GetForShopAsync(
        int shopId, bool? openOnly = null, CancellationToken ct = default);

    /// <summary>Every return, newest first, optionally filtered — the administration queue.</summary>
    Task<IReadOnlyList<ReturnRequest>> GetAllAsync(
        ReturnStatus? status = null, string? search = null, int take = 200, CancellationToken ct = default);

    /// <summary>Loads one return with its order lines, for a detail view.</summary>
    Task<ReturnRequest?> GetAsync(int returnId, CancellationToken ct = default);

    /// <summary>Approves or rejects a requested return. Shop staff or an administrator.</summary>
    Task<ReturnResult> DecideAsync(
        int returnId, bool approve, string? note, string actor, CancellationToken ct = default);

    /// <summary>Records that the goods came back and restocks them. Shop staff or an administrator.</summary>
    Task<ReturnResult> MarkReceivedAsync(int returnId, string actor, CancellationToken ct = default);

    /// <summary>Issues the refund. Administrators only — it moves the marketplace's money.</summary>
    Task<ReturnResult> RefundAsync(int returnId, string actor, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ReturnService : IReturnService
{
    private static readonly OrderStatus[] ReturnableStatuses =
        { OrderStatus.Paid, OrderStatus.Shipped, OrderStatus.Delivered };

    private static readonly ReturnStatus[] OpenStatuses =
        { ReturnStatus.Requested, ReturnStatus.Approved, ReturnStatus.Received };

    private readonly ApplicationDbContext _db;
    private readonly IPaymentGateway _payments;
    private readonly IEmailService _emails;
    private readonly StoreSettings _settings;
    private readonly IVendorLedgerService? _ledger;
    private readonly ILogger<ReturnService> _logger;

    public ReturnService(
        ApplicationDbContext db,
        IPaymentGateway payments,
        IEmailService emails,
        IOptions<StoreSettings> settings,
        ILogger<ReturnService> logger,
        IVendorLedgerService? ledger = null)
    {
        _db = db;
        _payments = payments;
        _emails = emails;
        _settings = settings.Value;
        _ledger = ledger;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // Shopper
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<ReturnResult> CreateAsync(
        int orderId, string userId, IReadOnlyList<ReturnLineInput> lines, string? note,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return ReturnResult.Fail("Sign in to request a return.");
        }

        var order = await _db.Orders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

        if (order is null)
        {
            return ReturnResult.Fail("Order not found.");
        }

        // The order must belong to the caller — an id that belongs to somebody else is treated the
        // same as one that does not exist, so nothing about other shoppers' orders leaks.
        if (order.UserId != userId)
        {
            return ReturnResult.Fail("Order not found.");
        }

        if (!ReturnableStatuses.Contains(order.Status))
        {
            return ReturnResult.Fail(
                $"An order that is '{order.Status.ToDisplay()}' cannot be returned. Returns open once it is paid.");
        }

        if (await _db.ReturnRequests.AnyAsync(r => r.OrderId == orderId && OpenStatuses.Contains(r.Status), ct))
        {
            return ReturnResult.Fail("This order already has an open return request.");
        }

        // Validate every line against the order and against what previous refunds already sent back.
        var refundedQty = await RefundedQuantitiesAsync(orderId, ct);
        var byId = order.Lines.ToDictionary(l => l.Id);

        var chosen = new List<(OrderLine Line, int Quantity)>();

        foreach (var input in lines)
        {
            if (input.Quantity <= 0)
            {
                continue; // A row left at zero is simply not part of the request.
            }

            if (!byId.TryGetValue(input.OrderLineId, out var line))
            {
                return ReturnResult.Fail("One of the items does not belong to this order.");
            }

            var already = refundedQty.TryGetValue(line.Id, out var q) ? q : 0;
            if (input.Quantity > line.Quantity - already)
            {
                return ReturnResult.Fail(
                    already > 0
                        ? $"Only {line.Quantity - already} × '{line.ProductName}' remain returnable."
                        : $"Only {line.Quantity} × '{line.ProductName}' were ordered.");
            }

            chosen.Add((line, input.Quantity));
        }

        if (chosen.Count == 0)
        {
            return ReturnResult.Fail("Choose at least one item and quantity to return.");
        }

        var request = new ReturnRequest
        {
            OrderId = order.Id,
            ShopId = order.ShopId,
            Reference = await NextReferenceAsync(ct),
            Status = ReturnStatus.Requested,
            UserId = userId,
            Email = order.Email,
            CustomerNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            RequestedUtc = DateTime.UtcNow
        };

        foreach (var (line, quantity) in chosen)
        {
            request.Lines.Add(new ReturnLine
            {
                OrderLineId = line.Id,
                Quantity = quantity,
                // The price is snapshotted from the order line, which is itself a snapshot of what
                // the shopper actually paid — later catalogue edits cannot move the refund.
                UnitPrice = line.UnitPrice
            });
        }

        _db.ReturnRequests.Add(request);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Return {Reference} requested for order {OrderNumber} ({Lines} line(s), goods {Goods:N2})",
            request.Reference, order.OrderNumber, request.Lines.Count, request.Lines.Sum(l => l.LineTotal));

        return ReturnResult.Ok(request,
            $"Return {request.Reference} requested. The shop will approve or reject it.");
    }

    /// <inheritdoc />
    public async Task<ReturnResult> CancelAsync(int returnId, string userId, CancellationToken ct = default)
    {
        var request = await _db.ReturnRequests.FirstOrDefaultAsync(r => r.Id == returnId, ct);
        if (request is null || request.UserId != userId)
        {
            return ReturnResult.Fail("Return request not found.");
        }

        if (request.Status is not (ReturnStatus.Requested or ReturnStatus.Approved))
        {
            return ReturnResult.Fail(
                $"A return that is '{request.Status.ToDisplay()}' can no longer be cancelled.");
        }

        request.Status = ReturnStatus.Cancelled;
        request.DecidedUtc = DateTime.UtcNow;
        request.DecisionNote = "Cancelled by the shopper.";

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Return {Reference} cancelled by the shopper", request.Reference);

        return ReturnResult.Ok(request, $"Return {request.Reference} cancelled.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReturnRequest>> GetForUserAsync(string userId, CancellationToken ct = default) =>
        await _db.ReturnRequests.AsNoTracking()
            .Include(r => r.Order)
            .Include(r => r.Lines)
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.RequestedUtc)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReturnRequest>> GetForOrderAsync(int orderId, CancellationToken ct = default) =>
        await _db.ReturnRequests.AsNoTracking()
            .Include(r => r.Lines)
            .ThenInclude(l => l.OrderLine)
            .Where(r => r.OrderId == orderId)
            .OrderBy(r => r.RequestedUtc)
            .ToListAsync(ct);

    // ------------------------------------------------------------------
    // Shop and staff
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReturnRequest>> GetForShopAsync(
        int shopId, bool? openOnly = null, CancellationToken ct = default)
    {
        var query = _db.ReturnRequests.AsNoTracking()
            .Include(r => r.Order)
            .Include(r => r.Lines)
            .Where(r => r.ShopId == shopId)
            .AsQueryable();

        if (openOnly == true)
        {
            query = query.Where(r => OpenStatuses.Contains(r.Status));
        }

        return await query
            .OrderByDescending(r => r.RequestedUtc)
            .ThenByDescending(r => r.Id)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReturnRequest>> GetAllAsync(
        ReturnStatus? status = null, string? search = null, int take = 200, CancellationToken ct = default)
    {
        IQueryable<ReturnRequest> query = _db.ReturnRequests.AsNoTracking()
            .Include(r => r.Order)
            .Include(r => r.Shop)
            .Include(r => r.Lines);

        if (status is { } s)
        {
            query = query.Where(r => r.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim().Replace("%", "\\%").Replace("_", "\\_")}%";
            query = query.Where(r =>
                EF.Functions.Like(r.Reference, term, "\\") ||
                EF.Functions.Like(r.Email, term, "\\") ||
                EF.Functions.Like(r.Order!.OrderNumber, term, "\\"));
        }

        return await query
            .OrderByDescending(r => r.RequestedUtc)
            .ThenByDescending(r => r.Id)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public Task<ReturnRequest?> GetAsync(int returnId, CancellationToken ct = default) =>
        _db.ReturnRequests.AsNoTracking()
            .Include(r => r.Order)
            .ThenInclude(o => o!.Lines)
            .Include(r => r.Shop)
            .Include(r => r.Lines)
            .ThenInclude(l => l.OrderLine)
            .FirstOrDefaultAsync(r => r.Id == returnId, ct);

    /// <inheritdoc />
    public async Task<ReturnResult> DecideAsync(
        int returnId, bool approve, string? note, string actor, CancellationToken ct = default)
    {
        var request = await _db.ReturnRequests
            .Include(r => r.Order)
            .FirstOrDefaultAsync(r => r.Id == returnId, ct);

        if (request is null)
        {
            return ReturnResult.Fail("Return request not found.");
        }

        if (request.Status != ReturnStatus.Requested)
        {
            return ReturnResult.Fail(
                $"A return that is '{request.Status.ToDisplay()}' cannot be decided.");
        }

        request.Status = approve ? ReturnStatus.Approved : ReturnStatus.Rejected;
        request.DecidedUtc = DateTime.UtcNow;
        request.HandledBy = string.IsNullOrWhiteSpace(actor) ? null : actor.Trim();
        request.DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Return {Reference} {Decision} by {Actor}",
            request.Reference, approve ? "approved" : "rejected", actor);

        await TryNotifyAsync(request, "return decision",
            () => _emails.NotifyReturnDecidedAsync(request, approve, ct));

        return ReturnResult.Ok(request,
            approve
                ? $"Return {request.Reference} approved — waiting for the goods."
                : $"Return {request.Reference} rejected.");
    }

    /// <inheritdoc />
    public async Task<ReturnResult> MarkReceivedAsync(int returnId, string actor, CancellationToken ct = default)
    {
        var request = await _db.ReturnRequests
            .Include(r => r.Order)
            .Include(r => r.Lines)
            .ThenInclude(l => l.OrderLine!)
            .ThenInclude(ol => ol.ProductVariant)
            .ThenInclude(v => v!.Product)
            .ThenInclude(p => p!.Listings)
            .FirstOrDefaultAsync(r => r.Id == returnId, ct);

        if (request is null)
        {
            return ReturnResult.Fail("Return request not found.");
        }

        if (request.Status != ReturnStatus.Approved)
        {
            return ReturnResult.Fail(
                $"A return that is '{request.Status.ToDisplay()}' cannot be received.");
        }

        var restocked = 0;

        foreach (var line in request.Lines)
        {
            var variant = line.OrderLine?.ProductVariant;
            if (variant is null)
            {
                // The product variant was removed after the sale; its stock pool is gone with it.
                continue;
            }

            // Stock is held on the shop's listing — the same pool checkout committed. When the
            // listing no longer carries stock (an unlisted product), the variant's legacy pool
            // receives it, which is where a cancellation restock goes for the same reason.
            var listing = variant.Product?.Listings
                .FirstOrDefault(l => l.IsActive && l.ShopId == request.ShopId);

            int stockAfter;
            if (listing is not null)
            {
                listing.StockQuantity += line.Quantity;
                listing.UpdatedUtc = DateTime.UtcNow;
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
                Reason = StockMovementReason.Return,
                Note = $"Return {request.Reference} received ({request.Order?.OrderNumber})",
                CreatedBy = actor,
                CreatedUtc = DateTime.UtcNow
            });

            restocked += line.Quantity;
        }

        request.Status = ReturnStatus.Received;
        request.ReceivedUtc = DateTime.UtcNow;
        request.HandledBy = string.IsNullOrWhiteSpace(actor) ? null : actor.Trim();

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Return {Reference} received; {Restocked} unit(s) restocked", request.Reference, restocked);

        return ReturnResult.Ok(request,
            $"Return {request.Reference} received; {restocked} unit(s) back on the shelf.");
    }

    /// <inheritdoc />
    public async Task<ReturnResult> RefundAsync(int returnId, string actor, CancellationToken ct = default)
    {
        var request = await _db.ReturnRequests
            .Include(r => r.Order)
            .ThenInclude(o => o!.Lines)
            .Include(r => r.Lines)
            .Include(r => r.Order)
            .ThenInclude(o => o!.Payments)
            .FirstOrDefaultAsync(r => r.Id == returnId, ct);

        if (request is null)
        {
            return ReturnResult.Fail("Return request not found.");
        }

        if (request.Status != ReturnStatus.Received)
        {
            return ReturnResult.Fail(
                "Only a received return can be refunded. Record the goods as received first.");
        }

        var order = request.Order!;
        var now = DateTime.UtcNow;

        var computation = await ComputeRefundAsync(order, request, ct);
        if (computation is null)
        {
            return ReturnResult.Fail("This order's sale is not on the vendor ledger, so there is nothing to reverse.");
        }

        var (goodsAfterDiscount, taxShare, refundAmount, fullRefund) = computation.Value;

        if (refundAmount <= 0m)
        {
            return ReturnResult.Fail("There is nothing left to refund on this order.");
        }

        // ---- Move the money ------------------------------------------------
        // A card refund goes back through the gateway against the original charge. Cash on delivery
        // was collected by the courier, so there is no electronic trail to reverse — the refund is
        // recorded as a refunded payment row with no transaction reference.
        var cod = order.PaymentMethod == PaymentMethod.CashOnDelivery;
        string provider;
        string? refundTransactionId = null;
        var isTest = false;

        if (!cod)
        {
            var succeeded = order.Payments
                .Where(p => p.Status == PaymentStatus.Succeeded)
                .OrderBy(p => p.Id)
                .ToList();

            if (succeeded.Count == 0)
            {
                return ReturnResult.Fail(
                    "No successful payment is on record for this order, so there is nothing to refund through the gateway.");
            }

            provider = succeeded[0].Provider;
            var remaining = refundAmount;

            foreach (var payment in succeeded)
            {
                if (remaining <= 0m)
                {
                    break;
                }

                var slice = Math.Min(remaining, payment.Amount);
                var receipt = await _payments.RefundAsync(payment.TransactionId, slice, ct);
                if (!receipt.Success)
                {
                    // The gateway refused: nothing has been mutated yet, so the request can be
                    // retried once the provider is willing.
                    return ReturnResult.Fail($"The gateway refused the refund: {receipt.FailureReason}");
                }

                refundTransactionId ??= receipt.TransactionId;
                isTest = receipt.IsTest;
                remaining -= slice;
            }
        }
        else
        {
            provider = "cash-on-delivery";
        }

        var refundRow = new Payment
        {
            OrderId = order.Id,
            Provider = provider,
            TransactionId = refundTransactionId,
            Amount = refundAmount,
            Currency = _settings.Currency,
            Status = PaymentStatus.Refunded,
            IsTest = cod ? false : isTest,
            CreatedUtc = now,
            CompletedUtc = now
        };
        _db.Payments.Add(refundRow);

        // ---- Reverse the vendor's credit ------------------------------------
        var reversed = false;
        if (_ledger is not null)
        {
            reversed = await _ledger.RecordPartialReversalAsync(
                order, request.Id, goodsAfterDiscount,
                $"Return {request.Reference}: {refundAmount:N2} refunded to the shopper.", ct);
        }

        // ---- Close the request, and the order when it is fully refunded ------
        request.Status = ReturnStatus.Refunded;
        request.RefundedUtc = now;
        request.RefundAmount = refundAmount;
        request.HandledBy = string.IsNullOrWhiteSpace(actor) ? null : actor.Trim();
        request.DecisionNote = reversed
            ? request.DecisionNote
            : "Refund recorded; the vendor ledger had no sale to reverse.";

        order.RefundedTotal += refundAmount;

        if (fullRefund)
        {
            if (order.Status.AllowedTransitions().Contains(OrderStatus.Refunded))
            {
                order.Status = OrderStatus.Refunded;
                order.CancelledUtc = now;

                // The courier's cash was collected; recording it as refunded keeps the collection
                // state honest rather than leaving an order marked "collected" that was repaid.
                if (order.CollectionStatus == CollectionStatus.Collected)
                {
                    order.CollectionStatus = CollectionStatus.Refunded;
                }
            }
            else
            {
                _logger.LogWarning(
                    "Return {Reference} fully refunded order {OrderNumber}, but its status {Status} cannot move to Refunded; the refund is recorded without a status change",
                    request.Reference, order.OrderNumber, order.Status);
            }
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Return {Reference} refunded {Amount:N2} of order {OrderNumber} (full order: {Full})",
            request.Reference, refundAmount, order.OrderNumber, fullRefund);

        await TryNotifyAsync(request, "refund",
            () => _emails.NotifyReturnRefundedAsync(request, ct));

        return ReturnResult.Ok(request,
            $"{refundAmount:N2} refunded for return {request.Reference}.");
    }

    // ------------------------------------------------------------------
    // Amount computation
    // ------------------------------------------------------------------

    /// <summary>
    /// Works out what a return is worth: the returned goods after their share of the discount, the
    /// tax on that, and shipping when the whole order comes back. Everything is capped twice — the
    /// goods part at what the ledger still credits, the total at what the order still owes — so
    /// repeated or concurrent returns can never over-refund.
    /// </summary>
    /// <returns>
    /// (goods after discount, tax share, total refund amount, is this a full-order refund), or null
    /// when the order's sale is not on the ledger at all.
    /// </returns>
    private async Task<(decimal Goods, decimal Tax, decimal Amount, bool Full)?> ComputeRefundAsync(
        Order order, ReturnRequest request, CancellationToken ct)
    {
        var sale = await _db.VendorLedgerEntries.AsNoTracking()
            .FirstOrDefaultAsync(l => l.OrderId == order.Id && l.Type == VendorLedgerEntryType.Sale, ct);

        var goodsReturned = request.Lines.Sum(l => l.LineTotal);
        var linesSum = order.Lines.Sum(l => l.LineTotal);

        // The order-level discount is apportioned by value, matching how checkout spread it across
        // lines for tax. With no discount, this is simply the goods back at their listed price.
        var discountShare = order.DiscountTotal > 0m && linesSum > 0m
            ? Round(goodsReturned * order.DiscountTotal / linesSum)
            : 0m;
        var goodsAfterDiscount = Round(goodsReturned - discountShare);

        if (sale is not null)
        {
            // The ledger's own arithmetic is the second cap: reversals are stored negated, so what
            // has already been returned is their negated sum.
            var reversalGross = await _db.VendorLedgerEntries.AsNoTracking()
                .Where(l => l.OrderId == order.Id && l.Type == VendorLedgerEntryType.Reversal)
                .Select(l => l.GrossAmount)
                .ToListAsync(ct);

            var remainingGoods = sale.GrossAmount + reversalGross.Sum();
            if (remainingGoods <= 0m)
            {
                return null; // The sale is fully reversed; a refund cannot take from it again.
            }

            if (goodsAfterDiscount > remainingGoods)
            {
                goodsAfterDiscount = Round(remainingGoods);
            }
        }
        else if (order.ShopId > 0)
        {
            // A shopless (store-owned) order has no ledger at all; a vendor order whose sale is
            // missing was never credited, and there is nothing to reverse.
            return null;
        }

        var taxBase = order.Subtotal;
        var taxShare = taxBase > 0m
            ? Round(order.TaxTotal * goodsAfterDiscount / taxBase)
            : 0m;

        var alreadyRefunded = order.RefundedTotal;
        var amount = Round(goodsAfterDiscount + taxShare);
        var amountWithShipping = Round(amount + order.ShippingTotal);

        // The whole order is back when every line's ordered quantity is accounted for by this
        // return plus earlier refunded ones — only then does the shipping come back too.
        var refundedQty = await RefundedQuantitiesAsync(order.Id, ct);
        var nowQty = request.Lines.GroupBy(l => l.OrderLineId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        var full = order.Lines.All(line =>
        {
            var prior = refundedQty.TryGetValue(line.Id, out var q) ? q : 0;
            var current = nowQty.TryGetValue(line.Id, out var q2) ? q2 : 0;
            return prior + current >= line.Quantity;
        });

        if (full)
        {
            amount = amountWithShipping;
        }

        // The final cap: the order itself. Whatever was refunded before — the one-shot full refund
        // or earlier returns — leaves this much.
        var remainingTotal = order.GrandTotal - alreadyRefunded;
        if (amount > remainingTotal)
        {
            amount = Round(remainingTotal);
        }

        full = full && alreadyRefunded + amount >= order.GrandTotal - 0.005m;

        return (goodsAfterDiscount, taxShare, amount, full);
    }

    /// <summary>Units of each order line already sent back by completed (refunded) returns.</summary>
    private async Task<Dictionary<int, int>> RefundedQuantitiesAsync(int orderId, CancellationToken ct)
    {
        var rows = await _db.ReturnRequests.AsNoTracking()
            .Where(r => r.OrderId == orderId && r.Status == ReturnStatus.Refunded)
            .Select(r => r.Lines.Select(l => new { l.OrderLineId, l.Quantity }))
            .ToListAsync(ct);

        return rows
            .SelectMany(x => x)
            .GroupBy(l => l.OrderLineId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
    }

    private decimal Round(decimal value) =>
        Math.Round(value, 2, _settings.Rounding);

    private async Task<string> NextReferenceAsync(CancellationToken ct)
    {
        var year = DateTime.UtcNow.Year;
        var prefix = $"RMA-{year}-";

        var last = await _db.ReturnRequests.AsNoTracking()
            .Where(r => r.Reference.StartsWith(prefix))
            .OrderByDescending(r => r.Reference)
            .Select(r => r.Reference)
            .FirstOrDefaultAsync(ct);

        var next = 1;
        if (last is not null && int.TryParse(last[prefix.Length..], out var parsed))
        {
            next = parsed + 1;
        }

        return prefix + next.ToString("D4");
    }

    /// <summary>
    /// Queues a notification, swallowing any failure — the refund has already been committed by the
    /// time this runs, so an email problem is logged and moved past, exactly as order notifications are.
    /// </summary>
    private async Task TryNotifyAsync(ReturnRequest request, string what, Func<Task> notify)
    {
        try
        {
            await notify();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Could not queue the {What} email for return {Reference}", what, request.Reference);
        }
    }
}
