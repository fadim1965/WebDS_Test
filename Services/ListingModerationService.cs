using Microsoft.EntityFrameworkCore;
using WebDS_Test.Domain;
using WebDS_Test.Services.Email;

namespace WebDS_Test.Services;

/// <summary>The outcome of a moderation action.</summary>
/// <param name="Success">Whether the action was carried out.</param>
/// <param name="Message">What happened, in words meant for the person who asked.</param>
/// <param name="Product">The listing the action touched, when there was one.</param>
public sealed record ListingResult(bool Success, string Message, Product? Product = null)
{
    public static ListingResult Ok(Product product, string message) => new(true, message, product);

    public static ListingResult Fail(string message) => new(false, message);
}

/// <summary>One listing in the moderation queue.</summary>
public sealed class ListingQueueRow
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string ShopName { get; init; } = string.Empty;
    public ListingStatus Status { get; init; }
    public string? Reason { get; init; }
    public DateTime? ModeratedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }
    public int VariantCount { get; init; }
    public decimal Price { get; init; }
    public int Stock { get; init; }
}

/// <summary>
/// Decides what happens to a vendor's listing: a vendor submits it, an administrator approves it
/// or turns it down, and only an approved listing is visible to shoppers.
/// </summary>
/// <remarks>
/// <para>
/// The service owns the pairing between <see cref="Product.IsPublished"/> — the flag the whole
/// storefront filters on — and <see cref="Product.ModerationStatus"/>, the review state. Keeping
/// the two in step here means no controller has to remember both, and a listing can never end up
/// visible without having been approved.
/// </para>
/// <para>
/// The state a listing had before moderation existed was "published", so a store that predates the
/// feature keeps every listing on sale. Only listings submitted after this is in place go through
/// the queue.
/// </para>
/// </remarks>
public interface IListingModerationService
{
    /// <summary>
    /// Puts a listing into the review queue. The vendor must own the listing.
    /// </summary>
    Task<ListingResult> SubmitAsync(int productId, int shopId, CancellationToken ct = default);

    /// <summary>Approves a listing: it goes live.</summary>
    Task<ListingResult> ApproveAsync(int productId, CancellationToken ct = default);

    /// <summary>Turns a listing down. The vendor is told why.</summary>
    Task<ListingResult> RejectAsync(int productId, string reason, CancellationToken ct = default);

    /// <summary>Pulls a live listing back to draft, as the marketplace.</summary>
    Task<ListingResult> WithdrawAsync(int productId, string? reason, CancellationToken ct = default);

    /// <summary>Takes a listing off sale, as its owner. The vendor is not mailed about their own action.</summary>
    Task<ListingResult> UnpublishAsync(int productId, int shopId, CancellationToken ct = default);

    /// <summary>
    /// The listings waiting for a decision, newest first. A null status lists every state, so the
    /// screen can show the whole picture and not only the queue.
    /// </summary>
    Task<IReadOnlyList<ListingQueueRow>> GetQueueAsync(
        ListingStatus? status, string? search, CancellationToken ct = default);

    /// <summary>How many listings are waiting for a decision.</summary>
    Task<int> CountPendingAsync(CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ListingModerationService : IListingModerationService
{
    private readonly ApplicationDbContext _db;
    private readonly IEmailService? _emails;
    private readonly ILogger<ListingModerationService> _logger;

    public ListingModerationService(
        ApplicationDbContext db, ILogger<ListingModerationService> logger, IEmailService? emails = null)
    {
        _db = db;
        _logger = logger;
        _emails = emails;
    }

    public async Task<ListingResult> SubmitAsync(int productId, int shopId, CancellationToken ct = default)
    {
        var product = await _db.Products.Include(p => p.Shop).FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null)
        {
            return ListingResult.Fail("That listing no longer exists.");
        }

        if (product.ShopId != shopId)
        {
            return ListingResult.Fail("That listing belongs to another shop.");
        }

        if (product.IsDeleted)
        {
            return ListingResult.Fail("That listing has been deleted.");
        }

        if (product.ModerationStatus == ListingStatus.PendingReview)
        {
            return ListingResult.Fail("That listing is already waiting for a decision.");
        }

        // The vendor cannot put a listing on sale by itself, so submitting it takes it down if it
        // happens to be live.
        SetState(product, ListingStatus.PendingReview, reason: null);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Listing {ProductId} ({Name}) submitted for review", product.Id, product.Name);

        return ListingResult.Ok(product, $"'{product.Name}' was submitted and is waiting for approval.");
    }

    public async Task<ListingResult> ApproveAsync(int productId, CancellationToken ct = default)
    {
        var product = await _db.Products.Include(p => p.Shop).FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null)
        {
            return ListingResult.Fail("That listing no longer exists.");
        }

        if (product.IsDeleted)
        {
            return ListingResult.Fail("That listing has been deleted.");
        }

        if (product.ModerationStatus == ListingStatus.Published)
        {
            return ListingResult.Fail("That listing is already published.");
        }

        SetState(product, ListingStatus.Published, reason: null);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Listing {ProductId} ({Name}) approved", product.Id, product.Name);

        await NotifyVendorAsync(product, "approved", ct);

        return ListingResult.Ok(product, $"'{product.Name}' was approved and is now live.");
    }

