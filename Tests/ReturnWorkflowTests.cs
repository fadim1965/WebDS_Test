using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services;
using WebDS_Test.Services.Email;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// The return workflow end to end: a shopper asks, the shop decides and receives, the
/// administration refunds — and the refund arithmetic can never give back more than the order owes.
/// </summary>
/// <remarks>
/// The numbers are deliberately blunt. A 49.00 order (2 × 24.50) with 4.50 shipping and 5.39 tax
/// refunds 27.20 for one unit (24.50 goods + 2.70 tax) and 31.69 for the second (the same 27.20
/// plus the 4.50 shipping) — the round trip lands on the order's grand total, 58.89.
/// </remarks>
public class ReturnWorkflowTests
{
    private static Order PaidOrder(int id = 1)
    {
        var order = new Order
        {
            Id = id,
            OrderNumber = $"ORD-2026-{id:D4}",
            Status = OrderStatus.Paid,
            Email = "shopper@example.com",
            CustomerName = "Sam Shopper",
            UserId = "shopper-1",
            ShopId = 1,
            ShippingName = "Sam Shopper",
            ShippingLine1 = "12 Rue Verdun",
            ShippingCity = "Beirut",
            ShippingPostalCode = "1107",
            ShippingCountry = "Lebanon",
            Subtotal = 49.00m,
            ShippingTotal = 4.50m,
            TaxTotal = 5.39m,
            GrandTotal = 58.89m,
            PaymentMethod = PaymentMethod.CashOnDelivery,
            CollectionStatus = CollectionStatus.Collected, // the courier already took the cash
            PlacedUtc = DateTime.UtcNow
        };

        order.Lines.Add(new OrderLine
        {
            ProductName = "EMS Priority Envelope",
            VariantName = "Pack of 10",
            Sku = "EMS-10",
            Quantity = 2,
            UnitPrice = 24.50m,
            LineTotal = 49.00m
        });

        return order;
    }

    private static ApplicationDbContext NewDb() => TestSupport.NewDb();

    private static async Task<ApplicationDbContext> SeededWithPaidOrderAndLedgedSale()
    {
        var db = NewDb();
        db.Shops.Add(new Shop
        {
            Id = 1,
            Name = "Shop 1",
            Slug = "shop-1",
            OwnerUserId = "owner-1",
            CommissionRate = 0.10m,
            Status = ShopStatus.Approved
        });

        var order = PaidOrder();
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        // The sale's ledger credit — 10% commission on 49.00.
        await new VendorLedgerService(db, NullLogger<VendorLedgerService>.Instance)
            .RecordSaleAsync(order, "paid");

        return db;
    }

