namespace WebDS_Test.Services;

/// <summary>
/// Writes the marketplace's vendor ledger: what it owes a shop, and why.
/// </summary>
/// <remarks>
/// <para>
/// Every write is idempotent per order and entry type, because the calls arrive from places that
/// repeat: a payment webhook redelivered after a timeout, a tracking event replayed by the carrier,
/// an administrator clicking a button twice. A ledger that can double-credit is a ledger nobody can
/// pay from, so "credited exactly once" is designed in rather than hoped for.
/// </para>
/// <para>
/// Each write is committed as it is made. The idempotency check is a database query, so it can only
/// be trusted once the row it is looking for is actually stored — leaving it pending would let a
/// repeated call in the same unit of work credit twice. Saving here still commits the caller's other
/// pending changes with it, because EF Core persists the whole change tracker at once.
/// </para>
/// </remarks>
public interface IVendorLedgerService
{
    /// <summary>
    /// Credits a shop for a sale whose money the marketplace has collected. Returns false when there
    /// is nothing to do: the order belongs to no shop, or its sale is already on the ledger.
    /// </summary>
    Task<bool> RecordSaleAsync(Order order, string note, CancellationToken ct = default);

    /// <summary>
    /// Reverses a previously credited sale after a cancellation or refund. Returns false when there
    /// is no credit to reverse, or it has already been reversed.
    /// </summary>
    Task<bool> RecordReversalAsync(Order order, string note, CancellationToken ct = default);