    public async Task<ListingResult> RejectAsync(int productId, string reason, CancellationToken ct = default)
    {
        var product = await _db.Products.Include(p => p.Shop).FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null)
        {
            return ListingResult.Fail("That listing no longer exists.");
        }

        if (product.IsDeleted)
        {
            return ListingResult.Fail("That listing has been deleted.");
        }

        var trimmed = reason?.Trim();
        SetState(product, ListingStatus.Rejected,
            reason: string.IsNullOrWhiteSpace(trimmed) ? "Not approved by the marketplace." : trimmed);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Listing {ProductId} ({Name}) rejected: {Reason}", product.Id, product.Name, product.ModerationReason);

        await NotifyVendorAsync(product, "rejected", ct);

        return ListingResult.Ok(product, $"'{product.Name}' was rejected.");
    }

    public async Task<ListingResult> WithdrawAsync(int productId, string? reason, CancellationToken ct = default)
    {
        var product = await _db.Products.Include(p => p.Shop).FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null)
        {
            return ListingResult.Fail("That listing no longer exists.");
        }

        if (product.ModerationStatus != ListingStatus.Published)
        {
            return ListingResult.Fail("Only a live listing can be withdrawn.");
        }

        var trimmed = reason?.Trim();
        SetState(product, ListingStatus.Draft,
            reason: string.IsNullOrWhiteSpace(trimmed) ? null : trimmed);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Listing {ProductId} ({Name}) withdrawn by the marketplace", product.Id, product.Name);

        await NotifyVendorAsync(product, "withdrawn", ct);

        return ListingResult.Ok(product, $"'{product.Name}' was withdrawn and is no longer live.");
    }

    public async Task<ListingResult> UnpublishAsync(int productId, int shopId, CancellationToken ct = default)
    {
        var product = await _db.Products.Include(p => p.Shop).FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null)
        {
            return ListingResult.Fail("That listing no longer exists.");
        }

        if (product.ShopId != shopId)
        {
            return ListingResult.Fail("That listing belongs to another shop.");
        }

        if (product.ModerationStatus != ListingStatus.Published)
        {
            return ListingResult.Fail("Only a live listing can be taken off sale.");
        }

        SetState(product, ListingStatus.Draft, reason: null);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Listing {ProductId} ({Name}) taken off sale by its shop", product.Id, product.Name);

        return ListingResult.Ok(product, $"'{product.Name}' was taken off sale.");
    }

    public async Task<IReadOnlyList<ListingQueueRow>> GetQueueAsync(
        ListingStatus? status, string? search, CancellationToken ct = default)
    {
        var query = _db.Products.AsNoTracking()
            .Include(p => p.Shop)
            .Include(p => p.Variants)
            .Where(p => !p.IsDeleted);

        if (status is { } wanted)
        {
            query = query.Where(p => p.ModerationStatus == wanted);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim().Replace("%", "\\%").Replace("_", "\\_")}%";
            query = query.Where(p => EF.Functions.Like(p.Name, term, "\\"));
        }

        return await query
            .OrderByDescending(p => p.UpdatedUtc)
            .Select(p => new ListingQueueRow
            {
                Id = p.Id,
                Name = p.Name,
                Slug = p.Slug,
                ShopName = p.Shop != null ? p.Shop.Name : "Unknown shop",
                Status = p.ModerationStatus,
                Reason = p.ModerationReason,
                ModeratedUtc = p.ModeratedUtc,
                UpdatedUtc = p.UpdatedUtc,
                VariantCount = p.Variants.Count,
                // Cast to double inside the projection: SQLite cannot aggregate a decimal column
                // (see CatalogService for the same workaround), and these values are only shown.
                Price = p.Variants.Any() ? (decimal)p.Variants.Min(v => (double)v.Price) : 0m,
                Stock = p.Variants.Where(v => v.IsActive).Sum(v => (int?)v.StockQuantity) ?? 0
            })
            .ToListAsync(ct);
    }

    public Task<int> CountPendingAsync(CancellationToken ct = default) =>
        _db.Products.CountAsync(
            p => !p.IsDeleted && p.ModerationStatus == ListingStatus.PendingReview, ct);

    /// <summary>
    /// Moves a listing to a state and keeps the visible flag in step with it. Every transition goes
    /// through here, so a listing can never be visible without having been published.
    /// </summary>
    private static void SetState(Product product, ListingStatus status, string? reason)
    {
        product.ModerationStatus = status;
        product.ModerationReason = reason;
        product.IsPublished = status == ListingStatus.Published;
        product.ModeratedUtc = DateTime.UtcNow;
        product.UpdatedUtc = DateTime.UtcNow;
    }

    /// <summary>Tells the vendor's shop owner what the marketplace decided.</summary>
    private async Task NotifyVendorAsync(Product product, string decision, CancellationToken ct)
    {
        if (_emails is null)
        {
            return;
        }

        var ownerEmail = await (
                from p in _db.Products.AsNoTracking()
                join s in _db.Shops.AsNoTracking() on p.ShopId equals s.Id
                join u in _db.Users.AsNoTracking() on s.OwnerUserId equals u.Id
                where p.Id == product.Id
                select u.Email)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(ownerEmail))
        {
            return;
        }

        try
        {
            await _emails.NotifyListingDecisionAsync(product, ownerEmail, decision, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A mail server that is down must not undo a decision that has already been recorded.
            _logger.LogWarning(ex, "Could not notify the vendor about listing {ProductId}", product.Id);
        }
    }
}