    private static ReturnService Returns(ApplicationDbContext db) => new(
        db,
        new SandboxPaymentGateway(NullLogger<SandboxPaymentGateway>.Instance),
        new EmailService(
            db,
            new RecordingTransport(),
            TestSupport.Options(TestSupport.EmailOptions()),
            TestSupport.Options(TestSupport.Store()),
            TestSupport.Options(TestSupport.Brand()),
            NullLogger<EmailService>.Instance),
        TestSupport.Options(TestSupport.Store()),
        NullLogger<ReturnService>.Instance,
        new VendorLedgerService(db, NullLogger<VendorLedgerService>.Instance));

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_return_refunds_goods_plus_tax_then_shipping_when_the_whole_order_returns()
    {
        await using var db = await SeededWithPaidOrderAndLedgedSale();
        var service = Returns(db);
        var order = await db.Orders.Include(o => o.Lines).SingleAsync();
        var line = order.Lines.Single();

        // ---- Ask for one of the two units --------------------------------
        var created = await service.CreateAsync(order.Id, "shopper-1",
            new List<ReturnLineInput> { new(line.Id, 1) }, "Wrong size", CancellationToken.None);
        Assert.True(created.Success, created.Message);
        var first = created.Return!;

        // ---- Shop approves, receives, admin refunds ----------------------
        var approved = await service.DecideAsync(first.Id, true, "Send it back", "vendor@example.com");
        Assert.True(approved.Success, approved.Message);
        Assert.Equal(ReturnStatus.Approved, approved.Return!.Status);

        var received = await service.MarkReceivedAsync(first.Id, "vendor@example.com");
        Assert.True(received.Success, received.Message);
        Assert.Equal(ReturnStatus.Received, received.Return!.Status);

        var refunded = await service.RefundAsync(first.Id, "admin@example.com");
        Assert.True(refunded.Success, refunded.Message);

        // 24.50 goods + 2.70 tax (5.39 × 24.50/49.00); shipping only on a full return.
        Assert.Equal(27.20m, refunded.Return!.RefundAmount);
        Assert.NotEqual(OrderStatus.Refunded, order.Status); // partial — the order is still open

        // ---- The ledger took back exactly the goods, not the tax ---------
        var reversal = await db.VendorLedgerEntries
            .Where(e => e.Type == VendorLedgerEntryType.Reversal && e.ReturnRequestId == first.Id)
            .SingleAsync();
        Assert.Equal(-24.50m, reversal.GrossAmount);

        // ---- Second return: the last unit brings shipping back -----------
        var second = await service.CreateAsync(order.Id, "shopper-1",
            new List<ReturnLineInput> { new(line.Id, 1) }, "The pair does not match", CancellationToken.None);
        Assert.True(second.Success, second.Message);

        await service.DecideAsync(second.Return!.Id, true, null, "vendor@example.com");
        await service.MarkReceivedAsync(second.Return.Id, "vendor@example.com");
        var full = await service.RefundAsync(second.Return.Id, "admin@example.com");
        Assert.True(full.Success, full.Message);

        // Same goods 27.20 + 4.50 shipping = 31.69, the exact remainder of 58.89 − 27.20.
        Assert.Equal(31.69m, full.Return!.RefundAmount);
        Assert.Equal(58.89m, order.RefundedTotal);
        Assert.Equal(OrderStatus.Refunded, order.Status);
        Assert.Equal(ReturnStatus.Refunded, await db.ReturnRequests
            .Where(r => r.Id == second.Return.Id).Select(r => r.Status).SingleAsync());

        // The courier had collected the cash; recording the refund keeps that honest too.
        Assert.Equal(CollectionStatus.Refunded, await db.Orders
            .Where(o => o.Id == order.Id).Select(o => o.CollectionStatus).SingleAsync());
    }

