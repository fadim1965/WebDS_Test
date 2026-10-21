using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services;
using WebDS_Test.Services.Email;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// Shopper-to-shop conversations end to end: a thread opens from the storefront, each side's reply
/// notifies the other, the unread markers only move for the side that is actually reading, and a
/// closed thread is final.
/// </summary>
/// <remarks>
/// The invariants that matter here are the ones neither controller can be trusted to hold on its
/// own: a thread belongs to exactly one shopper and one shop, "unread" is derived from the per-side
/// markers rather than stored twice, and every notification is keyed on the message it carries, so
/// a retry cannot mail the same message again.
/// </remarks>
public class ConversationTests
{
    private static ApplicationDbContext NewDb() => TestSupport.NewDb();

    private static async Task<ApplicationDbContext> SeededAsync()
    {
        var db = NewDb();
        var user = TestSupport.User(email: "shopper@example.com", displayName: "Sam Shopper");
        db.Users.Add(user);

        db.Shops.Add(new Shop
        {
            Id = 1,
            Name = "LibanPost Shop",
            Slug = "libanpost-shop",
            OwnerUserId = "owner-1",
            Email = "shop@example.com",
            CommissionRate = 0.10m,
            Status = ShopStatus.Approved
        });

        var order = TestSupport.Order(id: 7, number: "ORD-2026-0007", status: OrderStatus.Paid);
        order.UserId = user.Id;
        db.Orders.Add(order);

        await db.SaveChangesAsync();
        return db;
    }

    private static ConversationService Service(ApplicationDbContext db) => new(
        db,
        new EmailService(
            db,
            new RecordingTransport(),
            TestSupport.Options(TestSupport.EmailOptions()),
            TestSupport.Options(TestSupport.Store()),
            TestSupport.Options(TestSupport.Brand()),
            NullLogger<EmailService>.Instance),
        NullLogger<ConversationService>.Instance);

    // ------------------------------------------------------------------
    // Opening
    // ------------------------------------------------------------------

    [Fact]
    public async Task Opening_a_conversation_files_the_first_message_and_notifies_the_shop()
    {
        await using var db = await SeededAsync();
        var service = Service(db);
        var user = await db.Users.SingleAsync();

        var result = await service.StartAsync(
            1, null, "Is this in stock?",
            "Do you have the EMS envelopes in a pack of 10?",
            user.Id, user.Email, user.DisplayName);

        Assert.True(result.Success, result.Message);

        var stored = await db.Conversations
            .Include(c => c.Shop)
            .Include(c => c.Messages)
            .SingleAsync();
        Assert.Equal("CONV-2026-0001", stored.Reference);
        Assert.Equal(ConversationStatus.Open, stored.Status);
        Assert.Equal(user.Id, stored.UserId);
        Assert.Equal("shop@example.com", stored.Shop!.Email);

        // The shopper has, trivially, read their own opener.
        Assert.NotNull(stored.CustomerLastReadUtc);
        Assert.Null(stored.VendorLastReadUtc);

        var message = Assert.Single(stored.Messages);
        Assert.Equal(ConversationAuthor.Customer, message.Author);
        Assert.Equal(user.Id, message.SenderUserId);
        Assert.Equal("Do you have the EMS envelopes in a pack of 10?", message.Body);
        Assert.Equal(message.Body, stored.LastMessagePreview);
        Assert.Equal(ConversationAuthor.Customer, stored.LastAuthor);

        // One notification, addressed to the shop's contact, keyed on the message it carries.
        var mail = await db.EmailMessages.SingleAsync();
        Assert.Equal(EmailKind.ConversationMessage, mail.Kind);
        Assert.Equal("shop@example.com", mail.ToAddress);
        Assert.Equal($"conversation-message:{message.Id}", mail.DedupeKey);

        // And it is queued, not sent inline — nothing here blocks on a mail server.
        Assert.Equal(EmailStatus.Queued, mail.Status);
    }

    [Fact]
    public async Task References_advance_within_the_year()
    {
        await using var db = await SeededAsync();
        var service = Service(db);
        var user = await db.Users.SingleAsync();

        var first = await service.StartAsync(1, null, "First", "First question", user.Id, user.Email, user.DisplayName);
        var second = await service.StartAsync(1, null, "Second", "Second question", user.Id, user.Email, user.DisplayName);

        Assert.Equal($"CONV-{DateTime.UtcNow.Year}-0001", first.Conversation!.Reference);
        Assert.Equal($"CONV-{DateTime.UtcNow.Year}-0002", second.Conversation!.Reference);
    }

