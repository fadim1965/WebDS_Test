using Microsoft.EntityFrameworkCore;
using WebDS_Test.Data;
using WebDS_Test.Domain;

namespace WebDS_Test.Services;

public sealed class CarrierRemittanceCreateInput
{
    public string Carrier { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public string? BankReference { get; set; }
    public DateTime RemittanceDateUtc { get; set; } = DateTime.UtcNow;
    public string? Notes { get; set; }
    public decimal Tolerance { get; set; } = 0.01m;
    public List<CarrierRemittanceLineInput> Lines { get; set; } = new();
}

public sealed class CarrierRemittanceLineInput
{
    public string? OrderNumber { get; set; }
    public decimal ActualAmount { get; set; }
    public string? Note { get; set; }
}

public sealed record RemittanceResult(bool Success, string Message, CarrierRemittance? Remittance = null);

public interface ICodRemittanceService
{
    Task<IReadOnlyList<CarrierRemittance>> ListAsync(RemittanceStatus? status, CancellationToken ct = default);
    Task<CarrierRemittance?> GetAsync(int id, CancellationToken ct = default);
    Task<RemittanceResult> CreateAsync(CarrierRemittanceCreateInput input, string createdBy, CancellationToken ct = default);
    Task<RemittanceResult> UpdateLineAsync(int remittanceId, int lineId, decimal actualAmount, string? note, CancellationToken ct = default);
    Task<RemittanceResult> AutoMatchAsync(int remittanceId, DateTime fromUtc, DateTime toUtc, string? carrier, CancellationToken ct = default);
    Task<RemittanceResult> ReconcileAsync(int id, string actor, CancellationToken ct = default);
    Task<RemittanceResult> DisputeAsync(int id, string actor, string note, CancellationToken ct = default);
    Task<RemittanceResult> CloseAsync(int id, string actor, string? note, CancellationToken ct = default);
    Task<RemittanceResult> DeleteAsync(int id, CancellationToken ct = default);
}

public sealed class CodRemittanceService : ICodRemittanceService
{
    private readonly ApplicationDbContext _db;
    private readonly IVendorLedgerService _ledger;
    private readonly ILogger<CodRemittanceService> _logger;

    public CodRemittanceService(
        ApplicationDbContext db,
        IVendorLedgerService ledger,
        ILogger<CodRemittanceService> logger)
    {
        _db = db;
        _ledger = ledger;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CarrierRemittance>> ListAsync(RemittanceStatus? status, CancellationToken ct = default)
    {
        var q = _db.CarrierRemittances.AsNoTracking().Include(r => r.Lines).AsQueryable();
        if (status is { } s)
        {
            q = q.Where(r => r.Status == s);
        }
        return await q.OrderByDescending(r => r.ReceivedUtc).ToListAsync(ct);
    }

    public async Task<CarrierRemittance?> GetAsync(int id, CancellationToken ct = default)
    {
        return await _db.CarrierRemittances
            .Include(r => r.Lines).ThenInclude(l => l.Order)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<RemittanceResult> CreateAsync(CarrierRemittanceCreateInput input, string createdBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Carrier))
        {
            return new RemittanceResult(false, "Carrier is required.");
        }
        if (string.IsNullOrWhiteSpace(input.Reference))
        {
            return new RemittanceResult(false, "The carrier's batch / statement reference is required.");
        }

        var exists = await _db.CarrierRemittances.AnyAsync(
            r => r.Carrier == input.Carrier && r.Reference == input.Reference, ct);
        if (exists)
        {
            return new RemittanceResult(false, "A remittance with that carrier and reference already exists.");
        }

        var remittance = new CarrierRemittance
        {
            Carrier = input.Carrier.Trim(),
            Reference = input.Reference.Trim(),
            BankReference = string.IsNullOrWhiteSpace(input.BankReference) ? null : input.BankReference.Trim(),
            RemittanceDateUtc = input.RemittanceDateUtc == default ? DateTime.UtcNow : input.RemittanceDateUtc,
            ReceivedUtc = DateTime.UtcNow,
            ReceivedBy = createdBy,
            Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim(),
            Status = RemittanceStatus.Received,
            Tolerance = input.Tolerance > 0m ? input.Tolerance : 0.01m,
            ActualAmount = input.Lines.Sum(l => l.ActualAmount)
        };

        foreach (var li in input.Lines)
        {
            if (string.IsNullOrWhiteSpace(li.OrderNumber))
            {
                continue;
            }

            var order = await _db.Orders.AsNoTracking()
                .FirstOrDefaultAsync(o => o.OrderNumber == li.OrderNumber!.Trim(), ct);

            string? tracking = null;
            if (order is not null)
            {
                tracking = await _db.OrderShipments.AsNoTracking()
                    .Where(s => s.OrderId == order.Id)
                    .OrderByDescending(s => s.CreatedUtc)
                    .Select(s => s.TrackingNumber)
                    .FirstOrDefaultAsync(ct);
            }

            remittance.Lines.Add(new CarrierRemittanceLine
            {
                OrderId = order?.Id,
                TrackingNumber = tracking,
                ExpectedAmount = order?.CodAmount ?? 0m,
                ActualAmount = li.ActualAmount,
                Variance = li.ActualAmount - (order?.CodAmount ?? 0m),
                Note = string.IsNullOrWhiteSpace(li.Note) ? null : li.Note.Trim(),
                Status = RemittanceLineStatus.Matched
            });
        }

        _db.CarrierRemittances.Add(remittance);
        await _db.SaveChangesAsync(ct);

        return new RemittanceResult(true, "Remittance recorded.", remittance);
    }

