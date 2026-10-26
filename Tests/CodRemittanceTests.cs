using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// Carrier cash-on-delivery remittances: a batch the carrier says it paid, reconciled against the
/// orders we expected cash from, with the vendor ledger adjusted when an order never produced cash.
/// </summary>
public class CodRemittanceTests
{
    private static async Task<ApplicationDbContext> SeededDb()
    {
        var db = TestSupport.NewDb();
        db.Shops.Add(new Shop
        {
            Id = 1,
            Name = "Shop 1",
            Slug = "shop-1",
            OwnerUserId = "owner-1",
            Status = ShopStatus.Approved,
            CommissionRate = 0.10m
        });
        db.Users.Add(TestSupport.User("owner-1", "owner@example.com"));
        await db.SaveChangesAsync();
        return db;
    }

    private static Order DeliveredCodOrder(int id, string number, decimal codAmount, bool returned = false)
    {
        var order = TestSupport.Order(id, number);
        order.PaymentMethod = PaymentMethod.CashOnDelivery;
        order.CodAmount = codAmount;
        order.GrandTotal = codAmount;
        order.Subtotal = codAmount - 4.50m;
        order.CollectionStatus = returned ? CollectionStatus.CollectionFailed : CollectionStatus.Collected;
        order.Status = returned ? OrderStatus.Cancelled : OrderStatus.Delivered;
        order.DeliveredUtc = DateTime.UtcNow;
        return order;
    }

    [Fact]
    public async Task A_remittance_records_expected_and_actual_per_line()
    {
        await using var db = await SeededDb();
        var order = DeliveredCodOrder(1, "ORD-1", 100m);
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var ledger = new VendorLedgerService(db, NullLogger<VendorLedgerService>.Instance);
        var service = new CodRemittanceService(db, ledger, NullLogger<CodRemittanceService>.Instance);

        var result = await service.CreateAsync(new CarrierRemittanceCreateInput
        {
            Carrier = "libanpost",
            Reference = "BATCH-1",
            Lines = new()
            {
                new CarrierRemittanceLineInput { OrderNumber = "ORD-1", ActualAmount = 95m, Note = "short 5" }
            }
        }, "admin", default);

        Assert.True(result.Success);
        Assert.NotNull(result.Remittance);
        var r = result.Remittance!;
        Assert.Single(r.Lines);
        Assert.Equal(100m, r.Lines.First().ExpectedAmount);
        Assert.Equal(95m, r.Lines.First().ActualAmount);
        Assert.Equal(-5m, r.Lines.First().Variance);
        Assert.Equal(95m, r.ActualAmount);
    }

    [Fact]
    public async Task Reconciling_a_matched_remittance_marks_it_reconciled()
    {
        await using var db = await SeededDb();
        var order = DeliveredCodOrder(1, "ORD-1", 100m);
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var ledger = new VendorLedgerService(db, NullLogger<VendorLedgerService>.Instance);
        var service = new CodRemittanceService(db, ledger, NullLogger<CodRemittanceService>.Instance);

        var created = await service.CreateAsync(new CarrierRemittanceCreateInput
        {
            Carrier = "libanpost",
            Reference = "BATCH-2",
            Lines = new() { new CarrierRemittanceLineInput { OrderNumber = "ORD-1", ActualAmount = 100m } }
        }, "admin", default);

        var reconciled = await service.ReconcileAsync(created.Remittance!.Id, "admin", default);
        Assert.True(reconciled.Success);
        Assert.Equal(RemittanceStatus.Reconciled, reconciled.Remittance!.Status);
        Assert.Equal(RemittanceLineStatus.Matched, reconciled.Remittance.Lines.First().Status);
        Assert.Equal(0m, reconciled.Remittance.Variance);
    }

    [Fact]
    public async Task Reconciling_a_short_remittance_marks_it_disputed_without_touching_the_vendor_ledger()
    {
        await using var db = await SeededDb();
        var order = DeliveredCodOrder(1, "ORD-1", 100m);
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        // Credit the vendor for the sale, as delivery would have.
        var ledger = new VendorLedgerService(db, NullLogger<VendorLedgerService>.Instance);
        Assert.True(await ledger.RecordSaleAsync(order, "delivered", default));

        var service = new CodRemittanceService(db, ledger, NullLogger<CodRemittanceService>.Instance);
        var created = await service.CreateAsync(new CarrierRemittanceCreateInput
        {
            Carrier = "libanpost",
            Reference = "BATCH-3",
            Lines = new() { new CarrierRemittanceLineInput { OrderNumber = "ORD-1", ActualAmount = 90m } }
        }, "admin", default);

        var reconciled = await service.ReconcileAsync(created.Remittance!.Id, "admin", default);
        Assert.True(reconciled.Success);
        Assert.Equal(RemittanceStatus.Disputed, reconciled.Remittance!.Status);
        Assert.Equal(RemittanceLineStatus.Short, reconciled.Remittance.Lines.First().Status);

        // Vendor credit survives the carrier shortfall.
        var entries = await db.VendorLedgerEntries.AsNoTracking().Where(e => e.OrderId == order.Id).ToListAsync();
        Assert.Contains(entries, e => e.Type == VendorLedgerEntryType.Sale);
        Assert.DoesNotContain(entries, e => e.Type == VendorLedgerEntryType.Reversal);
    }

