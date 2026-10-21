using Microsoft.EntityFrameworkCore;
using WebDS_Test.Services.Email;

namespace WebDS_Test.Services;

/// <summary>Outcome of a conversation operation.</summary>
public sealed record ConversationResult(bool Success, string Message, Conversation? Conversation = null)
{
    public static ConversationResult Ok(Conversation conversation, string message) => new(true, message, conversation);
    public static ConversationResult Fail(string message) => new(false, message);
}

/// <summary>
/// Owns shopper-to-shop conversations: a thread is started from the shop's storefront page,
/// answered from the vendor area, and read on either side.
/// </summary>
/// <remarks>
/// <para>
/// Ownership is enforced by the callers the same way <c>ReturnService</c>'s is — the controller
/// loads a thread, checks it belongs to the signed-in shopper (or to the resolved shop) and treats
/// anything else as not existing. This service enforces the invariants that are the same whichever
/// side is writing: a thread exists, it is open, the message is within bounds, and the read markers
/// only move forward.
/// </para>
/// <para>
/// Notification is deliberately after the commit and best-effort, exactly as returns notify: the
/// message the user just wrote is the fact, and a mail server having a bad day must not take it
/// away. Each notification is keyed on the message id, so a retry cannot mail twice.
/// </para>
/// </remarks>
public interface IConversationService
{
    /// <summary>Opens a thread from a shopper, with the first message. Guests cannot start one.</summary>
    Task<ConversationResult> StartAsync(
        int shopId, int? orderId, string subject, string body,
        string userId, string customerEmail, string customerName, CancellationToken ct = default);

    /// <summary>A shopper's threads, newest activity first.</summary>
    Task<IReadOnlyList<Conversation>> GetForCustomerAsync(string userId, CancellationToken ct = default);

    /// <summary>A shop's threads, newest activity first; <paramref name="openOnly"/> narrows to the worklist.</summary>
    Task<IReadOnlyList<Conversation>> GetForShopAsync(
        int shopId, bool? openOnly = null, CancellationToken ct = default);

    /// <summary>Loads one thread with its messages and shop, for a detail view.</summary>
    Task<Conversation?> GetAsync(int conversationId, CancellationToken ct = default);

    /// <summary>Appends a message from whichever side is writing and notifies the other side.</summary>
    Task<ConversationResult> ReplyAsync(
        int conversationId, ConversationAuthor author, string senderName, string? senderUserId,
        string body, CancellationToken ct = default);

    /// <summary>Closes a thread. Either side may do it; there is no reopen in this build.</summary>
    Task<ConversationResult> CloseAsync(
        int conversationId, ConversationAuthor actor, CancellationToken ct = default);

    /// <summary>Threads with something new for the shopper to read.</summary>
    Task<int> CountUnreadForCustomerAsync(string userId, CancellationToken ct = default);

    /// <summary>Threads waiting on the shop to answer.</summary>
    Task<int> CountUnreadForShopAsync(int shopId, CancellationToken ct = default);

    /// <summary>Moves the shopper's read marker to now. No-op if there is nothing newer.</summary>
    Task MarkCustomerReadAsync(int conversationId, CancellationToken ct = default);