    [Fact]
    public async Task A_card_refund_goes_back_through_the_gateway_against_the_charge()
    {
        await using var db = await SeededWithPaidOrderAndLedgedSale();
        var service = Returns(db);
        var order = await db.Orders.Include(o => o.Lines).SingleAsync();
        order.PaymentMethod = PaymentMethod.Card;

        // One successful charge on record, with a transaction id.
        db.Payments.Add(new Payment
        {
            OrderId = order.Id,
            Provider = "sandbox",
            TransactionId = "sbx_test0001",
            Amount = 58.89m,
            Currency = "USD",
            Status = PaymentStatus.Succeeded,
            CreatedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var created = await service.CreateAsync(order.Id, "shopper-1",
            new List<ReturnLineInput> { new(order.Lines.Single().Id, 1) }, null, CancellationToken.None);
        Assert.True(created.Success, created.Message);

        await service.DecideAsync(created.Return!.Id, true, null, "vendor@example.com");
        await service.MarkReceivedAsync(created.Return.Id, "vendor@example.com");
        var refunded = await service.RefundAsync(created.Return.Id, "admin@example.com");
        Assert.True(refunded.Success, refunded.Message);

        var refundRow = await db.Payments
            .Where(p => p.OrderId == order.Id && p.Status == PaymentStatus.Refunded)
            .SingleAsync();
        Assert.Equal(27.20m, refundRow.Amount);
        Assert.Equal("sandbox", refundRow.Provider);
        // The sandbox answers a refund with an sbr_ id — evidence the charge, not the row, was used.
        Assert.NotNull(refundRow.TransactionId);
        Assert.StartsWith("sbr_", refundRow.TransactionId);
    }

    [Fact]
    public async Task Returns_close_when_the_shopper_cancels_an_open_request()
    {
        await using var db = await SeededWithPaidOrderAndLedgedSale();
        var service = Returns(db);
        var order = await db.Orders.Include(o => o.Lines).SingleAsync();

        var created = await service.CreateAsync(order.Id, "shopper-1",
            new List<ReturnLineInput> { new(order.Lines.Single().Id, 1) }, null, CancellationToken.None);
        Assert.True(created.Success, created.Message);

        var cancelled = await service.CancelAsync(created.Return!.Id, "shopper-1");
        Assert.True(cancelled.Success, cancelled.Message);
        Assert.Equal(ReturnStatus.Cancelled, cancelled.Return!.Status);

        // A cancelled return is closed, so a new one can be raised right away.
        var again = await service.CreateAsync(order.Id, "shopper-1",
            new List<ReturnLineInput> { new(order.Lines.Single().Id, 1) }, null, CancellationToken.None);
        Assert.True(again.Success, again.Message);
    }

    // ------------------------------------------------------------------
    // Guards
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_unpaid_order_cannot_be_returned()
    {
        await using var db = await SeededWithPaidOrderAndLedgedSale();
        var service = Returns(db);
        var order = await db.Orders.Include(o => o.Lines).SingleAsync();
        order.Status = OrderStatus.Pending;
        db.SaveChanges();

        var result = await service.CreateAsync(order.Id, "shopper-1",
            new List<ReturnLineInput> { new(order.Lines.Single().Id, 1) }, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("cannot be returned", result.Message);
    }

    [Fact]
    public async Task A_partial_refund_leaves_exactly_one_more_unit_returnable()
    {
        await using var db = await SeededWithPaidOrderAndLedgedSale();
        var service = Returns(db);
        var order = await db.Orders.Include(o => o.Lines).SingleAsync();
        var line = order.Lines.Single();

        // First return refunds one of the two units.
        var first = await service.CreateAsync(order.Id, "shopper-1",
            new List<ReturnLineInput> { new(line.Id, 1) }, null, CancellationToken.None);
        await service.DecideAsync(first.Return!.Id, true, null, "vendor@example.com");
        await service.MarkReceivedAsync(first.Return.Id, "vendor@example.com");
        await service.RefundAsync(first.Return.Id, "admin@example.com");

        Assert.Equal(27.20m, order.RefundedTotal);
        Assert.Equal(OrderStatus.Paid, order.Status); // one unit's worth still open

        // The unit that was refunded is gone — asking for it again names the remainder.
        var again = await service.CreateAsync(order.Id, "shopper-1",
            new List<ReturnLineInput> { new(line.Id, 3) }, null, CancellationToken.None);

        Assert.False(again.Success);
        Assert.Contains("Only 1 ×", again.Message);
    }

    [Fact]
    public async Task Requesting_more_units_than_remain_returnable_is_refused()
    {
        await using var db = await SeededWithPaidOrderAndLedgedSale();
        var service = Returns(db);
        var order = await db.Orders.Include(o => o.Lines).SingleAsync();
        var line = order.Lines.Single();

        // Three units were never ordered.
        var greedy = await service.CreateAsync(order.Id, "shopper-1",
            new List<ReturnLineInput> { new(line.Id, 5) }, null, CancellationToken.None);

        Assert.False(greedy.Success);
        Assert.Contains("were ordered", greedy.Message);
    }

    [Fact]
    public async Task Only_one_open_return_may_exist_per_order()
    {
        await using var db = await SeededWithPaidOrderAndLedgedSale();
        var service = Returns(db);
        var order = await db.Orders.Include(o => o.Lines).SingleAsync();
        var line = order.Lines.Single();

        var first = await service.CreateAsync(order.Id, "shopper-1",
            new List<ReturnLineInput> { new(line.Id, 1) }, null, CancellationToken.None);
        Assert.True(first.Success, first.Message);

        var second = await service.CreateAsync(order.Id, "shopper-1",
            new List<ReturnLineInput> { new(line.Id, 1) }, null, CancellationToken.None);

        Assert.False(second.Success);
        Assert.Contains("already has an open return request", second.Message);
    }

    [Fact]
    public async Task A_return_from_another_shopper_is_not_visible_and_cannot_be_cancelled()
    {
        await using var db = await SeededWithPaidOrderAndLedgedSale();
        var service = Returns(db);
        var order = await db.Orders.Include(o => o.Lines).SingleAsync();

        var created = await service.CreateAsync(order.Id, "shopper-1",
            new List<ReturnLineInput> { new(order.Lines.Single().Id, 1) }, null, CancellationToken.None);

        var hijack = await service.CancelAsync(created.Return!.Id, "someone-else");
        Assert.False(hijack.Success);
        Assert.Contains("not found", hijack.Message);
    }

    // ------------------------------------------------------------------
    // Restock
    // ------------------------------------------------------------------

    [Fact]
    public async Task Received_goods_restock_the_shops_listing_pool()
    {
        await using var db = await SeededWithPaidOrderAndLedgedSale();
        var service = Returns(db);

        var product = new Product
        {
            Id = 1,
            ShopId = 1,
            Name = "Stamps set",
            Slug = "stamps-set",
            CategoryId = 0,
            Variants = { new ProductVariant { Id = 1, ProductId = 1, Sku = "ST-1", Price = 24.50m, StockQuantity = 6 } },
            Listings = { new ShopListing { Id = 1, ShopId = 1, ProductId = 1, IsActive = true, StockQuantity = 3 } }
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var order = await db.Orders.Include(o => o.Lines).SingleAsync();
        order.Lines.Single().ProductVariantId = 1;
        await db.SaveChangesAsync();

        var created = await service.CreateAsync(order.Id, "shopper-1",
            new List<ReturnLineInput> { new(order.Lines.Single().Id, 2) }, null, CancellationToken.None);
        Assert.True(created.Success, created.Message);

        var approved = await service.DecideAsync(created.Return!.Id, true, null, "vendor@example.com");
        Assert.True(approved.Success, approved.Message);

        var received = await service.MarkReceivedAsync(created.Return.Id, "vendor@example.com");
        Assert.True(received.Success, received.Message);

        var listing = await db.Set<ShopListing>().SingleAsync();
        Assert.Equal(5, listing.StockQuantity); // 3 held + 2 returned

        // The variant is untouched — the pool checkout committed is the listing's.
        var variant = await db.Set<ProductVariant>().SingleAsync();
        Assert.Equal(6, variant.StockQuantity);

        var movement = await db.StockMovements.SingleAsync();
        Assert.Equal(StockMovementReason.Return, movement.Reason);
        Assert.Equal(5, movement.StockAfter);
    }

    [Fact]
    public async Task Without_a_listing_received_goods_fall_back_to_the_variant_pool()
    {
        await using var db = await SeededWithPaidOrderAndLedgedSale();
        var service = Returns(db);

        // A product with no active listing — the shop removed it after the sale.
        db.Products.Add(new Product
        {
            Id = 2,
            ShopId = 1,
            Name = "Withdrawn stamps",
            Slug = "withdrawn-stamps",
            CategoryId = 0,
            Variants = { new ProductVariant { Id = 2, ProductId = 2, Sku = "WD-1", Price = 24.50m, StockQuantity = 9 } }
        });
        await db.SaveChangesAsync();

        var order = await db.Orders.Include(o => o.Lines).SingleAsync();
        order.Lines.Single().ProductVariantId = 2;
        await db.SaveChangesAsync();

        var created = await service.CreateAsync(order.Id, "shopper-1",
            new List<ReturnLineInput> { new(order.Lines.Single().Id, 1) }, null, CancellationToken.None);
        await service.DecideAsync(created.Return!.Id, true, null, "vendor@example.com");
        var received = await service.MarkReceivedAsync(created.Return.Id, "vendor@example.com");
        Assert.True(received.Success, received.Message);

        var variant = await db.Set<ProductVariant>().SingleAsync(v => v.Id == 2);
        Assert.Equal(10, variant.StockQuantity);
        Assert.Equal(0, await db.Set<ShopListing>().CountAsync());
    }

    // ------------------------------------------------------------------
    // Payout hardening: fail and reverse release the covered entries
    // ------------------------------------------------------------------

    private static async Task<(ApplicationDbContext Db, int EntryId)> SeededWithOneSettledPayout()
    {
        var db = NewDb();
        db.Shops.Add(new Shop
        {
            Id = 1,
            Name = "Shop 1",
            Slug = "shop-1",
            OwnerUserId = "owner-1",
            CommissionRate = 0.10m,
            Status = ShopStatus.Approved
        });

        db.Orders.Add(TestSupport.Order(id: 1));
        await db.SaveChangesAsync();

        await new VendorLedgerService(db, NullLogger<VendorLedgerService>.Instance)
            .RecordSaleAsync(await db.Orders.FirstAsync(), "paid");

        var payouts = new PayoutService(
            db,
            TestSupport.Options(new PayoutOptions()),
            NullLogger<PayoutService>.Instance);

        var created = await payouts.CreatePayoutAsync(1, "Bank transfer", null, "admin@example.com");
        Assert.True(created.Success, created.Message);

        var entry = await db.VendorLedgerEntries.SingleAsync();
        Assert.NotNull(entry.SettledUtc); // paid out

        return (db, entry.Id);
    }

    [Fact]
    public async Task A_failed_payout_releases_its_entries_back_to_the_balance()
    {
        var (db, entryId) = await SeededWithOneSettledPayout();
        await using var _ = db;
        var payouts = new PayoutService(
            db, TestSupport.Options(new PayoutOptions()), NullLogger<PayoutService>.Instance);

        var payout = await db.VendorPayouts.SingleAsync();

        var marked = await payouts.MarkFailedAsync(payout.Id, "IBAN rejected by the bank", "admin@example.com");
        Assert.True(marked.Success, marked.Message);

        Assert.Equal(PayoutStatus.Failed, payout.Status);
        Assert.Equal("IBAN rejected by the bank", payout.FailureReason);
        Assert.NotNull(payout.FailedUtc);

        var entry = await db.VendorLedgerEntries.SingleAsync(e => e.Id == entryId);
        Assert.Null(entry.SettledUtc); // payable again

        // And the shop can be paid out again.
        var again = await payouts.CreatePayoutAsync(1, "Bank transfer", null, "admin@example.com");
        Assert.True(again.Success, again.Message);
        Assert.Equal(2, await db.VendorPayouts.CountAsync());
    }

    [Fact]
    public async Task A_reversed_payout_also_releases_its_entries_and_keeps_its_totals()
    {
        var (db, entryId) = await SeededWithOneSettledPayout();
        await using var _ = db;
        var payouts = new PayoutService(
            db, TestSupport.Options(new PayoutOptions()), NullLogger<PayoutService>.Instance);

        var payout = await db.VendorPayouts.SingleAsync();
        var totals = (Gross: payout.GrossAmount, Net: payout.NetAmount);

        var reversed = await payouts.ReverseAsync(payout.Id, "recorded in error", "admin@example.com");
        Assert.True(reversed.Success, reversed.Message);

        Assert.Equal(PayoutStatus.Reversed, payout.Status);
        Assert.NotNull(payout.ReversedUtc);

        var entry = await db.VendorLedgerEntries.SingleAsync(e => e.Id == entryId);
        Assert.Null(entry.SettledUtc);

        // A correction never edits a financial document: the row keeps its figures.
        Assert.Equal(totals.Gross, payout.GrossAmount);
        Assert.Equal(totals.Net, payout.NetAmount);
    }

    [Fact]
    public async Task A_payout_cannot_be_failed_or_reversed_twice()
    {
        var (db, _) = await SeededWithOneSettledPayout();
        await using var _ = db;
        var payouts = new PayoutService(
            db, TestSupport.Options(new PayoutOptions()), NullLogger<PayoutService>.Instance);

        var payout = await db.VendorPayouts.SingleAsync();
        Assert.True((await payouts.MarkFailedAsync(payout.Id, "bank refused", "a")).Success);

        var second = await payouts.MarkFailedAsync(payout.Id, "again", "a");
        Assert.False(second.Success);
        Assert.Contains("already", second.Message);

        var third = await payouts.ReverseAsync(payout.Id, null, "a");
        Assert.False(third.Success);
    }

    // ------------------------------------------------------------------
    // Bank details travel with the registration
    // ------------------------------------------------------------------

    [Fact]
    public async Task Registration_records_the_payout_destination()
    {
        await using var db = TestSupport.NewDb();
        var shops = new ShopService(
            db,
            new SlugService(),
            new ThemeCatalog(),
            NullLogger<ShopService>.Instance);

        db.Users.Add(TestSupport.User(email: "vendor@example.com", displayName: "Vera Vendor"));
        await db.SaveChangesAsync();
        var owner = await db.Users.FirstAsync();

        var result = await shops.RegisterAsync(owner.Id, new ShopRegistration
        {
            Name = "Beirut Stamps",
            AcceptTerms = true,
            BankAccountName = "Beirut Stamps SARL",
            BankName = "Banque Libano-Française",
            BankIban = "LB62 0019 0000 0000 0001 2345 6789"
        });

        Assert.True(result.Success, result.Message);
        Assert.Equal("Beirut Stamps SARL", result.Shop!.BankAccountName);
        Assert.Equal("Banque Libano-Française", result.Shop!.BankName);
        Assert.Equal("LB62 0019 0000 0000 0001 2345 6789", result.Shop!.BankIban);
    }
}
