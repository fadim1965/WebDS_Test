using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// End-to-end tests for the post-sale service: the fee split is checked against the rules, and the
/// lifecycle is checked by actually moving a request through it and reading the persisted state.
/// </summary>
public class PostSaleServiceTests
{
    private static PostSaleService Service(ApplicationDbContext db) =>
        new(db, NullLogger<PostSaleService>.Instance);

    private static async Task<(ApplicationDbContext Db, PostSaleService Service, Order Order, ApplicationUser User)>
        SeedAsync(OrderStatus status = OrderStatus.Paid, bool tracking = false)
    {
        var db = TestSupport.NewDb();

        var user = TestSupport.User();
        db.Users.Add(user);

        var order = TestSupport.Order(status: status);
        order.UserId = user.Id;
        order.PaidUtc = DateTime.UtcNow.AddDays(-1);

        if (tracking)
        {
            order.TrackingNumber = "LP-TRACK-1";
            order.ShippedUtc = DateTime.UtcNow;
            order.Status = OrderStatus.Shipped;
        }

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        return (db, Service(db), order, user);
    }

    [Fact]
    public async Task Cancellation_BeforePackaging_RefundsGoodsAndShipping()
    {
        var (db, service, order, user) = await SeedAsync();

        var created = await service.CreateAsync(
            order.Id, user.Id, PostSaleType.Cancellation, PostSaleReason.ChangedMind,
            orderLineId: null, detail: "Ordered by mistake");

        Assert.True(created.Success, created.Message);
        Assert.Equal(FeeBearer.Seller, created.Request!.FeeAllocation.ItemPriceBearer);
        Assert.Equal(53.50m, created.Request.RefundAmount);

        var approved = await service.DecideAsync(created.Request.Id, approve: true, "ok", "admin");
        Assert.True(approved.Success, approved.Message);
        Assert.Equal(PostSaleStatus.Processed, approved.Request!.Status);

        var reloaded = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        Assert.Equal(53.50m, reloaded.RefundedTotal);
    }

