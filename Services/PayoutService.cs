using Microsoft.Extensions.Options;
using WebDS_Test.Services.Email;

namespace WebDS_Test.Services;

/// <summary>A shop's unsettled balance: what the marketplace still owes it.</summary>
public sealed record ShopOutstanding(
    int ShopId,
    string ShopName,
    ShopStatus Status,
    int EntryCount,
    decimal Gross,
    decimal Commission,
    decimal Net)
{
    /// <summary>True when there is actually something to settle.</summary>
    public bool IsPayable => Net > 0m;
}

/// <summary>Outcome of a settlement attempt.</summary>
public sealed record PayoutResult(bool Success, string Message, VendorPayout? Payout = null)
{
    public static PayoutResult Ok(VendorPayout payout, string message) => new(true, message, payout);
    public static PayoutResult Fail(string message) => new(false, message);
}

/// <summary>
/// Settles vendors: reads the ledger to say what is owed, and stamps entries as paid when a payout
/// is made.
/// </summary>
/// <remarks>
/// A payout never writes an earning. It records the settlement and marks the entries it covers, so
/// the outstanding balance is always derivable ("the unsettled entries") and never a stored number
/// that could drift. Balances are summed in memory rather than by the database: money is stored as
/// <c>decimal</c>, and SQLite — one of the two supported providers — cannot aggregate it in SQL.
/// </remarks>
public interface IPayoutService
{
    /// <summary>Unsettled balance for a shop, or null when the shop does not exist.</summary>
    Task<ShopOutstanding?> GetOutstandingAsync(int shopId, CancellationToken ct = default);

    /// <summary>Unsettled balances for every shop, largest first.</summary>
    Task<IReadOnlyList<ShopOutstanding>> GetOutstandingByShopAsync(CancellationToken ct = default);

    /// <summary>Payout history, newest first, optionally narrowed to one shop.</summary>
    Task<IReadOnlyList<VendorPayout>> GetPayoutsAsync(int? shopId = null, int take = 100, CancellationToken ct = default);

    Task<VendorPayout?> GetPayoutAsync(int payoutId, CancellationToken ct = default);

    /// <summary>The ledger entries a payout settled, oldest first — its statement lines.</summary>
    Task<IReadOnlyList<VendorLedgerEntry>> GetStatementLinesAsync(int payoutId, CancellationToken ct = default);

    /// <summary>Every ledger entry for a shop, newest first — settled or not.</summary>
    Task<IReadOnlyList<VendorLedgerEntry>> GetLedgerAsync(int shopId, int take = 200, CancellationToken ct = default);

    /// <summary>Settles a shop's outstanding balance as a single payout.</summary>
    Task<PayoutResult> CreatePayoutAsync(
        int shopId, string? method, string? note, string? createdBy, CancellationToken ct = default);

    /// <summary>
    /// Marks a recorded payout as failed — the bank refused the transfer — and releases the ledger
    /// entries it covered back to unsettled, so the shop's payable re-opens.
    /// </summary>
    Task<PayoutResult> MarkFailedAsync(
        int payoutId, string reason, string? handledBy, CancellationToken ct = default);