    public async Task<RemittanceResult> UpdateLineAsync(int remittanceId, int lineId, decimal actualAmount, string? note, CancellationToken ct = default)
    {
        var remittance = await _db.CarrierRemittances.Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == remittanceId, ct);
        if (remittance is null)
        {
            return new RemittanceResult(false, "Remittance not found.");
        }
        if (remittance.Status is RemittanceStatus.Closed)
        {
            return new RemittanceResult(false, "A closed remittance cannot be edited.");
        }

        var line = remittance.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null)
        {
            return new RemittanceResult(false, "Line not found.");
        }

        line.ActualAmount = actualAmount;
        line.Variance = actualAmount - line.ExpectedAmount;
        line.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        line.Status = RemittanceLineStatus.Matched;

        await _db.SaveChangesAsync(ct);
        return new RemittanceResult(true, "Line updated.", remittance);
    }

    public async Task<RemittanceResult> AutoMatchAsync(int remittanceId, DateTime fromUtc, DateTime toUtc, string? carrier, CancellationToken ct = default)
    {
        var remittance = await _db.CarrierRemittances.Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == remittanceId, ct);
        if (remittance is null)
        {
            return new RemittanceResult(false, "Remittance not found.");
        }
        if (remittance.Status is RemittanceStatus.Closed or RemittanceStatus.Reconciled)
        {
            return new RemittanceResult(false, "Only a draft or received remittance can be auto-matched.");
        }

        var existingOrderIds = remittance.Lines.Where(l => l.OrderId.HasValue).Select(l => l.OrderId!.Value).ToHashSet();

        var query = _db.Orders.AsNoTracking()
            .Where(o => o.PaymentMethod == PaymentMethod.CashOnDelivery
                     && o.CollectionStatus == CollectionStatus.Collected
                     && o.DeliveredUtc != null
                     && o.DeliveredUtc >= fromUtc
                     && o.DeliveredUtc <= toUtc);

        if (!string.IsNullOrWhiteSpace(carrier))
        {
            query = query.Where(o => o.Carrier == carrier || true);
        }

        var orders = await query.OrderBy(o => o.DeliveredUtc).ToListAsync(ct);

        var added = 0;
        foreach (var order in orders)
        {
            if (existingOrderIds.Contains(order.Id))
            {
                continue;
            }

            var tracking = await _db.OrderShipments.AsNoTracking()
                .Where(s => s.OrderId == order.Id)
                .OrderByDescending(s => s.CreatedUtc)
                .Select(s => s.TrackingNumber)
                .FirstOrDefaultAsync(ct);

            remittance.Lines.Add(new CarrierRemittanceLine
            {
                OrderId = order.Id,
                TrackingNumber = tracking,
                ExpectedAmount = order.CodAmount,
                ActualAmount = 0m,
                Variance = -order.CodAmount,
                Status = RemittanceLineStatus.Matched
            });
            added++;
        }

        await _db.SaveChangesAsync(ct);
        return new RemittanceResult(true, $"Added {added} delivered cash-on-delivery order(s).", remittance);
    }

    public async Task<RemittanceResult> ReconcileAsync(int id, string actor, CancellationToken ct = default)
    {
        var remittance = await _db.CarrierRemittances
            .Include(r => r.Lines).ThenInclude(l => l.Order)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (remittance is null)
        {
            return new RemittanceResult(false, "Remittance not found.");
        }
        if (remittance.Status is RemittanceStatus.Closed)
        {
            return new RemittanceResult(false, "A closed remittance cannot be reconciled again.");
        }

        var disputed = false;
        foreach (var line in remittance.Lines)
        {
            if (line.OrderId is null || line.Order is null)
            {
                line.Status = RemittanceLineStatus.Unmatched;
                line.Variance = line.ActualAmount - line.ExpectedAmount;
                disputed = true;
                continue;
            }

            var order = line.Order;

            // An order that was returned or failed delivery produced no cash: the vendor's credit
            // for it (recorded at delivery) has to be taken back.
            var returned = order.Status == OrderStatus.Cancelled
                        || order.CollectionStatus is CollectionStatus.CollectionFailed or CollectionStatus.Refunded;

            if (returned)
            {
                line.ExpectedAmount = 0m;
                line.Variance = line.ActualAmount;
                line.Status = line.ActualAmount == 0m
                    ? RemittanceLineStatus.Returned
                    : RemittanceLineStatus.Over;

                if (line.Status == RemittanceLineStatus.Over)
                {
                    disputed = true;
                }

                if (line.SettledUtc is null)
                {
                    var reversed = await _ledger.RecordReversalAsync(
                        order,
                        $"COD remittance {remittance.Carrier}/{remittance.Reference}: order returned or failed delivery, no cash collected.",
                        ct);
                    _logger.LogInformation(
                        "Reconcile {Ref}: reversal for order {Order} returned {Reversed}.",
                        remittance.Reference, order.OrderNumber, reversed);
                    line.SettledUtc = DateTime.UtcNow;
                }
                continue;
            }

            if (line.ExpectedAmount == 0m && order.CodAmount > 0m)
            {
                line.ExpectedAmount = order.CodAmount;
            }

            line.Variance = line.ActualAmount - line.ExpectedAmount;

            if (Math.Abs(line.Variance) <= remittance.Tolerance)
            {
                line.Status = RemittanceLineStatus.Matched;
            }
            else if (line.ActualAmount < line.ExpectedAmount - remittance.Tolerance)
            {
                line.Status = RemittanceLineStatus.Short;
                disputed = true;
            }
            else
            {
                line.Status = RemittanceLineStatus.Over;
                disputed = true;
            }
        }

        remittance.ExpectedAmount = remittance.Lines.Sum(l => l.ExpectedAmount);
        remittance.ActualAmount = remittance.Lines.Sum(l => l.ActualAmount);
        remittance.Variance = remittance.Lines.Sum(l => l.Variance);
        remittance.Status = disputed ? RemittanceStatus.Disputed : RemittanceStatus.Reconciled;
        remittance.ReconciledUtc = DateTime.UtcNow;
        remittance.ReconciledBy = actor;

        await _db.SaveChangesAsync(ct);

        var msg = disputed
            ? $"Reconciled with variance {remittance.Variance:N2}. Marked disputed."
            : "Reconciled: all lines match within tolerance.";

        return new RemittanceResult(true, msg, remittance);
    }

    public async Task<RemittanceResult> DisputeAsync(int id, string actor, string note, CancellationToken ct = default)
    {
        var remittance = await _db.CarrierRemittances.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (remittance is null)
        {
            return new RemittanceResult(false, "Remittance not found.");
        }
        if (remittance.Status == RemittanceStatus.Closed)
        {
            return new RemittanceResult(false, "A closed remittance cannot be disputed.");
        }

        remittance.Status = RemittanceStatus.Disputed;
        if (!string.IsNullOrWhiteSpace(note))
        {
            remittance.Notes = string.IsNullOrWhiteSpace(remittance.Notes)
                ? note.Trim()
                : remittance.Notes + "\n" + note.Trim();
        }
        await _db.SaveChangesAsync(ct);
        return new RemittanceResult(true, "Marked disputed.", remittance);
    }

    public async Task<RemittanceResult> CloseAsync(int id, string actor, string? note, CancellationToken ct = default)
    {
        var remittance = await _db.CarrierRemittances.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (remittance is null)
        {
            return new RemittanceResult(false, "Remittance not found.");
        }
        if (remittance.Status == RemittanceStatus.Draft)
        {
            return new RemittanceResult(false, "A draft cannot be closed; receive or reconcile it first.");
        }

        remittance.Status = RemittanceStatus.Closed;
        remittance.ClosedUtc = DateTime.UtcNow;
        remittance.ClosedBy = actor;
        if (!string.IsNullOrWhiteSpace(note))
        {
            remittance.Notes = string.IsNullOrWhiteSpace(remittance.Notes)
                ? note.Trim()
                : remittance.Notes + "\n" + note.Trim();
        }
        await _db.SaveChangesAsync(ct);
        return new RemittanceResult(true, "Closed.", remittance);
    }

    public async Task<RemittanceResult> DeleteAsync(int id, CancellationToken ct = default)
    {
        var remittance = await _db.CarrierRemittances.Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (remittance is null)
        {
            return new RemittanceResult(false, "Remittance not found.");
        }
        if (remittance.Status is RemittanceStatus.Reconciled or RemittanceStatus.Closed)
        {
            return new RemittanceResult(false, "A reconciled or closed remittance cannot be deleted.");
        }

        _db.CarrierRemittances.Remove(remittance);
        await _db.SaveChangesAsync(ct);
        return new RemittanceResult(true, "Deleted.");
    }
}