    [Fact]
    public async Task Only_a_trading_shop_can_be_contacted_and_only_the_shoppers_own_order_is_quoted()
    {
        await using var db = await SeededAsync();
        var service = Service(db);
        var user = await db.Users.SingleAsync();

        // An unapproved shop is not contactable, the way its storefront page is not reachable.
        db.Shops.Add(new Shop
        {
            Id = 2,
            Name = "Pending shop",
            Slug = "pending-shop",
            OwnerUserId = "owner-2",
            CommissionRate = 0.10m,
            Status = ShopStatus.PendingApproval
        });
        await db.SaveChangesAsync();

        var pending = await service.StartAsync(2, null, "Subject", "Body", user.Id, user.Email, user.DisplayName);
        Assert.False(pending.Success);

        // A guest cannot open a thread at all.
        var guest = await service.StartAsync(1, null, "Subject", "Body", "", "guest@example.com", "Guest");
        Assert.False(guest.Success);

        // Somebody else's order id is dropped rather than quoted: the thread still opens, without
        // the context the caller had no right to.
        var foreignOrder = await service.StartAsync(
            1, 9999, "About an order", "Is this order coming?", user.Id, user.Email, user.DisplayName);
        Assert.True(foreignOrder.Success, foreignOrder.Message);
        Assert.Null(foreignOrder.Conversation!.OrderId);

        // Their own order is kept.
        var ownOrder = await service.StartAsync(
            1, 7, "About an order", "Is this order coming?", user.Id, user.Email, user.DisplayName);
        Assert.True(ownOrder.Success, ownOrder.Message);
        Assert.Equal(7, ownOrder.Conversation!.OrderId);
    }

    [Fact]
    public async Task An_empty_subject_or_body_is_refused()
    {
        await using var db = await SeededAsync();
        var service = Service(db);
        var user = await db.Users.SingleAsync();

        var noSubject = await service.StartAsync(1, null, "   ", "Body", user.Id, user.Email, user.DisplayName);
        var noBody = await service.StartAsync(1, null, "Subject", "", user.Id, user.Email, user.DisplayName);
        var tooLong = await service.StartAsync(1, null, "Subject", new string('x', 4001), user.Id, user.Email, user.DisplayName);

        Assert.False(noSubject.Success);
        Assert.False(noBody.Success);
        Assert.False(tooLong.Success);
        Assert.Equal(0, await db.Conversations.CountAsync());
    }

    // ------------------------------------------------------------------
    // Replying, unread counts and read markers
    // ------------------------------------------------------------------

    [Fact]
    public async Task Each_sides_reply_notifies_the_other_and_moves_only_their_own_unread_count()
    {
        await using var db = await SeededAsync();
        var service = Service(db);
        var user = await db.Users.SingleAsync();

        var opened = await service.StartAsync(
            1, null, "Delivery to Tripoli", "How long does delivery take?", user.Id, user.Email, user.DisplayName);
        var id = opened.Conversation!.Id;

        // The shopper wrote, so it is the shop waiting — never the shopper, on their own message.
        Assert.Equal(1, await service.CountUnreadForShopAsync(1));
        Assert.Equal(0, await service.CountUnreadForCustomerAsync(user.Id));

        // The shop opens the thread: waiting drops to zero.
        await service.MarkVendorReadAsync(id);
        Assert.Equal(0, await service.CountUnreadForShopAsync(1));

        // The shop answers: the shopper now has something new, and the shop is not waiting on itself.
        var reply = await service.ReplyAsync(id, ConversationAuthor.Vendor, "LibanPost Shop", "owner-1", "Two to three days.");
        Assert.True(reply.Success, reply.Message);
        Assert.Equal(1, await service.CountUnreadForCustomerAsync(user.Id));
        Assert.Equal(0, await service.CountUnreadForShopAsync(1));

        // The reply reached the shopper by mail, keyed on the message.
        var mail = await db.EmailMessages.OrderByDescending(m => m.Id).FirstAsync();
        Assert.Equal("shopper@example.com", mail.ToAddress);
        Assert.StartsWith("conversation-message:", mail.DedupeKey);

        // The shopper opens the thread: their marker moves, the badge clears.
        await service.MarkCustomerReadAsync(id);
        Assert.Equal(0, await service.CountUnreadForCustomerAsync(user.Id));

        // And the round trip: the shopper's next reply puts the shop back in waiting.
        var second = await service.ReplyAsync(id, ConversationAuthor.Customer, user.DisplayName, user.Id, "Thank you.");
        Assert.True(second.Success, second.Message);
        Assert.Equal(1, await service.CountUnreadForShopAsync(1));
        Assert.Equal(0, await service.CountUnreadForCustomerAsync(user.Id));
    }