    /// <summary>
    /// Reverses a recorded payout — it was made in error — and releases the ledger entries it
    /// covered back to unsettled. The payout row keeps its totals for the statement; a correction
    /// is never an edit of a financial document, it is a new fact beside it.
    /// </summary>
    Task<PayoutResult> ReverseAsync(
        int payoutId, string? note, string? handledBy, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class PayoutService : IPayoutService
{
    private const decimal MinimumTransaction = 0m;

    private readonly ApplicationDbContext _db;
    private readonly PayoutOptions _options;
    private readonly IEmailService? _emails;
    private readonly ILogger<PayoutService> _logger;

    public PayoutService(
        ApplicationDbContext db,
        IOptions<PayoutOptions> options,
        ILogger<PayoutService> logger,
        IEmailService? emails = null)
    {
        _db = db;
        _options = options.Value;
        _logger = logger;
        _emails = emails;
    }

    public async Task<ShopOutstanding?> GetOutstandingAsync(int shopId, CancellationToken ct = default)
    {
        var shop = await _db.Shops.AsNoTracking().FirstOrDefaultAsync(s => s.Id == shopId, ct);
        if (shop is null)
        {
            return null;
        }

        var entries = await _db.VendorLedgerEntries.AsNoTracking()
            .Where(l => l.ShopId == shopId && l.SettledUtc == null)
            .ToListAsync(ct);

        return Summarise(shop.Id, shop.Name, shop.Status, entries);
    }

    public async Task<IReadOnlyList<ShopOutstanding>> GetOutstandingByShopAsync(CancellationToken ct = default)
    {
        // Both sides are small (shops, and unsettled entries since the last payout), and summing in
        // memory keeps the query portable across SQL Server and SQLite.
        var shops = await _db.Shops.AsNoTracking().ToListAsync(ct);

        var entries = await _db.VendorLedgerEntries.AsNoTracking()
            .Where(l => l.SettledUtc == null)
            .ToListAsync(ct);

        var byShop = entries.ToLookup(l => l.ShopId);

        return shops
            .Select(s => Summarise(s.Id, s.Name, s.Status, byShop[s.Id]))
            .OrderByDescending(o => o.Net)
            .ThenBy(o => o.ShopName)
            .ToList();
    }

    public async Task<IReadOnlyList<VendorPayout>> GetPayoutsAsync(
        int? shopId = null, int take = 100, CancellationToken ct = default)
    {
        var query = _db.VendorPayouts.AsNoTracking().Include(p => p.Shop).AsQueryable();

        if (shopId is { } id)
        {
            query = query.Where(p => p.ShopId == id);
        }

        return await query
            .OrderByDescending(p => p.CreatedUtc)
            .ThenByDescending(p => p.Id)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct);
    }

    public Task<VendorPayout?> GetPayoutAsync(int payoutId, CancellationToken ct = default) =>
        _db.VendorPayouts.AsNoTracking()
            .Include(p => p.Shop)
            .FirstOrDefaultAsync(p => p.Id == payoutId, ct);

    public async Task<IReadOnlyList<VendorLedgerEntry>> GetStatementLinesAsync(
        int payoutId, CancellationToken ct = default)
    {
        var payout = await _db.VendorPayouts.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == payoutId, ct);

        if (payout is null)
        {
            return Array.Empty<VendorLedgerEntry>();
        }

        return await _db.VendorLedgerEntries.AsNoTracking()
            .Where(l => l.PayoutReference == payout.Reference)
            .OrderBy(l => l.CreatedUtc)
            .ThenBy(l => l.Id)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<VendorLedgerEntry>> GetLedgerAsync(
        int shopId, int take = 200, CancellationToken ct = default)
    {
        return await _db.VendorLedgerEntries.AsNoTracking()
            .Where(l => l.ShopId == shopId)
            .OrderByDescending(l => l.CreatedUtc)
            .ThenByDescending(l => l.Id)
            .Take(Math.Clamp(take, 1, 1000))
            .ToListAsync(ct);
    }

    public async Task<PayoutResult> CreatePayoutAsync(
        int shopId, string? method, string? note, string? createdBy, CancellationToken ct = default)
    {
        var shop = await _db.Shops.FirstOrDefaultAsync(s => s.Id == shopId, ct);
        if (shop is null)
        {
            return PayoutResult.Fail("Shop not found.");
        }

        var unsettled = await _db.VendorLedgerEntries
            .Where(l => l.ShopId == shopId && l.SettledUtc == null)
            .ToListAsync(ct);

        if (unsettled.Count == 0)
        {
            return PayoutResult.Fail($"There is nothing outstanding for {shop.Name}.");
        }

        var net = unsettled.Sum(l => l.NetAmount);
        if (net <= MinimumTransaction)
        {
            return PayoutResult.Fail(
                $"{shop.Name}'s balance is {net:N2}; a payout is only made when the shop is in credit.");
        }

        if (_options.MinimumAmount > 0m && net < _options.MinimumAmount)
        {
            return PayoutResult.Fail(
                $"{shop.Name}'s balance of {net:N2} is below the minimum payout of {_options.MinimumAmount:N2}.");
        }

        var now = DateTime.UtcNow;
        var reference = await NextReferenceAsync(ct);

        var payout = new VendorPayout
        {
            ShopId = shopId,
            Reference = reference,
            PeriodStartUtc = unsettled.Min(l => l.CreatedUtc),
            PeriodEndUtc = unsettled.Max(l => l.CreatedUtc),
            EntryCount = unsettled.Count,
            GrossAmount = unsettled.Sum(l => l.GrossAmount),
            CommissionAmount = unsettled.Sum(l => l.CommissionAmount),
            NetAmount = net,
            Method = string.IsNullOrWhiteSpace(method) ? _options.DefaultMethod : method.Trim(),
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy,
            CreatedUtc = now
        };

        _db.VendorPayouts.Add(payout);

        // Mark each covered entry. Because the reference is on the entry too, the statement survives
        // independently of this row.
        foreach (var entry in unsettled)
        {
            entry.SettledUtc = now;
            entry.PayoutReference = reference;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Payout {Reference}: settled {Count} ledger entries for shop {ShopId} — net {Net:N2}",
            reference, unsettled.Count, shopId, net);

        // Best-effort notification: a failing email must not undo the settlement itself.
        if (_emails is not null)
        {
            try
            {
                payout.Shop = shop;
                await _emails.NotifyPayoutIssuedAsync(payout, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Payout {Reference}: failed to queue the shop notification email", reference);
            }
        }

        return PayoutResult.Ok(payout, $"{reference} settles {unsettled.Count} entries for {shop.Name} ({net:N2}).");
    }

    /// <inheritdoc />
    public async Task<PayoutResult> MarkFailedAsync(
        int payoutId, string reason, string? handledBy, CancellationToken ct = default)
    {
        var payout = await _db.VendorPayouts.FirstOrDefaultAsync(p => p.Id == payoutId, ct);
        if (payout is null)
        {
            return PayoutResult.Fail("Payout not found.");
        }

        if (payout.Status != PayoutStatus.Paid)
        {
            return PayoutResult.Fail(
                $"Payout {payout.Reference} is already marked {payout.Status.ToDisplay().ToLowerInvariant()}.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return PayoutResult.Fail("A reason is required to mark a payout failed.");
        }

        var released = await ReleaseEntriesAsync(payout.Reference, ct);

        payout.Status = PayoutStatus.Failed;
        payout.FailureReason = reason.Trim();
        payout.HandledBy = string.IsNullOrWhiteSpace(handledBy) ? null : handledBy.Trim();
        payout.FailedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Payout {Reference}: marked failed — {Reason}; {Count} ledger entries released back to unsettled",
            payout.Reference, reason.Trim(), released);

        return PayoutResult.Ok(payout,
            $"{payout.Reference} marked failed; {released} ledger entries released back to the shop's balance.");
    }

    /// <inheritdoc />
    public async Task<PayoutResult> ReverseAsync(
        int payoutId, string? note, string? handledBy, CancellationToken ct = default)
    {
        var payout = await _db.VendorPayouts.FirstOrDefaultAsync(p => p.Id == payoutId, ct);
        if (payout is null)
        {
            return PayoutResult.Fail("Payout not found.");
        }

        if (payout.Status != PayoutStatus.Paid)
        {
            return PayoutResult.Fail(
                $"Payout {payout.Reference} is already marked {payout.Status.ToDisplay().ToLowerInvariant()}.");
        }

        var released = await ReleaseEntriesAsync(payout.Reference, ct);

        payout.Status = PayoutStatus.Reversed;
        payout.Note = string.IsNullOrWhiteSpace(note) ? payout.Note : note.Trim();
        payout.HandledBy = string.IsNullOrWhiteSpace(handledBy) ? null : handledBy.Trim();
        payout.ReversedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Payout {Reference}: reversed; {Count} ledger entries released back to unsettled",
            payout.Reference, released);

        return PayoutResult.Ok(payout,
            $"{payout.Reference} reversed; {released} ledger entries released back to the shop's balance.");
    }

    /// <summary>
    /// Releases every ledger entry a payout covered back to unsettled. Reversing through the
    /// reference keeps the statement intact — the entries' amounts are never edited, they simply
    /// become payable again.
    /// </summary>
    private async Task<int> ReleaseEntriesAsync(string reference, CancellationToken ct)
    {
        var covered = await _db.VendorLedgerEntries
            .Where(l => l.PayoutReference == reference)
            .ToListAsync(ct);

        foreach (var entry in covered)
        {
            entry.SettledUtc = null;
            entry.PayoutReference = null;
        }

        return covered.Count;
    }

    private static ShopOutstanding Summarise(
        int shopId, string shopName, ShopStatus status, IEnumerable<VendorLedgerEntry> entries)
    {
        var list = entries as ICollection<VendorLedgerEntry> ?? entries.ToList();

        return new ShopOutstanding(
            shopId,
            shopName,
            status,
            list.Count,
            list.Sum(l => l.GrossAmount),
            list.Sum(l => l.CommissionAmount),
            list.Sum(l => l.NetAmount));
    }

    private async Task<string> NextReferenceAsync(CancellationToken ct)
    {
        var prefix = $"PAY-{DateTime.UtcNow:yyyy}-";

        // References are sequential per year. The unique index on Reference is the real guard; the
        // scan just produces a readable next number.
        var existing = await _db.VendorPayouts.AsNoTracking()
            .Where(p => p.Reference.StartsWith(prefix))
            .Select(p => p.Reference)
            .ToListAsync(ct);

        var next = 1;
        foreach (var reference in existing)
        {
            var suffix = reference[prefix.Length..];
            if (int.TryParse(suffix, out var value) && value >= next)
            {
                next = value + 1;
            }
        }

        return prefix + next.ToString("D4");
    }
}