    /// <summary>
    /// Reverses part of a previously credited sale after a partial refund. Returns false when there
    /// is nothing left to reverse, or this return has already been reversed.
    /// </summary>
    /// <remarks>
    /// The gross reversed is the returned goods value after discount — the same base commission was
    /// charged on. It is clamped to what the sale still has outstanding, so repeated calls can never
    /// take back more than the sale credited.
    /// </remarks>
    Task<bool> RecordPartialReversalAsync(
        Order order, int returnRequestId, decimal grossReturned, string note, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class VendorLedgerService : IVendorLedgerService
{
    private const decimal DefaultCommissionRate = 0.10m;

    private readonly ApplicationDbContext _db;
    private readonly ILogger<VendorLedgerService> _logger;

    public VendorLedgerService(ApplicationDbContext db, ILogger<VendorLedgerService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<bool> RecordSaleAsync(Order order, string note, CancellationToken ct = default)
    {
        if (order.ShopId <= 0)
        {
            // A shopless order (the store selling directly) has no vendor to owe.
            return false;
        }

        var alreadyCredited = await _db.VendorLedgerEntries
            .AnyAsync(l => l.OrderId == order.Id && l.Type == VendorLedgerEntryType.Sale, ct);

        if (alreadyCredited)
        {
            return false;
        }

        var shop = await _db.Shops.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == order.ShopId, ct);

        var rate = Clamp(shop?.CommissionRate ?? VendorLedgerService.DefaultCommissionRate);

        // Commission applies to goods only — the shipping the shopper paid is the carrier's, not the
        // vendor's, and is deliberately excluded from the base.
        var gross = order.Subtotal;
        var commission = Math.Round(gross * rate, 2, MidpointRounding.AwayFromZero);
        var net = gross - commission;

        _db.VendorLedgerEntries.Add(new VendorLedgerEntry
        {
            ShopId = order.ShopId,
            OrderId = order.Id,
            Type = VendorLedgerEntryType.Sale,
            GrossAmount = gross,
            CommissionAmount = commission,
            NetAmount = net,
            Note = Truncate(note, 200),
            CreatedUtc = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Vendor ledger: credited shop {ShopId} for order {OrderNumber} — gross {Gross:N2}, commission {Commission:N2}, net {Net:N2}",
            order.ShopId, order.OrderNumber, gross, commission, net);

        return true;
    }

    public async Task<bool> RecordReversalAsync(Order order, string note, CancellationToken ct = default)
    {
        if (order.ShopId <= 0)
        {
            return false;
        }

        var sale = await _db.VendorLedgerEntries.AsNoTracking()
            .FirstOrDefaultAsync(l => l.OrderId == order.Id && l.Type == VendorLedgerEntryType.Sale, ct);

        if (sale is null)
        {
            // Nothing was ever collected for this order, so there is nothing to take back. This is
            // the ordinary case of cancelling an order that was never paid.
            return false;
        }

        var alreadyReversed = await _db.VendorLedgerEntries
            .AnyAsync(l => l.OrderId == order.Id
                         && l.Type == VendorLedgerEntryType.Reversal
                         && l.ReturnRequestId == null, ct);

        // Only a full reversal closes the sale. A partial reversal belongs to a return and leaves
        // the rest of the sale outstanding, so it must not satisfy this check.
        if (alreadyReversed)
        {
            return false;
        }

        // Reversal grosses are stored negated; what earlier returns have already taken back is
        // their negated sum. The sale's remaining goods are all this reversal can still claim.
        var reversalGross = await _db.VendorLedgerEntries.AsNoTracking()
            .Where(l => l.OrderId == order.Id && l.Type == VendorLedgerEntryType.Reversal)
            .Select(l => l.GrossAmount)
            .ToListAsync(ct);

        var remaining = sale.GrossAmount + reversalGross.Sum();

        if (remaining <= 0m)
        {
            return false;
        }

        decimal gross, commission, net;

        if (remaining == sale.GrossAmount)
        {
            // Nothing was returned before: negate the sale exactly, so the sale and its reversal
            // always sum to zero and the shop's balance returns to what it was before the sale.
            gross = -sale.GrossAmount;
            commission = -sale.CommissionAmount;
            net = -sale.NetAmount;
        }
        else
        {
            // Part of the sale was already reversed by a return: this entry takes back only the
            // remainder, at the rate the sale itself was charged — not the shop's rate today, so a
            // commission change between sale and refund cannot rewrite history.
            var saleRate = sale.GrossAmount == 0m
                ? 0m
                : Clamp(sale.CommissionAmount / sale.GrossAmount);

            commission = Math.Round(remaining * saleRate, 2, MidpointRounding.AwayFromZero);
            net = remaining - commission;
            gross = -remaining;
            commission = -commission;
            net = -net;
        }

        _db.VendorLedgerEntries.Add(new VendorLedgerEntry
        {
            ShopId = sale.ShopId,
            OrderId = order.Id,
            Type = VendorLedgerEntryType.Reversal,
            GrossAmount = gross,
            CommissionAmount = commission,
            NetAmount = net,
            Note = Truncate(note, 200),
            CreatedUtc = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Vendor ledger: reversed shop {ShopId} for order {OrderNumber} — net {Net:N2} taken back",
            sale.ShopId, order.OrderNumber, net);

        return true;
    }

    /// <summary>
    /// Reverses part of a credited sale after a partial refund. The amounts mirror
    /// <see cref="RecordSaleAsync"/> proportionally: the returned goods value after discount is
    /// charged commission at the same rate, and every figure is written negated, so the sale and its
    /// reversals always reconcile back to what the shop still keeps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotency is per return rather than per order — one order can have several partial refunds,
    /// so the bare (order, type) pair cannot be the key. The reversal is keyed on
    /// <c>(OrderId, Type, ReturnRequestId)</c>, and the gross is additionally clamped to what the
    /// sale still has outstanding, so no sequence of calls can take back more than was credited.
    /// A full reversal (see <see cref="RecordReversalAsync"/>) closes the sale completely; once one
    /// exists there is nothing left for a partial reversal to take.
    /// </para>
    /// <para>
    /// Like every write here, the row is committed immediately, together with the caller's other
    /// pending changes.
    /// </para>
    /// </remarks>
    public async Task<bool> RecordPartialReversalAsync(
        Order order, int returnRequestId, decimal grossReturned, string note, CancellationToken ct = default)
    {
        if (order.ShopId <= 0 || returnRequestId <= 0 || grossReturned <= 0m)
        {
            return false;
        }

        var sale = await _db.VendorLedgerEntries.AsNoTracking()
            .FirstOrDefaultAsync(l => l.OrderId == order.Id && l.Type == VendorLedgerEntryType.Sale, ct);

        if (sale is null)
        {
            // Nothing was ever collected for this order, so there is nothing to take back.
            return false;
        }

        var alreadyReversed = await _db.VendorLedgerEntries
            .AnyAsync(l => l.OrderId == order.Id
                         && l.Type == VendorLedgerEntryType.Reversal
                         && l.ReturnRequestId == returnRequestId, ct);

        if (alreadyReversed)
        {
            return false;
        }

        var reversals = await _db.VendorLedgerEntries.AsNoTracking()
            .Where(l => l.OrderId == order.Id && l.Type == VendorLedgerEntryType.Reversal)
            .Select(l => l.GrossAmount)
            .ToListAsync(ct);

        // Reversal grosses are stored negated, so the returned so far is their negated sum.
        var returnedSoFar = -reversals.Sum();
        var remaining = sale.GrossAmount - returnedSoFar;

        if (remaining <= 0m)
        {
            // The sale has already been fully reversed (or over-reversed by rounding); a partial
            // refund can only take what is still on the ledger.
            return false;
        }

        // A safety net: the refund flow caps the amount itself, so this should never bite — but a
        // ledger that can be over-drawn by a bad caller is a ledger nobody can pay from.
        if (grossReturned > remaining)
        {
            _logger.LogWarning(
                "Vendor ledger: clamping partial reversal for order {OrderNumber} from {Requested:N2} to {Remaining:N2}",
                order.OrderNumber, grossReturned, remaining);
            grossReturned = remaining;
        }

        // The reversal mirrors the sale it takes back, so the rate is the one the sale itself was
        // charged — not the shop's rate today, which a later commission change must not rewrite.
        var saleRate = sale.GrossAmount == 0m
            ? 0m
            : Clamp(sale.CommissionAmount / sale.GrossAmount);

        var commission = Math.Round(grossReturned * saleRate, 2, MidpointRounding.AwayFromZero);
        var net = grossReturned - commission;

        _db.VendorLedgerEntries.Add(new VendorLedgerEntry
        {
            ShopId = order.ShopId,
            OrderId = order.Id,
            Type = VendorLedgerEntryType.Reversal,
            ReturnRequestId = returnRequestId,
            GrossAmount = -grossReturned,
            CommissionAmount = -commission,
            NetAmount = -net,
            Note = Truncate(note, 200),
            CreatedUtc = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Vendor ledger: partially reversed shop {ShopId} for order {OrderNumber} — gross {Gross:N2}, commission {Commission:N2}, net {Net:N2}",
            order.ShopId, order.OrderNumber, grossReturned, commission, net);

        return true;
    }

    private static decimal Clamp(decimal rate) => rate < 0m ? 0m : rate > 1m ? 1m : rate;

    private static string Truncate(string value, int max)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