    [Fact]
    public async Task Reconciling_a_returned_order_reverses_the_vendor_credit()
    {
        await using var db = await SeededDb();
        var order = DeliveredCodOrder(1, "ORD-1", 100m, returned: true);
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var ledger = new VendorLedgerService(db, NullLogger<VendorLedgerService>.Instance);
        Assert.True(await ledger.RecordSaleAsync(order, "delivered", default));

        var service = new CodRemittanceService(db, ledger, NullLogger<CodRemittanceService>.Instance);
        var created = await service.CreateAsync(new CarrierRemittanceCreateInput
        {
            Carrier = "libanpost",
            Reference = "BATCH-4",
            Lines = new() { new CarrierRemittanceLineInput { OrderNumber = "ORD-1", ActualAmount = 0m } }
        }, "admin", default);

        var reconciled = await service.ReconcileAsync(created.Remittance!.Id, "admin", default);
        Assert.True(reconciled.Success);
        Assert.Equal(RemittanceLineStatus.Returned, reconciled.Remittance!.Lines.First().Status);

        var entries = await db.VendorLedgerEntries.AsNoTracking().Where(e => e.OrderId == order.Id).ToListAsync();
        Assert.Contains(entries, e => e.Type == VendorLedgerEntryType.Sale);
        Assert.Contains(entries, e => e.Type == VendorLedgerEntryType.Reversal);
    }

    [Fact]
    public async Task Duplicate_carrier_reference_is_rejected()
    {
        await using var db = await SeededDb();
        var ledger = new VendorLedgerService(db, NullLogger<VendorLedgerService>.Instance);
        var service = new CodRemittanceService(db, ledger, NullLogger<CodRemittanceService>.Instance);

        await service.CreateAsync(new CarrierRemittanceCreateInput
        {
            Carrier = "libanpost", Reference = "DUP-1"
        }, "admin", default);

        var second = await service.CreateAsync(new CarrierRemittanceCreateInput
        {
            Carrier = "libanpost", Reference = "DUP-1"
        }, "admin", default);

        Assert.False(second.Success);
    }

    [Fact]
    public async Task Auto_match_pulls_delivered_cod_orders_in_the_window()
    {
        await using var db = await SeededDb();
        var o1 = DeliveredCodOrder(1, "ORD-1", 50m);
        var o2 = DeliveredCodOrder(2, "ORD-2", 75m);
        db.Orders.AddRange(o1, o2);
        await db.SaveChangesAsync();

        var ledger = new VendorLedgerService(db, NullLogger<VendorLedgerService>.Instance);
        var service = new CodRemittanceService(db, ledger, NullLogger<CodRemittanceService>.Instance);

        var created = await service.CreateAsync(new CarrierRemittanceCreateInput
        {
            Carrier = "libanpost", Reference = "BATCH-5"
        }, "admin", default);

        var matched = await service.AutoMatchAsync(
            created.Remittance!.Id, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), "libanpost", default);

        Assert.True(matched.Success);
        var reloaded = await service.GetAsync(created.Remittance.Id, default);
        Assert.Equal(2, reloaded!.Lines.Count);
    }

    [Fact]
    public async Task Closing_a_reconciled_remittance_marks_it_closed()
    {
        await using var db = await SeededDb();
        var order = DeliveredCodOrder(1, "ORD-1", 100m);
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var ledger = new VendorLedgerService(db, NullLogger<VendorLedgerService>.Instance);
        var service = new CodRemittanceService(db, ledger, NullLogger<CodRemittanceService>.Instance);

        var created = await service.CreateAsync(new CarrierRemittanceCreateInput
        {
            Carrier = "libanpost", Reference = "BATCH-6",
            Lines = new() { new CarrierRemittanceLineInput { OrderNumber = "ORD-1", ActualAmount = 100m } }
        }, "admin", default);

        await service.ReconcileAsync(created.Remittance!.Id, "admin", default);
        var closed = await service.CloseAsync(created.Remittance.Id, "admin", "done", default);
        Assert.True(closed.Success);
        Assert.Equal(RemittanceStatus.Closed, closed.Remittance!.Status);
        Assert.NotNull(closed.Remittance.ClosedUtc);
    }
}