    /// <summary>Moves the shop's read marker to now. No-op if there is nothing newer.</summary>
    Task MarkVendorReadAsync(int conversationId, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ConversationService : IConversationService
{
    private readonly ApplicationDbContext _db;
    private readonly IEmailService _emails;
    private readonly ILogger<ConversationService> _logger;

    public ConversationService(
        ApplicationDbContext db,
        IEmailService emails,
        ILogger<ConversationService> logger)
    {
        _db = db;
        _emails = emails;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ConversationResult> StartAsync(
        int shopId, int? orderId, string subject, string body,
        string userId, string customerEmail, string customerName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return ConversationResult.Fail("Sign in to contact the shop.");
        }

        subject = (subject ?? string.Empty).Trim();
        body = (body ?? string.Empty).Trim();

        if (subject.Length is 0 or > 200)
        {
            return ConversationResult.Fail("Give the message a subject of up to 200 characters.");
        }

        if (body.Length is 0 or > 4000)
        {
            return ConversationResult.Fail("Write a message of up to 4,000 characters.");
        }

        if (string.IsNullOrWhiteSpace(customerEmail))
        {
            return ConversationResult.Fail("Your account has no email address the shop could reply to.");
        }

        // Only a trading shop can be contacted — the same rule the storefront page applies.
        // Tracked (not AsNoTracking) deliberately: the notification below needs the shop on the
        // new thread, and a tracked principal is fixed up onto the conversation automatically.
        var shop = await _db.Shops
            .FirstOrDefaultAsync(s => s.Id == shopId && s.Status == ShopStatus.Approved, ct);
        if (shop is null)
        {
            return ConversationResult.Fail("Shop not found.");
        }

        // An order, when quoted, must belong to this shopper — an id belonging to somebody else
        // is treated the same as one that does not exist.
        if (orderId is { } quotedOrder)
        {
            var ownsOrder = await _db.Orders.AsNoTracking()
                .AnyAsync(o => o.Id == quotedOrder && o.UserId == userId && o.ShopId == shopId, ct);
            if (!ownsOrder)
            {
                orderId = null;
            }
        }

        var conversation = new Conversation
        {
            Reference = await NextReferenceAsync(ct),
            ShopId = shopId,
            Shop = shop,
            OrderId = orderId,
            UserId = userId,
            CustomerEmail = customerEmail.Trim(),
            Subject = subject,
            Status = ConversationStatus.Open,
            CreatedUtc = DateTime.UtcNow,
            LastMessageUtc = DateTime.UtcNow,
            // The shopper has just seen their own opening message.
            CustomerLastReadUtc = DateTime.UtcNow
        };

        conversation.Messages.Add(new ConversationMessage
        {
            Author = ConversationAuthor.Customer,
            SenderName = Truncate(customerName, 120),
            SenderUserId = userId,
            Body = body,
            SentUtc = DateTime.UtcNow
        });

        conversation.LastAuthor = ConversationAuthor.Customer;
        conversation.LastMessagePreview = Truncate(body, 300);

        _db.Conversations.Add(conversation);
        await _db.SaveChangesAsync(ct);

        await TryNotifyAsync(conversation.Reference, "opened",
            () => _emails.NotifyConversationMessageAsync(conversation, conversation.Messages.First(), ct));

        return ConversationResult.Ok(conversation,
            $"Message sent to {shop.Name}. They will see it in their shop inbox.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Conversation>> GetForCustomerAsync(string userId, CancellationToken ct = default) =>
        await _db.Conversations.AsNoTracking()
            .Where(c => c.UserId == userId)
            .Include(c => c.Shop)
            .Include(c => c.Order)
            .OrderByDescending(c => c.LastMessageUtc)
            .ThenByDescending(c => c.Id)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Conversation>> GetForShopAsync(
        int shopId, bool? openOnly = null, CancellationToken ct = default)
    {
        var query = _db.Conversations.AsNoTracking()
            .Where(c => c.ShopId == shopId);

        if (openOnly == true)
        {
            query = query.Where(c => c.Status == ConversationStatus.Open);
        }

        return await query
            .Include(c => c.Order)
            .OrderByDescending(c => c.LastMessageUtc)
            .ThenByDescending(c => c.Id)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<Conversation?> GetAsync(int conversationId, CancellationToken ct = default) =>
        await _db.Conversations.AsNoTracking()
            .Include(c => c.Shop)
            .Include(c => c.Order)
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);

    /// <inheritdoc />
    public async Task<ConversationResult> ReplyAsync(
        int conversationId, ConversationAuthor author, string senderName, string? senderUserId,
        string body, CancellationToken ct = default)
    {
        body = (body ?? string.Empty).Trim();
        if (body.Length is 0 or > 4000)
        {
            return ConversationResult.Fail("Write a message of up to 4,000 characters.");
        }

        var conversation = await _db.Conversations
            .Include(c => c.Shop)
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);

        if (conversation is null)
        {
            return ConversationResult.Fail("Conversation not found.");
        }

        if (conversation.Status != ConversationStatus.Open)
        {
            return ConversationResult.Fail("This conversation is closed.");
        }

        var message = new ConversationMessage
        {
            ConversationId = conversation.Id,
            Author = author,
            SenderName = Truncate(senderName, 120),
            SenderUserId = senderUserId,
            Body = body,
            SentUtc = DateTime.UtcNow
        };

        _db.ConversationMessages.Add(message);

        conversation.LastMessagePreview = Truncate(body, 300);
        conversation.LastAuthor = author;
        conversation.LastMessageUtc = DateTime.UtcNow;

        // Whatever side just wrote has, trivially, read everything up to its own message — so its
        // marker moves with the write and the other side's stays where it was.
        if (author == ConversationAuthor.Customer)
        {
            conversation.CustomerLastReadUtc = DateTime.UtcNow;
        }
        else
        {
            conversation.VendorLastReadUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);

        // Committed first, notified after: the message is the fact, the mail is a courtesy.
        await TryNotifyAsync(conversation.Reference, "replied",
            () => _emails.NotifyConversationMessageAsync(conversation, message, ct));

        return ConversationResult.Ok(conversation, "Message sent.");
    }

    /// <inheritdoc />
    public async Task<ConversationResult> CloseAsync(
        int conversationId, ConversationAuthor actor, CancellationToken ct = default)
    {
        var conversation = await _db.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);

        if (conversation is null)
        {
            return ConversationResult.Fail("Conversation not found.");
        }

        if (conversation.Status != ConversationStatus.Open)
        {
            return ConversationResult.Fail("This conversation is already closed.");
        }

        conversation.Status = ConversationStatus.Closed;
        conversation.ClosedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return ConversationResult.Ok(conversation, "Conversation closed.");
    }

    /// <inheritdoc />
    public async Task<int> CountUnreadForCustomerAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return 0;
        }

        // Anything whose newest message came from the other side and postdates the shopper's last
        // visit counts once — an inbox badge counts threads, not messages.
        return await _db.Conversations.AsNoTracking()
            .CountAsync(c => c.UserId == userId
                && c.LastAuthor != ConversationAuthor.Customer
                && c.LastMessageUtc > (c.CustomerLastReadUtc ?? DateTime.MinValue), ct);
    }