    [Fact]
    public async Task Reading_a_thread_twice_is_a_no_op_once_the_marker_has_moved()
    {
        await using var db = await SeededAsync();
        var service = Service(db);
        var user = await db.Users.SingleAsync();

        var opened = await service.StartAsync(1, null, "Subject", "First message", user.Id, user.Email, user.DisplayName);
        var id = opened.Conversation!.Id;
        await service.ReplyAsync(id, ConversationAuthor.Vendor, "Shop", "owner-1", "Answer");

        await service.MarkVendorReadAsync(id);
        var firstMarker = (await db.Conversations.SingleAsync(c => c.Id == id)).VendorLastReadUtc;

        await service.MarkVendorReadAsync(id);
        var secondMarker = (await db.Conversations.SingleAsync(c => c.Id == id)).VendorLastReadUtc;

        Assert.Equal(firstMarker, secondMarker);

        // And when a side wrote most recently there is nothing for it to mark, so its marker
        // stays exactly where it was.
        await service.ReplyAsync(id, ConversationAuthor.Customer, user.DisplayName, user.Id, "Thank you.");
        var customerBefore = (await db.Conversations.SingleAsync(c => c.Id == id)).CustomerLastReadUtc;

        await service.MarkCustomerReadAsync(id);
        var customerAfter = (await db.Conversations.SingleAsync(c => c.Id == id)).CustomerLastReadUtc;

        Assert.Equal(customerBefore, customerAfter);
    }

    [Fact]
    public async Task The_inbox_lists_each_side_in_newest_activity_order_and_scopes_to_its_owner()
    {
        await using var db = await SeededAsync();
        var service = Service(db);
        var user = await db.Users.SingleAsync();

        var older = await service.StartAsync(1, null, "Older", "First question", user.Id, user.Email, user.DisplayName);
        var newer = await service.StartAsync(1, null, "Newer", "Second question", user.Id, user.Email, user.DisplayName);

        // Force distinct activity timestamps rather than trusting the clock's resolution.
        var olderRow = await db.Conversations.SingleAsync(c => c.Id == older.Conversation!.Id);
        olderRow.LastMessageUtc = DateTime.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();

        var mine = await service.GetForCustomerAsync(user.Id);
        Assert.Equal(2, mine.Count);
        Assert.Equal(newer.Conversation!.Id, mine[0].Id);
        Assert.Equal(older.Conversation!.Id, mine[1].Id);

        // A different shopper sees none of them, and the shop sees only its own threads.
        Assert.Empty(await service.GetForCustomerAsync("somebody-else"));
        Assert.Equal(2, (await service.GetForShopAsync(1)).Count);
        Assert.Empty(await service.GetForShopAsync(2));
    }

    // ------------------------------------------------------------------
    // Closing
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_closed_conversation_is_final_for_both_sides()
    {
        await using var db = await SeededAsync();
        var service = Service(db);
        var user = await db.Users.SingleAsync();

        var opened = await service.StartAsync(1, null, "Subject", "Question", user.Id, user.Email, user.DisplayName);
        var id = opened.Conversation!.Id;

        var closed = await service.CloseAsync(id, ConversationAuthor.Vendor);
        Assert.True(closed.Success, closed.Message);
        Assert.Equal(ConversationStatus.Closed, closed.Conversation!.Status);
        Assert.NotNull(closed.Conversation.ClosedUtc);

        // Neither side can write again, and the second close says so rather than pretending.
        var vendorReply = await service.ReplyAsync(id, ConversationAuthor.Vendor, "Shop", "owner-1", "Too late");
        var customerReply = await service.ReplyAsync(id, ConversationAuthor.Customer, user.DisplayName, user.Id, "Too late");
        var again = await service.CloseAsync(id, ConversationAuthor.Customer);

        Assert.False(vendorReply.Success);
        Assert.False(customerReply.Success);
        Assert.False(again.Success);
        Assert.Equal(1, await db.ConversationMessages.CountAsync());
    }

    // ------------------------------------------------------------------
    // The notification template
    // ------------------------------------------------------------------

    [Fact]
    public void The_message_template_names_the_conversation_and_escapes_the_body()
    {
        var conversation = new Conversation
        {
            Reference = "CONV-2026-0001",
            Subject = "Stock question",
            CustomerEmail = "shopper@example.com",
            Shop = new Shop { Name = "LibanPost Shop" }
        };
        var message = new ConversationMessage
        {
            Author = ConversationAuthor.Customer,
            SenderName = "Sam Shopper",
            Body = "<script>alert('hi')</script>\nSecond line"
        };

        var rendered = EmailTemplates.ConversationMessage(
            conversation, message, TestSupport.Store(), TestSupport.Brand());

        Assert.Contains("CONV-2026-0001", rendered.Subject);
        Assert.Contains("Stock question", rendered.Html);
        // Encoded, never executed — and the writer's line break survives as HTML.
        Assert.DoesNotContain("<script>", rendered.Html);
        Assert.Contains("&lt;script&gt;", rendered.Html);
        Assert.Contains("<br />Second line", rendered.Html);
        // The plain-text alternative carries the body as written.
        Assert.Contains("<script>alert('hi')</script>", rendered.Text);
    }
}
