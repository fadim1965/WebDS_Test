using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services;
using WebDS_Test.Services.Email;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// The vendor money trail: a sale is credited once, with commission withheld; a refund or
/// cancellation reverses it; and a payout settles whatever is left without rewriting history.
/// </summary>
/// <remarks>
/// These are the rules that decide what a real shop is paid, so the tests are deliberately blunt
/// about the arithmetic (49.00 at 10% is 44.10, not "about 44") and about idempotency, because a
/// webhook redelivery is the normal case, not the exceptional one.
/// </remarks>
public class VendorPayoutTests
{
    // ------------------------------------------------------------------
    // Fixtures
    // ------------------------------------------------------------------

    private static Shop Shop(int id = 1, decimal rate = 0.10m) => new()
    {
        Id = id,
        Name = $"Shop {id}",
        Slug = $"shop-{id}",
        OwnerUserId = $"owner-{id}",
        CommissionRate = rate,
        Status = ShopStatus.Approved
    };

    private static Order Order(int id, decimal subtotal, int shopId = 1)
    {
        var order = TestSupport.Order(id: id, number: $"ORD-2026-{id:D4}");
        order.ShopId = shopId;
        order.Subtotal = subtotal;
        return order;
    }

    private static VendorLedgerService Ledger(ApplicationDbContext db) =>
        new(db, NullLogger<VendorLedgerService>.Instance);

    private static PayoutService Payouts(ApplicationDbContext db, decimal minimum = 0m) =>
        new(db,
            TestSupport.Options(new PayoutOptions { MinimumAmount = minimum }),
            NullLogger<PayoutService>.Instance);

    private static async Task<ApplicationDbContext> Seeded(params Shop[] shops)
    {
        var db = TestSupport.NewDb();
        db.Shops.AddRange(shops);
        await db.SaveChangesAsync();
        return db;
    }

    // ------------------------------------------------------------------
    // Crediting a collected sale
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_collected_sale_is_credited_with_commission_withheld()
    {
        await using var db = await Seeded(Shop(rate: 0.10m));
        var order = Order(1, 49.00m);
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var credited = await Ledger(db).RecordSaleAsync(order, "paid");

        Assert.True(credited);

        var entry = await db.VendorLedgerEntries.SingleAsync();
        Assert.Equal(VendorLedgerEntryType.Sale, entry.Type);
        Assert.Equal(1, entry.ShopId);
        Assert.Equal(1, entry.OrderId);
        Assert.Equal(49.00m, entry.GrossAmount);
        Assert.Equal(4.90m, entry.CommissionAmount);
        Assert.Equal(44.10m, entry.NetAmount);
        Assert.Null(entry.SettledUtc);
    }

    [Fact]
    public async Task Commission_comes_from_the_shops_own_rate()
    {
        await using var db = await Seeded(Shop(rate: 0.20m));
        db.Orders.Add(Order(id: 1, subtotal: 49.00m));
        await db.SaveChangesAsync();

        await Ledger(db).RecordSaleAsync(await db.Orders.FirstAsync(), "paid");

        var entry = await db.VendorLedgerEntries.SingleAsync();
        Assert.Equal(9.80m, entry.CommissionAmount);
        Assert.Equal(39.20m, entry.NetAmount);
    }

    [Fact]
    public async Task A_missing_shop_falls_back_to_the_default_commission()
    {
        // No shop row exists, so the writer cannot read a rate. It must still withhold something
        // rather than credit the full subtotal by accident.
        var db = TestSupport.NewDb();
        await using var _ = db;
        db.Orders.Add(Order(1, 50.00m));
        await db.SaveChangesAsync();

        await Ledger(db).RecordSaleAsync(await db.Orders.FirstAsync(), "paid");

        var entry = await db.VendorLedgerEntries.SingleAsync();
        Assert.Equal(5.00m, entry.CommissionAmount);   // default 10%
        Assert.Equal(45.00m, entry.NetAmount);
    }

    [Fact]
    public async Task Crediting_the_same_order_twice_credits_once()
    {
        var db = await Seeded(Shop());
        await using var _ = db;
        db.Orders.Add(Order(1, 49.00m));
        await db.SaveChangesAsync();

        var ledger = Ledger(db);
        var order = await db.Orders.FirstAsync();

        Assert.True(await ledger.RecordSaleAsync(order, "first"));
        Assert.False(await ledger.RecordSaleAsync(order, "again"));

        Assert.Equal(1, await db.VendorLedgerEntries.CountAsync());
    }

