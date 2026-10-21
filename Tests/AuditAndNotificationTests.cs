using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services;
using WebDS_Test.Services.Email;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// The admin audit trail and the notifications the workflow generates: what was recorded, what
/// filters narrow it, and the dedupe keys that stop a retried action from mailing twice.
/// </summary>
public class AuditAndNotificationTests
{
    private static AuditTrail Audit(ApplicationDbContext db) =>
        new(db, NullLogger<AuditTrail>.Instance);

    private static ApplicationDbContext NewDb() => TestSupport.NewDb();

    // ------------------------------------------------------------------
    // Audit trail
    // ------------------------------------------------------------------

    [Fact]
    public async Task Recorded_actions_are_read_back_newest_first_with_their_details()
    {
        await using var db = NewDb();
        var audit = Audit(db);

        await audit.RecordAsync("admin@example.com", "shop.approved", "Shop", 7, "Approved Shop 7 with commission 10%.");
        await audit.RecordAsync("admin@example.com", "return.received", "ReturnRequest", 3, "Received goods for RMA-2026-0001.");

        var entries = await audit.GetRecentAsync(ct: CancellationToken.None);

        Assert.Equal(2, entries.Count);
        Assert.Equal("return.received", entries[0].Action); // newest first
        Assert.Equal("ReturnRequest", entries[0].EntityType);
        Assert.Equal(3, entries[0].EntityId);
        Assert.Equal("admin@example.com", entries[0].Actor);
        Assert.Contains("RMA-2026-0001", entries[0].Summary);
    }

    [Fact]
    public async Task The_trail_filters_by_entity_and_actor()
    {
        await using var db = NewDb();
        var audit = Audit(db);

        await audit.RecordAsync("admin@example.com", "shop.approved", "Shop", 7, "one");
        await audit.RecordAsync("vendor@example.com", "shop.approved", "Shop", 9, "two");
        await audit.RecordAsync("admin@example.com", "order.refunded", "Order", 7, "three");

        var shopsById = await audit.GetRecentAsync(entityType: "Shop", entityId: 7, ct: CancellationToken.None);
        Assert.Single(shopsById);
        Assert.Equal("one", shopsById[0].Summary);

        var byVendor = await audit.GetRecentAsync(actor: "vendor", ct: CancellationToken.None);
        Assert.Single(byVendor);
        Assert.Equal("vendor@example.com", byVendor[0].Actor);

        var shopActions = await audit.GetRecentAsync("shop.approved", ct: CancellationToken.None);
        Assert.Equal(2, shopActions.Count);
    }

    [Fact]
    public async Task A_failing_audit_write_is_swallowed_not_thrown()
    {
        // A trail documents actions; when its context is gone, the write fails quietly rather
        // than undoing or failing whatever action it was trying to document.
        var db = NewDb();
        var audit = Audit(db);
        await db.DisposeAsync();

        var exception = await Record.ExceptionAsync(() =>
            audit.RecordAsync("admin@example.com", "shop.rejected", "Shop", null, "note"));

        Assert.Null(exception);
    }

    // ------------------------------------------------------------------
    // Payout and shop-status notifications
    // ------------------------------------------------------------------

    private static EmailService Emails(ApplicationDbContext db) => new(
        db,
        new RecordingTransport(),
        TestSupport.Options(TestSupport.EmailOptions()),
        TestSupport.Options(TestSupport.Store()),
        TestSupport.Options(TestSupport.Brand()),
        NullLogger<EmailService>.Instance);

    private static VendorPayout Payout(Shop shop) => new()
    {
        Id = 1,
        ShopId = shop.Id,
        Shop = shop,
        Reference = "PAY-2026-0001",
        PeriodStartUtc = DateTime.UtcNow.AddDays(-7),
        PeriodEndUtc = DateTime.UtcNow,
        EntryCount = 3,
        GrossAmount = 100.00m,
        CommissionAmount = 10.00m,
        NetAmount = 90.00m,
        Method = "Bank transfer",
        CreatedUtc = DateTime.UtcNow
    };

    [Fact]
    public async Task A_payout_notifies_the_shop_exactly_once_per_payout()
    {
        await using var db = NewDb();
        var shop = new Shop
        {
            Id = 1,
            Name = "Beirut Stamps",
            Slug = "beirut-stamps",
            OwnerUserId = "owner-1",
            Email = "payouts@beirutstamps.example",
            Status = ShopStatus.Approved
        };
        db.Shops.Add(shop);
        await db.SaveChangesAsync();

        var emails = Emails(db);
        await emails.NotifyPayoutIssuedAsync(Payout(shop));
        await emails.NotifyPayoutIssuedAsync(Payout(shop)); // a retry of the same fact

        var queued = await db.EmailMessages.ToListAsync();
        var single = Assert.Single(queued);
        Assert.Equal(EmailKind.PayoutIssued, single.Kind);
        Assert.Equal("payouts@beirutstamps.example", single.ToAddress);
        Assert.Contains("PAY-2026-0001", single.Subject);
        Assert.NotNull(single.DedupeKey);
    }

