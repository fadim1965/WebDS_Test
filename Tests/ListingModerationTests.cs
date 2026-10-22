using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services;
using WebDS_Test.Services.Email;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// Listing moderation end to end: a vendor submits a listing, an administrator approves it or
/// turns it down, and only an approved listing is visible to shoppers.
/// </summary>
/// <remarks>
/// The invariant that matters most is that the review state and the visible flag cannot drift
/// apart: a listing must never be on sale without having been approved, and a listing that is
/// waiting for a decision must not be. Both are held by the service rather than by each controller,
/// so the tests exercise the service directly.
/// </remarks>
public class ListingModerationTests
{
    private static ApplicationDbContext NewDb() => TestSupport.NewDb();

    /// <summary>A shop, its owner, a category and one live listing.</summary>
    private static async Task<ApplicationDbContext> SeededAsync()
    {
        var db = NewDb();

        var owner = TestSupport.User(email: "vendor@example.com", displayName: "Vera Vendor");
        db.Users.Add(owner);

        db.Shops.Add(new Shop
        {
            Id = 1,
            Name = "Beirut Books",
            Slug = "beirut-books",
            OwnerUserId = owner.Id,
            Email = "vendor@example.com",
            CommissionRate = 0.10m,
            Status = ShopStatus.Approved
        });

        db.Categories.Add(new Category
        {
            Id = 1,
            Name = "Books",
            Slug = "books",
            IsGlobal = true
        });

        db.Products.Add(new Product
        {
            Id = 1,
            ShopId = 1,
            CategoryId = 1,
            Name = "A Book",
            Slug = "a-book",
            IsPublished = true,
            ModerationStatus = ListingStatus.Published
        });

        db.ProductVariants.Add(new ProductVariant
        {
            Id = 1,
            ProductId = 1,
            Sku = "BOOK-1",
            Name = "Paperback",
            Price = 10m,
            StockQuantity = 3,
            IsActive = true
        });

        await db.SaveChangesAsync();
        return db;
    }

    private static ListingModerationService Service(ApplicationDbContext db) => new(
        db,
        NullLogger<ListingModerationService>.Instance,
        new EmailService(
            db,
            new RecordingTransport(),
            TestSupport.Options(TestSupport.EmailOptions()),
            TestSupport.Options(TestSupport.Store()),
            TestSupport.Options(TestSupport.Brand()),
            NullLogger<EmailService>.Instance));

    // ------------------------------------------------------------------
    // Submitting
    // ------------------------------------------------------------------

    [Fact]
    public async Task Submitting_a_listing_queues_it_and_takes_it_off_sale()
    {
        await using var db = await SeededAsync();
        var service = Service(db);

        var result = await service.SubmitAsync(1, 1);

        Assert.True(result.Success, result.Message);

        var product = await db.Products.SingleAsync();
        Assert.Equal(ListingStatus.PendingReview, product.ModerationStatus);
        Assert.False(product.IsPublished);
        Assert.Null(product.ModerationReason);
        Assert.NotNull(product.ModeratedUtc);
    }

    [Fact]
    public async Task A_vendor_cannot_submit_another_shops_listing()
    {
        await using var db = await SeededAsync();
        var service = Service(db);

        var result = await service.SubmitAsync(1, shopId: 99);

        Assert.False(result.Success);

        var product = await db.Products.SingleAsync();
        Assert.Equal(ListingStatus.Published, product.ModerationStatus);
        Assert.True(product.IsPublished);
    }

    [Fact]
    public async Task A_listing_that_is_already_waiting_is_not_queued_twice()
    {
        await using var db = await SeededAsync();
        var service = Service(db);

        await service.SubmitAsync(1, 1);
        var second = await service.SubmitAsync(1, 1);

        Assert.False(second.Success);
    }

    [Fact]
    public async Task A_deleted_listing_is_not_submitted()
    {
        await using var db = await SeededAsync();
        var product = await db.Products.SingleAsync();
        product.IsDeleted = true;
        await db.SaveChangesAsync();
        var service = Service(db);

        var result = await service.SubmitAsync(1, 1);

        Assert.False(result.Success);
    }

    // ------------------------------------------------------------------
    // Deciding
    // ------------------------------------------------------------------

    [Fact]
    public async Task Approving_a_listing_puts_it_live()
    {
        await using var db = await SeededAsync();
        var service = Service(db);

        await service.SubmitAsync(1, 1);
        var result = await service.ApproveAsync(1);

        Assert.True(result.Success, result.Message);

        var product = await db.Products.SingleAsync();
        Assert.Equal(ListingStatus.Published, product.ModerationStatus);
        Assert.True(product.IsPublished);
        Assert.Null(product.ModerationReason);
    }

    [Fact]
    public async Task Rejecting_a_listing_records_the_reason_and_keeps_it_off_sale()
    {
        await using var db = await SeededAsync();
        var service = Service(db);

        await service.SubmitAsync(1, 1);
        var result = await service.RejectAsync(1, "The price is wrong.");

        Assert.True(result.Success, result.Message);

        var product = await db.Products.SingleAsync();
        Assert.Equal(ListingStatus.Rejected, product.ModerationStatus);
        Assert.False(product.IsPublished);
        Assert.Equal("The price is wrong.", product.ModerationReason);
    }

