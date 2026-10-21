using Microsoft.EntityFrameworkCore;

namespace WebDS_Test.Services;

/// <summary>Result of a manual inventory adjustment.</summary>
public sealed record StockAdjustmentResult(bool Success, string Message, int NewStock = 0)
{
    public static StockAdjustmentResult Ok(string message, int newStock) => new(true, message, newStock);
    public static StockAdjustmentResult Fail(string message) => new(false, message);
}

/// <summary>Administrative inventory control. Every change writes a stock movement.</summary>
public interface IInventoryService
{
    /// <summary>Applies a signed delta to a variant and records the movement.</summary>
    Task<StockAdjustmentResult> AdjustAsync(
        int variantId, int delta, StockMovementReason reason, string? note = null,
        string? actor = null, CancellationToken ct = default);

    /// <summary>Sets an absolute stock level, recording the difference as an adjustment.</summary>
    Task<StockAdjustmentResult> SetStockAsync(
        int variantId, int newQuantity, string? note = null, string? actor = null, CancellationToken ct = default);

    Task<IReadOnlyList<ProductVariant>> GetLowStockAsync(int threshold = 5, CancellationToken ct = default);

    Task<IReadOnlyList<StockMovement>> GetRecentMovementsAsync(int take = 50, CancellationToken ct = default);
}

public sealed class InventoryService : IInventoryService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<InventoryService> _logger;

    public InventoryService(ApplicationDbContext db, ILogger<InventoryService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<StockAdjustmentResult> AdjustAsync(
        int variantId, int delta, StockMovementReason reason, string? note = null,
        string? actor = null, CancellationToken ct = default)
    {
        var variant = await _db.ProductVariants
            .Include(v => v.Product)
            .FirstOrDefaultAsync(v => v.Id == variantId, ct);

        if (variant is null)
        {
            return StockAdjustmentResult.Fail("Variant not found.");
        }

        var updated = variant.StockQuantity + delta;
        if (updated < 0)
        {
            return StockAdjustmentResult.Fail(
                $"Cannot reduce stock below zero (currently {variant.StockQuantity}).");
        }

        variant.StockQuantity = updated;

        _db.StockMovements.Add(new StockMovement
        {
            ProductVariantId = variant.Id,
            QuantityDelta = delta,
            StockAfter = updated,
            Reason = reason,
            Note = note,
            CreatedBy = actor,
            CreatedUtc = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Stock for {Sku} adjusted by {Delta} to {Stock} ({Reason})",
            variant.Sku, delta, updated, reason);

        return StockAdjustmentResult.Ok(
            $"{variant.Product?.Name} ({variant.Sku}) is now {updated} in stock.", updated);
    }

    public async Task<StockAdjustmentResult> SetStockAsync(
        int variantId, int newQuantity, string? note = null, string? actor = null, CancellationToken ct = default)
    {
        if (newQuantity < 0)
        {
            return StockAdjustmentResult.Fail("Stock cannot be negative.");
        }

        var variant = await _db.ProductVariants.FirstOrDefaultAsync(v => v.Id == variantId, ct);
        if (variant is null)
        {
            return StockAdjustmentResult.Fail("Variant not found.");
        }

        var delta = newQuantity - variant.StockQuantity;
        if (delta == 0)
        {
            return StockAdjustmentResult.Ok("Stock level unchanged.", variant.StockQuantity);
        }

        return await AdjustAsync(variantId, delta, StockMovementReason.Adjustment, note, actor, ct);
    }

    public async Task<IReadOnlyList<ProductVariant>> GetLowStockAsync(int threshold = 5, CancellationToken ct = default) =>
        await _db.ProductVariants.AsNoTracking()
            .Include(v => v.Product)
            .Where(v => v.IsActive && v.StockQuantity <= threshold)
            .OrderBy(v => v.StockQuantity)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<StockMovement>> GetRecentMovementsAsync(int take = 50, CancellationToken ct = default) =>
        await _db.StockMovements.AsNoTracking()
            .Include(m => m.ProductVariant)
                .ThenInclude(v => v!.Product)
            .OrderByDescending(m => m.CreatedUtc)
            .Take(take)
            .ToListAsync(ct);
}