    [Fact]
    public async Task Cancellation_WithTrackingNumber_IsDeclined()
    {
        var (_, service, order, user) = await SeedAsync(tracking: true);

        var created = await service.CreateAsync(
            order.Id, user.Id, PostSaleType.Cancellation, PostSaleReason.ChangedMind, null, null);

        Assert.False(created.Success);
        Assert.Contains("tracking number", created.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Return_ChangedMind_IssuesPromoCodeNotCash()
    {
        var (db, service, order, user) = await SeedAsync();
        var line = order.Lines.First();

        var created = await service.CreateAsync(
            order.Id, user.Id, PostSaleType.Return, PostSaleReason.ChangedMind,
            line.Id, "changed my mind");

        Assert.True(created.Success, created.Message);
        Assert.True(created.Request!.IsPromoCodeRefund);
        Assert.Equal(FeeBearer.Customer, created.Request.FeeAllocation.ItemPriceBearer);

        await service.DecideAsync(created.Request.Id, true, null, "admin");
        var received = await service.TransitionAsync(
            created.Request.Id, PostSaleStatus.PickupScheduled, "admin");
        Assert.True(received.Success, received.Message);
        received = await service.TransitionAsync(created.Request.Id, PostSaleStatus.PickedUp, "admin");
        Assert.True(received.Success, received.Message);
        received = await service.TransitionAsync(created.Request.Id, PostSaleStatus.Received, "admin");
        Assert.True(received.Success, received.Message);

        var processed = await service.ProcessAsync(created.Request.Id, "admin");
        Assert.True(processed.Success, processed.Message);
        Assert.False(string.IsNullOrWhiteSpace(processed.Request!.PromoCode));

        var coupon = await db.Coupons.FirstOrDefaultAsync(c => c.Code == processed.Request.PromoCode);
        Assert.NotNull(coupon);
        Assert.Equal(49.00m, coupon!.Value);
    }

    [Fact]
    public async Task Exchange_ApproveThenProcess_ShipsReplacementWithoutRefund()
    {
        var (_, service, order, user) = await SeedAsync();
        var line = order.Lines.First();

        var created = await service.CreateAsync(
            order.Id, user.Id, PostSaleType.Exchange, PostSaleReason.WrongItem, line.Id, "wrong size");

        Assert.True(created.Success, created.Message);
        Assert.Equal(0m, created.Request!.RefundAmount);

        var approved = await service.DecideAsync(created.Request.Id, true, null, "admin");
        Assert.True(approved.Success, approved.Message);
        Assert.Equal(PostSaleStatus.Approved, approved.Request!.Status);

        var processed = await service.ProcessAsync(created.Request.Id, "admin");
        Assert.True(processed.Success, processed.Message);
        Assert.Equal(PostSaleStatus.ExchangeShipped, processed.Request!.Status);
    }

    [Fact]
    public async Task Warranty_Repairable_NoRefund_AndTriages()
    {
        var (_, service, order, user) = await SeedAsync(OrderStatus.Delivered);
        var line = order.Lines.First();

        // Force the purchase date outside the grace window.
        order.PaidUtc = DateTime.UtcNow.AddDays(-30);

        var created = await service.CreateAsync(
            order.Id, user.Id, PostSaleType.Warranty, PostSaleReason.Defective, line.Id, "stopped working", "SN-123");

        Assert.True(created.Success, created.Message);

        var triaged = await service.TriageWarrantyAsync(
            created.Request!.Id, canBeRepaired: true, canBeReplaced: false, "admin");

        Assert.True(triaged.Success, triaged.Message);
        Assert.True(triaged.Request!.CanBeRepaired);
        Assert.Equal(0m, triaged.Request.RefundAmount);
        Assert.Equal(FeeBearer.NotApplicable, triaged.Request.FeeAllocation.ItemPriceBearer);
    }

    [Fact]
    public async Task Rts_RefundsGoodsButNotShipping()
    {
        var (_, service, order, _) = await SeedAsync(OrderStatus.Shipped, tracking: true);

        var created = await service.CreateRtsAsync(
            order.Id, "admin", "libanpost", callAttempts: 3, note: "unreachable");

        Assert.True(created.Success, created.Message);
        Assert.Equal(PostSaleType.RTS, created.Request!.Type);
        Assert.Equal(49.00m, created.Request.RefundAmount);
        Assert.Equal(FeeBearer.Customer, created.Request.FeeAllocation.ShippingFeeBearer);
    }

    [Fact]
    public async Task Fulfilment_RefusesWithoutNylonOrInvoice()
    {
        var (_, service, order, _) = await SeedAsync();

        var noNylon = await service.ConfirmSellerFulfilmentAsync(
            order.Id, order.ShopId, 1, hasNylonPockets: false, invoiceAttached: true, "seller");

        Assert.False(noNylon.Success);
        Assert.Contains("nylon", noNylon.Message, StringComparison.OrdinalIgnoreCase);

        var noInvoice = await service.ConfirmSellerFulfilmentAsync(
            order.Id, order.ShopId, 1, hasNylonPockets: true, invoiceAttached: false, "seller");

        Assert.False(noInvoice.Success);
        Assert.Contains("invoice", noInvoice.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fulfilment_ConfirmGeneratePickup_MovesThroughTheProcess()
    {
        var (db, service, order, _) = await SeedAsync();

        var confirmed = await service.ConfirmSellerFulfilmentAsync(
            order.Id, order.ShopId, packageCount: 2, hasNylonPockets: true, invoiceAttached: true, "seller");

        Assert.True(confirmed.Success, confirmed.Message);
        Assert.Equal(FulfilmentStatus.AwaitingLabelGeneration, confirmed.Fulfilment!.Status);
        Assert.Equal(2, confirmed.Fulfilment.PackageCount);
        Assert.Equal(0, confirmed.Fulfilment.LabelsGenerated);
        Assert.NotNull(confirmed.Fulfilment.SellerConfirmedUtc);

        var labelled = await service.GenerateFulfilmentLabelsAsync(confirmed.Fulfilment.Id, "admin");

        Assert.True(labelled.Success, labelled.Message);
        Assert.Equal(FulfilmentStatus.LabelsGenerated, labelled.Fulfilment!.Status);
        Assert.Equal(2, labelled.Fulfilment.LabelsGenerated);
        Assert.NotNull(labelled.Fulfilment.LabelsGeneratedUtc);

        var pickedUp = await service.RecordFulfilmentPickupAsync(labelled.Fulfilment.Id, "admin");

        Assert.True(pickedUp.Success, pickedUp.Message);
        Assert.Equal(FulfilmentStatus.PickedUpByShipper, pickedUp.Fulfilment!.Status);
        Assert.NotNull(pickedUp.Fulfilment.PickedUpByShipperUtc);

        var stored = await db.SellerFulfilments.AsNoTracking().FirstAsync();
        Assert.Equal(FulfilmentStatus.PickedUpByShipper, stored.Status);
        Assert.Equal(2, stored.LabelsGenerated);
    }

    [Fact]
    public async Task Fulfilment_CannotBeConfirmedForAnotherShop()
    {
        var (_, service, order, _) = await SeedAsync();

        var result = await service.ConfirmSellerFulfilmentAsync(
            order.Id, shopId: order.ShopId + 1, packageCount: 1,
            hasNylonPockets: true, invoiceAttached: true, "seller");

        Assert.False(result.Success);
        Assert.Contains("not found", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