    [Fact]
    public async Task A_rejection_without_a_reason_still_records_one()
    {
        await using var db = await SeededAsync();
        var service = Service(db);

        await service.SubmitAsync(1, 1);
        await service.RejectAsync(1, "   ");

        var product = await db.Products.SingleAsync();
        Assert.Equal(ListingStatus.Rejected, product.ModerationStatus);
        Assert.False(string.IsNullOrWhiteSpace(product.ModerationReason));
    }

    [Fact]
    public async Task Withdrawing_a_live_listing_takes_it_off_sale()
    {
        await using var db = await SeededAsync();
        var service = Service(db);

        var result = await service.WithdrawAsync(1, "Reported as counterfeit.");

        Assert.True(result.Success, result.Message);

        var product = await db.Products.SingleAsync();
        Assert.Equal(ListingStatus.Draft, product.ModerationStatus);
        Assert.False(product.IsPublished);
        Assert.Equal("Reported as counterfeit.", product.ModerationReason);
    }

    [Fact]
    public async Task Only_a_live_listing_can_be_withdrawn()
    {
        await using var db = await SeededAsync();
        var service = Service(db);

        await service.SubmitAsync(1, 1);
        var result = await service.WithdrawAsync(1, null);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task A_vendor_can_take_their_own_live_listing_off_sale()
    {
        await using var db = await SeededAsync();
        var service = Service(db);

        var result = await service.UnpublishAsync(1, 1);

        Assert.True(result.Success, result.Message);

        var product = await db.Products.SingleAsync();
        Assert.Equal(ListingStatus.Draft, product.ModerationStatus);
        Assert.False(product.IsPublished);
    }

    [Fact]
    public async Task A_vendor_cannot_take_another_shops_listing_off_sale()
    {
        await using var db = await SeededAsync();
        var service = Service(db);

        var result = await service.UnpublishAsync(1, shopId: 99);

        Assert.False(result.Success);

        var product = await db.Products.SingleAsync();
        Assert.True(product.IsPublished);
    }

    // ------------------------------------------------------------------
    // What the vendor and the marketplace are told
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_vendor_is_mailed_when_a_listing_is_approved()
    {
        await using var db = await SeededAsync();
        var service = Service(db);

        await service.SubmitAsync(1, 1);
        await service.ApproveAsync(1);

        var mail = await db.EmailMessages.SingleAsync();
        Assert.Equal(EmailKind.ListingDecision, mail.Kind);
        Assert.Equal("vendor@example.com", mail.ToAddress);
        Assert.Contains("approved", mail.Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_vendor_is_mailed_the_reason_when_a_listing_is_rejected()
    {
        await using var db = await SeededAsync();
        var service = Service(db);

        await service.SubmitAsync(1, 1);
        await service.RejectAsync(1, "The photos do not match the item.");

        var mail = await db.EmailMessages.SingleAsync();
        Assert.Equal(EmailKind.ListingDecision, mail.Kind);
        Assert.Contains("The photos do not match the item.", mail.BodyHtml);
        Assert.Contains("The photos do not match the item.", mail.BodyText);
    }

    [Fact]
    public async Task A_listing_taken_off_sale_by_its_owner_is_not_mailed_about()
    {
        await using var db = await SeededAsync();
        var service = Service(db);

        await service.UnpublishAsync(1, 1);

        Assert.Empty(await db.EmailMessages.ToListAsync());
    }

    // ------------------------------------------------------------------
    // The queue
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_queue_lists_what_is_waiting_for_a_decision()
    {
        await using var db = await SeededAsync();
        var service = Service(db);

        await service.SubmitAsync(1, 1);

        var waiting = await service.GetQueueAsync(ListingStatus.PendingReview, null);

        var row = Assert.Single(waiting);
        Assert.Equal(1, row.Id);
        Assert.Equal("A Book", row.Name);
        Assert.Equal("Beirut Books", row.ShopName);
        Assert.Equal(ListingStatus.PendingReview, row.Status);
        Assert.Equal(10m, row.Price);
        Assert.Equal(3, row.Stock);

        Assert.Equal(1, await service.CountPendingAsync());
    }

    [Fact]
    public async Task The_queue_can_be_filtered_by_name()
    {
        await using var db = await SeededAsync();
        var service = Service(db);

        await service.SubmitAsync(1, 1);

        Assert.Single(await service.GetQueueAsync(null, "a book"));
        Assert.Empty(await service.GetQueueAsync(null, "something else"));
    }

    [Fact]
    public async Task A_deleted_listing_is_not_in_the_queue()
    {
        await using var db = await SeededAsync();
        var service = Service(db);

        await service.SubmitAsync(1, 1);
        var product = await db.Products.SingleAsync();
        product.IsDeleted = true;
        await db.SaveChangesAsync();

        Assert.Empty(await service.GetQueueAsync(ListingStatus.PendingReview, null));
        Assert.Equal(0, await service.CountPendingAsync());
    }

    // ------------------------------------------------------------------
    // The store that predates moderation
    // ------------------------------------------------------------------

    [Fact]
    public void A_listing_that_predates_moderation_is_live()
    {
        // The zero value is "published", so a store that has never heard of review keeps every
        // listing on sale without an administrator having to re-approve it.
        Assert.Equal(0, (int)ListingStatus.Published);
        Assert.Equal(ListingStatus.Published, new Product().ModerationStatus);
        Assert.True(new Product().IsPublished);
    }
}