    /// <inheritdoc />
    public async Task<int> CountUnreadForShopAsync(int shopId, CancellationToken ct = default) =>
        await _db.Conversations.AsNoTracking()
            .CountAsync(c => c.ShopId == shopId
                && c.LastAuthor == ConversationAuthor.Customer
                && c.LastMessageUtc > (c.VendorLastReadUtc ?? DateTime.MinValue), ct);

    /// <inheritdoc />
    public async Task MarkCustomerReadAsync(int conversationId, CancellationToken ct = default)
    {
        var conversation = await _db.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);

        if (conversation is null)
        {
            return;
        }

        // Only move when there is something to mark — a GET must not write on every page view, and
        // there is nothing to mark when the shopper wrote most recently.
        if (conversation.LastAuthor == ConversationAuthor.Customer)
        {
            return;
        }

        if (conversation.CustomerLastReadUtc >= conversation.LastMessageUtc)
        {
            return;
        }

        conversation.CustomerLastReadUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task MarkVendorReadAsync(int conversationId, CancellationToken ct = default)
    {
        var conversation = await _db.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);

        if (conversation is null)
        {
            return;
        }

        // Only move when the shopper is the one who wrote last — a GET must not write on every
        // page view, and there is nothing to mark when the vendor side already wrote most recently.
        if (conversation.LastAuthor != ConversationAuthor.Customer)
        {
            return;
        }

        if (conversation.VendorLastReadUtc >= conversation.LastMessageUtc)
        {
            return;
        }

        conversation.VendorLastReadUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task<string> NextReferenceAsync(CancellationToken ct)
    {
        var year = DateTime.UtcNow.Year;
        var prefix = $"CONV-{year}-";

        var last = await _db.Conversations.AsNoTracking()
            .Where(c => c.Reference.StartsWith(prefix))
            .OrderByDescending(c => c.Reference)
            .Select(c => c.Reference)
            .FirstOrDefaultAsync(ct);

        var next = 1;
        if (last is not null && int.TryParse(last[prefix.Length..], out var parsed))
        {
            next = parsed + 1;
        }

        return prefix + next.ToString("D4");
    }

    /// <summary>
    /// Queues a notification, swallowing any failure — the message has already been committed by
    /// the time this runs, so an email problem is logged and moved past, exactly as order and
    /// return notifications are.
    /// </summary>
    private async Task TryNotifyAsync(string reference, string what, Func<Task> notify)
    {
        try
        {
            await notify();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Could not queue the conversation {What} email for {Reference}", what, reference);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