    [Fact]
    public async Task A_shop_without_a_contact_email_is_not_mailed_but_the_action_stands()
    {
        await using var db = NewDb();
        var emails = Emails(db);

        var shop = new Shop { Id = 2, Name = "No-email shop", Slug = "no-email", OwnerUserId = "o", Status = ShopStatus.Approved };
        shop.Email = null;

        await emails.NotifyPayoutIssuedAsync(Payout(shop));

        Assert.Empty(await db.EmailMessages.ToListAsync());
    }

    [Fact]
    public async Task Shop_status_decisions_mail_the_owner_once_per_distinct_action()
    {
        await using var db = NewDb();
        var owner = TestSupport.User(email: "vendor@example.com", displayName: "Vera Vendor");
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var emails = Emails(db);
        var shop = new Shop
        {
            Id = 1,
            Name = "Beirut Stamps",
            Slug = "beirut-stamps",
            OwnerUserId = owner.Id,
            Status = ShopStatus.PendingApproval
        };

        // Approved, then suspended with a reason, then suspended again with the same reason —
        // the retry of the suspension must dedupe, the approval must not be swallowed by it.
        shop.StatusReason = null;
        await emails.NotifyShopDecisionAsync(shop, owner.Email, "approved");

        shop.Status = ShopStatus.Suspended;
        shop.StatusReason = "Unpaid chargebacks";
        await emails.NotifyShopDecisionAsync(shop, owner.Email, "suspended");
        await emails.NotifyShopDecisionAsync(shop, owner.Email, "suspended");

        var queued = await db.EmailMessages.ToListAsync();
        Assert.Equal(2, queued.Count);
        Assert.All(queued, m => Assert.Equal(EmailKind.ShopStatus, m.Kind));
        Assert.Contains(queued, m => m.Subject.Contains("approved"));
        Assert.Contains(queued, m => m.Subject.Contains("suspended"));
        Assert.All(queued, m => Assert.Equal(owner.Email, m.ToAddress));
        Assert.All(queued, m => Assert.Equal(owner.Id, m.UserId));
    }

    // ------------------------------------------------------------------
    // Payouts carry the notification into service wiring
    // ------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_payout_queues_the_shop_notification()
    {
        await using var db = TestSupport.NewDb();
        db.Shops.Add(new Shop
        {
            Id = 1,
            Name = "Beirut Stamps",
            Slug = "beirut-stamps",
            OwnerUserId = "owner-1",
            Email = "payouts@beirutstamps.example",
            Status = ShopStatus.Approved
        });
        db.Orders.Add(TestSupport.Order(id: 1));
        await db.SaveChangesAsync();

        await new VendorLedgerService(db, NullLogger<VendorLedgerService>.Instance)
            .RecordSaleAsync(await db.Orders.FirstAsync(), "paid");

        var payouts = new PayoutService(
            db,
            TestSupport.Options(new PayoutOptions()),
            NullLogger<PayoutService>.Instance,
            Emails(db));

        var created = await payouts.CreatePayoutAsync(1, "Bank transfer", null, "admin@example.com");
        Assert.True(created.Success, created.Message);

        var queued = await db.EmailMessages.FirstOrDefaultAsync(m => m.Kind == EmailKind.PayoutIssued);
        Assert.NotNull(queued);
        Assert.Equal("payouts@beirutstamps.example", queued!.ToAddress);
        Assert.Contains(created.Payout!.Reference, queued.Subject);
    }

    [Fact]
    public async Task Approving_a_shop_queues_the_owner_notification()
    {
        await using var db = TestSupport.NewDb();
        var owner = TestSupport.User(email: "vendor@example.com", displayName: "Vera Vendor");
        db.Users.Add(owner);
        db.Shops.Add(new Shop
        {
            Id = 1,
            Name = "Beirut Stamps",
            Slug = "beirut-stamps",
            OwnerUserId = owner.Id,
            Status = ShopStatus.PendingApproval
        });
        await db.SaveChangesAsync();

        var approved = await new ShopService(
            db,
            new SlugService(),
            new ThemeCatalog(),
            NullLogger<ShopService>.Instance,
            Emails(db)).ApproveAsync(1, null);

        Assert.True(approved.Success, approved.Message);

        var queued = await db.EmailMessages.FirstOrDefaultAsync(m => m.Kind == EmailKind.ShopStatus);
        Assert.NotNull(queued);
        Assert.Equal("vendor@example.com", queued!.ToAddress);
        Assert.Contains("approved", queued.Subject, StringComparison.OrdinalIgnoreCase);
    }
}