    [Fact]
    public async Task A_shopless_order_is_never_credited()
    {
        var db = TestSupport.NewDb();
        await using var _ = db;
        db.Orders.Add(Order(1, 49.00m, shopId: 0));
        await db.SaveChangesAsync();

        var credited = await Ledger(db).RecordSaleAsync(await db.Orders.FirstAsync(), "paid");

        Assert.False(credited);
        Assert.Empty(await db.VendorLedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task A_commission_rate_above_one_cannot_flip_the_debt()
    {
        // A rate over 100% would make the net negative and read as "the vendor owes us". It is
        // clamped so the worst case is "the vendor is owed nothing", never a negative sale.
        var db = await Seeded(Shop(rate: 1.5m));
        await using var _ = db;
        db.Orders.Add(Order(1, 50.00m));
        await db.SaveChangesAsync();

        await Ledger(db).RecordSaleAsync(await db.Orders.FirstAsync(), "paid");

        var entry = await db.VendorLedgerEntries.SingleAsync();
        Assert.Equal(50.00m, entry.CommissionAmount);
        Assert.Equal(0.00m, entry.NetAmount);
    }

    // ------------------------------------------------------------------
    // Reversing
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_reversal_neutralises_the_sale_it_takes_back()
    {
        var db = await Seeded(Shop());
        await using var _ = db;
        db.Orders.Add(Order(1, 49.00m));
        await db.SaveChangesAsync();

        var ledger = Ledger(db);
        var order = await db.Orders.FirstAsync();

        await ledger.RecordSaleAsync(order, "collected");
        Assert.True(await ledger.RecordReversalAsync(order, "refunded"));

        var entries = await db.VendorLedgerEntries.ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal(0.00m, entries.Sum(e => e.NetAmount));
        Assert.Equal(0.00m, entries.Sum(e => e.GrossAmount));

        var reversal = entries.Single(e => e.Type == VendorLedgerEntryType.Reversal);
        Assert.Equal(-44.10m, reversal.NetAmount);
        Assert.Equal(-4.90m, reversal.CommissionAmount);
    }

    [Fact]
    public async Task Reversing_an_order_that_was_never_credited_does_nothing()
    {
        var db = await Seeded(Shop());
        await using var _ = db;
        db.Orders.Add(Order(1, 49.00m));
        await db.SaveChangesAsync();

        var reversed = await Ledger(db).RecordReversalAsync(await db.Orders.FirstAsync(), "cancelled");

        Assert.False(reversed);
        Assert.Empty(await db.VendorLedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task A_sale_can_only_be_reversed_once()
    {
        var db = await Seeded(Shop());
        await using var _ = db;
        db.Orders.Add(Order(1, 49.00m));
        await db.SaveChangesAsync();

        var ledger = Ledger(db);
        var order = await db.Orders.FirstAsync();

        await ledger.RecordSaleAsync(order, "collected");
        Assert.True(await ledger.RecordReversalAsync(order, "refunded"));
        Assert.False(await ledger.RecordReversalAsync(order, "refunded again"));

        Assert.Equal(1, await db.VendorLedgerEntries.CountAsync(e => e.Type == VendorLedgerEntryType.Reversal));
    }

    // ------------------------------------------------------------------
    // Outstanding balances
    // ------------------------------------------------------------------

    [Fact]
    public async Task Outstanding_is_the_sum_of_the_unsettled_entries()
    {
        var db = await Seeded(Shop());
        await using var _ = db;
        db.Orders.AddRange(Order(1, 49.00m), Order(2, 100.00m));
        await db.SaveChangesAsync();

        var ledger = Ledger(db);
        foreach (var order in await db.Orders.ToListAsync())
        {
            await ledger.RecordSaleAsync(order, "collected");
        }

        var outstanding = await Payouts(db).GetOutstandingAsync(1);

        Assert.NotNull(outstanding);
        Assert.Equal(2, outstanding!.EntryCount);
        Assert.Equal(149.00m, outstanding.Gross);
        Assert.Equal(14.90m, outstanding.Commission);
        Assert.Equal(134.10m, outstanding.Net);
        Assert.True(outstanding.IsPayable);
    }

    [Fact]
    public async Task Outstanding_for_an_unknown_shop_is_null()
    {
        var db = TestSupport.NewDb();
        await using var _ = db;

        Assert.Null(await Payouts(db).GetOutstandingAsync(999));
    }

    [Fact]
    public async Task Outstanding_by_shop_lists_the_biggest_balance_first()
    {
        var db = await Seeded(Shop(1, rate: 0m), Shop(2, rate: 0.5m));
        await using var _ = db;
        db.Orders.AddRange(Order(1, 49.00m, shopId: 1), Order(2, 49.00m, shopId: 2));
        await db.SaveChangesAsync();

        var ledger = Ledger(db);
        foreach (var order in await db.Orders.ToListAsync())
        {
            await ledger.RecordSaleAsync(order, "collected");
        }

        var balances = await Payouts(db).GetOutstandingByShopAsync();

        Assert.Equal(2, balances.Count);
        Assert.Equal(1, balances[0].ShopId);
        Assert.Equal(49.00m, balances[0].Net);
        Assert.Equal(24.50m, balances[1].Net);
    }

    // ------------------------------------------------------------------
    // Settling
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_payout_settles_every_outstanding_entry_and_keeps_a_statement()
    {
        var db = await Seeded(Shop());
        await using var _ = db;
        db.Orders.AddRange(Order(1, 49.00m), Order(2, 100.00m));
        await db.SaveChangesAsync();

        var ledger = Ledger(db);
        foreach (var order in await db.Orders.ToListAsync())
        {
            await ledger.RecordSaleAsync(order, "collected");
        }

        var result = await Payouts(db).CreatePayoutAsync(1, "Bank transfer", "October run", "admin@libanpost.example");

        Assert.True(result.Success);
        var payout = result.Payout!;
        Assert.Equal(1, payout.ShopId);
        Assert.Equal(2, payout.EntryCount);
        Assert.Equal(149.00m, payout.GrossAmount);
        Assert.Equal(14.90m, payout.CommissionAmount);
        Assert.Equal(134.10m, payout.NetAmount);
        Assert.Equal("October run", payout.Note);
        Assert.Equal("admin@libanpost.example", payout.CreatedBy);
        Assert.StartsWith($"PAY-{DateTime.UtcNow:yyyy}-", payout.Reference);

        // Every settled entry points back at the payout, which is what makes the statement
        // reconstructable without the payout row.
        var entries = await db.VendorLedgerEntries.ToListAsync();
        Assert.All(entries, e =>
        {
            Assert.NotNull(e.SettledUtc);
            Assert.Equal(payout.Reference, e.PayoutReference);
        });

        var lines = await Payouts(db).GetStatementLinesAsync(payout.Id);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public async Task A_settled_entry_drops_out_of_the_outstanding_balance()
    {
        var db = await Seeded(Shop());
        await using var _ = db;
        db.Orders.Add(Order(1, 49.00m));
        await db.SaveChangesAsync();

        await Ledger(db).RecordSaleAsync(await db.Orders.FirstAsync(), "collected");

        var payouts = Payouts(db);
        await Payouts(db).CreatePayoutAsync(1, null, null, null);

        var outstanding = await payouts.GetOutstandingAsync(1);
        Assert.NotNull(outstanding);
        Assert.Equal(0, outstanding!.EntryCount);
        Assert.Equal(0.00m, outstanding.Net);
        Assert.False(outstanding.IsPayable);
    }

    [Fact]
    public async Task A_payout_refuses_when_nothing_is_outstanding()
    {
        var db = await Seeded(Shop());
        await using var _ = db;

        var result = await Payouts(db).CreatePayoutAsync(1, null, null, "admin");

        Assert.False(result.Success);
        Assert.Empty(await db.VendorPayouts.ToListAsync());
    }

    [Fact]
    public async Task A_payout_refuses_a_balance_that_is_not_in_credit()
    {
        var db = await Seeded(Shop());
        await using var _ = db;
        db.Orders.Add(Order(1, 49.00m));
        await db.SaveChangesAsync();

        var ledger = Ledger(db);
        var order = await db.Orders.FirstAsync();
        await ledger.RecordSaleAsync(order, "collected");
        await ledger.RecordReversalAsync(order, "refunded");
        await db.SaveChangesAsync();

        var result = await Payouts(db).CreatePayoutAsync(1, null, null, "admin");

        Assert.False(result.Success);
        Assert.Contains("credit", result.Message);
    }

    [Fact]
    public async Task A_balance_below_the_minimum_is_not_paid_out()
    {
        var db = await Seeded(Shop());
        await using var _ = db;
        db.Orders.Add(Order(1, 49.00m));
        await db.SaveChangesAsync();

        await Ledger(db).RecordSaleAsync(await db.Orders.FirstAsync(), "collected");

        var result = await Payouts(db, minimum: 100m).CreatePayoutAsync(1, null, null, "admin");

        Assert.False(result.Success);
        Assert.Contains("minimum", result.Message);
        Assert.Empty(await db.VendorPayouts.ToListAsync());
    }

    [Fact]
    public async Task The_blank_method_falls_back_to_the_configured_default()
    {
        var db = await Seeded(Shop());
        await using var _ = db;
        db.Orders.Add(Order(1, 49.00m));
        await db.SaveChangesAsync();

        await Ledger(db).RecordSaleAsync(await db.Orders.FirstAsync(), "collected");

        var result = await Payouts(db).CreatePayoutAsync(1, "   ", null, "admin");

        Assert.True(result.Success);
        Assert.Equal("Bank transfer", result.Payout!.Method);
    }

    [Fact]
    public async Task Each_payout_only_covers_the_entries_still_unsettled()
    {
        var db = await Seeded(Shop());
        await using var _ = db;

        var ledger = Ledger(db);
        var payouts = Payouts(db);

        db.Orders.Add(Order(1, 49.00m));
        await db.SaveChangesAsync();
        await ledger.RecordSaleAsync(await db.Orders.FirstAsync(), "collected");

        var first = await payouts.CreatePayoutAsync(1, null, null, "admin");

        db.Orders.Add(Order(2, 100.00m));
        await db.SaveChangesAsync();
        await ledger.RecordSaleAsync(await db.Orders.SingleAsync(o => o.Id == 2), "collected");

        var second = await payouts.CreatePayoutAsync(1, null, null, "admin");

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal($"PAY-{DateTime.UtcNow:yyyy}-0001", first.Payout!.Reference);
        Assert.Equal($"PAY-{DateTime.UtcNow:yyyy}-0002", second.Payout!.Reference);
        Assert.Equal(1, second.Payout!.EntryCount);
        Assert.Equal(100.00m, second.Payout.GrossAmount);
    }

    [Fact]
    public async Task A_statement_contains_only_its_own_payouts_entries()
    {
        var db = await Seeded(Shop());
        await using var _ = db;

        var ledger = Ledger(db);
        var payouts = Payouts(db);

        db.Orders.Add(Order(1, 49.00m));
        await db.SaveChangesAsync();
        await ledger.RecordSaleAsync(await db.Orders.FirstAsync(), "collected");
        var first = await payouts.CreatePayoutAsync(1, null, null, "admin");

        db.Orders.Add(Order(2, 100.00m));
        await db.SaveChangesAsync();
        await ledger.RecordSaleAsync(await db.Orders.SingleAsync(o => o.Id == 2), "collected");
        await payouts.CreatePayoutAsync(1, null, null, "admin");

        var lines = await payouts.GetStatementLinesAsync(first.Payout!.Id);

        Assert.Single(lines);
        Assert.Equal(49.00m, lines[0].GrossAmount);
    }

    [Fact]
    public async Task A_reversal_after_a_settlement_comes_back_as_a_new_debt_to_the_marketplace()
    {
        var db = await Seeded(Shop());
        await using var _ = db;

        var ledger = Ledger(db);
        var payouts = Payouts(db);

        db.Orders.Add(Order(1, 49.00m));
        await db.SaveChangesAsync();

        var order = await db.Orders.FirstAsync();
        await ledger.RecordSaleAsync(order, "collected");
        var settled = await payouts.CreatePayoutAsync(1, null, null, "admin");

        // The vendor has already been paid for a sale that is now refunded, so the marketplace is
        // owed it back — the next settlement must show it.
        Assert.True(await ledger.RecordReversalAsync(order, "refunded"));

        var outstanding = await payouts.GetOutstandingAsync(1);
        Assert.Equal(-44.10m, outstanding!.Net);
        Assert.False(outstanding.IsPayable);
        Assert.NotNull(settled.Payout);
    }

    // ------------------------------------------------------------------
    // Wiring: the order lifecycle feeds the ledger
    // ------------------------------------------------------------------

    private static OrderService Orders(ApplicationDbContext db, IVendorLedgerService ledger)
    {
        var payments = new Mock<IPaymentGateway>(MockBehavior.Loose);
        payments.Setup(p => p.RefundAsync(It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PaymentReceipt(true, "refund-1"));

        var emails = new Mock<IEmailService>(MockBehavior.Loose);
        emails.Setup(e => e.NotifyOrderCancelledAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);
        emails.Setup(e => e.NotifyOrderShippedAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        return new OrderService(
            db,
            payments.Object,
            emails.Object,
            TestSupport.Options(TestSupport.Store()),
            NullLogger<OrderService>.Instance,
            coupons: null,
            ledger: ledger);
    }

    [Fact]
    public async Task Capturing_a_card_payment_credits_the_vendor()
    {
        var db = await Seeded(Shop());
        await using var _ = db;
        db.Orders.Add(Order(1, 49.00m));
        await db.SaveChangesAsync();

        var service = Orders(db, Ledger(db));

        var result = await service.MarkPaidAsync(1, "simulator", "txn_1", 58.89m);

        Assert.True(result.Success);
        var entry = await db.VendorLedgerEntries.SingleAsync(e => e.OrderId == 1);
        Assert.Equal(VendorLedgerEntryType.Sale, entry.Type);
        Assert.Equal(44.10m, entry.NetAmount);
    }

    [Fact]
    public async Task Marking_an_order_paid_by_hand_credits_the_vendor()
    {
        var db = await Seeded(Shop());
        await using var _ = db;
        db.Orders.Add(Order(1, 49.00m));
        await db.SaveChangesAsync();

        var service = Orders(db, Ledger(db));

        var result = await service.UpdateStatusAsync(1, OrderStatus.Paid);

        Assert.True(result.Success);
        Assert.Equal(1, await db.VendorLedgerEntries.CountAsync(e => e.Type == VendorLedgerEntryType.Sale));
    }

    [Fact]
    public async Task Refunding_a_paid_order_reverses_the_credit()
    {
        var db = await Seeded(Shop());
        await using var _ = db;
        db.Orders.Add(Order(1, 49.00m));
        await db.SaveChangesAsync();

        var service = Orders(db, Ledger(db));

        await service.MarkPaidAsync(1, "simulator", "txn_1", 58.89m);
        var result = await service.UpdateStatusAsync(1, OrderStatus.Refunded);

        Assert.True(result.Success);

        var entries = await db.VendorLedgerEntries.Where(e => e.OrderId == 1).ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Type == VendorLedgerEntryType.Reversal && e.NetAmount == -44.10m);
        Assert.Equal(0.00m, entries.Sum(e => e.NetAmount));
    }

    [Fact]
    public async Task Cancelling_an_unpaid_order_writes_no_ledger_entry()
    {
        var db = await Seeded(Shop());
        await using var _ = db;
        db.Orders.Add(Order(1, 49.00m));
        await db.SaveChangesAsync();

        var service = Orders(db, Ledger(db));

        var result = await service.UpdateStatusAsync(1, OrderStatus.Cancelled);

        Assert.True(result.Success);
        Assert.Empty(await db.VendorLedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task A_ledger_sale_survives_a_second_status_change()
    {
        // Paid -> Shipped must not credit a second time; only the first collection counts.
        var db = await Seeded(Shop());
        await using var _ = db;
        db.Orders.Add(Order(1, 49.00m));
        await db.SaveChangesAsync();

        var service = Orders(db, Ledger(db));

        await service.UpdateStatusAsync(1, OrderStatus.Paid);
        await service.UpdateStatusAsync(1, OrderStatus.Shipped);

        Assert.Equal(1, await db.VendorLedgerEntries.CountAsync(e => e.Type == VendorLedgerEntryType.Sale));
    }
}
