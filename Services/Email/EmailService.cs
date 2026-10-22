namespace WebDS_Test.Services.Email;

/// <summary>Everything needed to place one message in the outbox.</summary>
public sealed record EmailDraft(
    string ToAddress,
    string? ToName,
    string Subject,
    string Html,
    string Text,
    string Kind,
    string? DedupeKey = null,
    string? UserId = null,
    int? OrderId = null);

/// <summary>How many messages sit in each outbox state, for the admin header.</summary>
public sealed record EmailOutboxCounts(int Queued, int Sent, int Failed, int Skipped)
{
    public int Total => Queued + Sent + Failed + Skipped;
}

/// <summary>
/// The transactional email outbox: callers enqueue, a background dispatcher sends. Nothing here
/// blocks on a mail server.
/// </summary>
public interface IEmailService
{
    /// <summary>
    /// Records a message for delivery. Returns the existing row, unchanged, when
    /// <see cref="EmailDraft.DedupeKey"/> matches one already in the outbox.
    /// </summary>
    Task<EmailMessage?> EnqueueAsync(EmailDraft draft, CancellationToken ct = default);

    Task NotifyWelcomeAsync(ApplicationUser user, CancellationToken ct = default);

    Task NotifyOrderPlacedAsync(Order order, CancellationToken ct = default);

    Task NotifyOrderShippedAsync(Order order, CancellationToken ct = default);

    Task NotifyOrderCancelledAsync(Order order, CancellationToken ct = default);

    /// <summary>Refund confirmation for a one-shot full refund recorded by staff.</summary>
    Task NotifyRefundIssuedAsync(Order order, decimal amount, CancellationToken ct = default);

    /// <summary>Tells the shopper the shop approved or rejected their return request.</summary>
    Task NotifyReturnDecidedAsync(ReturnRequest request, bool approved, CancellationToken ct = default);

    /// <summary>Refund confirmation once a completed return has been paid back.</summary>
    Task NotifyReturnRefundedAsync(ReturnRequest request, CancellationToken ct = default);

    /// <summary>
    /// Tells the other side of a conversation there is a new message: a shopper's message mails
    /// the shop's contact address, a shop's or an administrator's mails the shopper. No-ops when
    /// the shop has no contact email.
    /// </summary>
    Task NotifyConversationMessageAsync(
        Conversation conversation, ConversationMessage message, CancellationToken ct = default);

    /// <summary>Tells a shop their settlement has been recorded. No-ops without a contact email.</summary>
    Task NotifyPayoutIssuedAsync(VendorPayout payout, CancellationToken ct = default);

    /// <summary>
    /// Tells a shop's owner the marketplace changed their shop's status.
    /// <paramref name="change"/> is one of <c>approved</c>, <c>rejected</c>, <c>suspended</c>,
    /// <c>reinstated</c> — it names the specific action, so reapprovals mail again.
    /// </summary>
    Task NotifyShopDecisionAsync(Shop shop, string ownerEmail, string change, CancellationToken ct = default);

    /// <summary>
    /// Tells a vendor how the marketplace decided on one of their listings.
    /// <paramref name="decision"/> is one of <c>approved</c>, <c>rejected</c> or <c>withdrawn</c> — it
    /// names the specific action, so a listing that is approved, withdrawn and approved again mails
    /// each time.
    /// </summary>
    Task NotifyListingDecisionAsync(
        Product product, string vendorEmail, string decision, CancellationToken ct = default);

    Task NotifyPasswordResetAsync(ApplicationUser user, string resetUrl, CancellationToken ct = default);

    /// <summary>Queues a fixed test message to the given address.</summary>
    Task EnqueueTestAsync(string toAddress, CancellationToken ct = default);

    /// <summary>
    /// Hands at most <paramref name="batchSize"/> queued messages to the transport, recording the
    /// outcome of each. Returns how many were delivered. Safe to call with the queue empty.
    /// </summary>
    Task<int> ProcessQueueAsync(int? batchSize = null, CancellationToken ct = default);

    Task<PagedResult<EmailMessage>> GetOutboxAsync(
        EmailStatus? status, string? kind, int page, int pageSize, CancellationToken ct = default);

    Task<EmailOutboxCounts> GetCountsAsync(CancellationToken ct = default);

    Task<EmailMessage?> GetMessageAsync(int id, CancellationToken ct = default);

    /// <summary>Returns a delivered or failed message to the queue and clears its history.</summary>
    Task<bool> ResendAsync(int messageId, CancellationToken ct = default);
}

/// <summary>Default <see cref="IEmailService"/>: renders from <see cref="EmailTemplates"/> and owns
/// the outbox table.</summary>
public sealed class EmailService : IEmailService
{
    private readonly ApplicationDbContext _db;
    private readonly IEmailTransport _transport;
    private readonly EmailOptions _options;
    private readonly StoreSettings _store;
    private readonly BrandingOptions _brand;
    private readonly ILogger<EmailService> _logger;

    public EmailService(
        ApplicationDbContext db,
        IEmailTransport transport,
        IOptions<EmailOptions> options,
        IOptions<StoreSettings> store,
        IOptions<BrandingOptions> brand,
        ILogger<EmailService> logger)
    {
        _db = db;
        _transport = transport;
        _options = options.Value;
        _store = store.Value;
        _brand = brand.Value;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // Enqueue
    // ------------------------------------------------------------------

    public async Task<EmailMessage?> EnqueueAsync(EmailDraft draft, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(draft.DedupeKey))
        {
            var existing = await _db.EmailMessages
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.DedupeKey == draft.DedupeKey, ct);

            if (existing is not null)
            {
                return existing;
            }
        }

        var message = new EmailMessage
        {
            ToAddress = draft.ToAddress.Trim(),
            ToName = string.IsNullOrWhiteSpace(draft.ToName) ? null : draft.ToName.Trim(),
            Subject = draft.Subject,
            Kind = draft.Kind,
            BodyHtml = draft.Html,
            BodyText = draft.Text,
            DedupeKey = string.IsNullOrWhiteSpace(draft.DedupeKey) ? null : draft.DedupeKey,
            UserId = draft.UserId,
            OrderId = draft.OrderId,
            CreatedUtc = DateTime.UtcNow,
            // With mail switched off the message is still recorded — visibly "skipped" rather than
            // silently dropped — so nothing about what the store intended to send is lost.
            Status = _options.Enabled ? EmailStatus.Queued : EmailStatus.Skipped
        };

        _db.EmailMessages.Add(message);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Queued {Kind} email {Id} to {To} ({Status})",
            message.Kind, message.Id, message.ToAddress, message.Status);

        return message;
    }

    public Task NotifyWelcomeAsync(ApplicationUser user, CancellationToken ct = default)
    {
        var rendered = EmailTemplates.Welcome(user, _store, _brand);

        return EnqueueAsync(new EmailDraft(
            user.Email, user.DisplayName, rendered.Subject, rendered.Html, rendered.Text,
            EmailKind.Welcome, DedupeKey: $"welcome:{user.Id}", UserId: user.Id), ct);
    }

    public Task NotifyOrderPlacedAsync(Order order, CancellationToken ct = default)
    {
        var rendered = EmailTemplates.OrderConfirmation(order, _store, _brand);

        return EnqueueAsync(new EmailDraft(
            order.Email, order.CustomerName, rendered.Subject, rendered.Html, rendered.Text,
            EmailKind.OrderConfirmation, DedupeKey: $"order-confirmation:{order.Id}",
            UserId: order.UserId, OrderId: order.Id), ct);
    }

    public Task NotifyOrderShippedAsync(Order order, CancellationToken ct = default)
    {
        var rendered = EmailTemplates.OrderShipped(order, _store, _brand);

        return EnqueueAsync(new EmailDraft(
            order.Email, order.CustomerName, rendered.Subject, rendered.Html, rendered.Text,
            EmailKind.OrderShipped, DedupeKey: $"order-shipped:{order.Id}",
            UserId: order.UserId, OrderId: order.Id), ct);
    }

    public Task NotifyOrderCancelledAsync(Order order, CancellationToken ct = default)
    {
        var rendered = EmailTemplates.OrderCancelled(order, _store, _brand);

        return EnqueueAsync(new EmailDraft(
            order.Email, order.CustomerName, rendered.Subject, rendered.Html, rendered.Text,
            EmailKind.OrderCancelled, DedupeKey: $"order-cancelled:{order.Id}",
            UserId: order.UserId, OrderId: order.Id), ct);
    }

    public Task NotifyRefundIssuedAsync(Order order, decimal amount, CancellationToken ct = default)
    {
        var rendered = EmailTemplates.RefundIssued(order, amount, _store, _brand);

        // Keyed per order and amount: the refund of a given order for a given figure is one fact,
        // and a retry of the same fact must not mail twice.
        return EnqueueAsync(new EmailDraft(
            order.Email, order.CustomerName, rendered.Subject, rendered.Html, rendered.Text,
            EmailKind.RefundIssued, DedupeKey: $"refund-issued:{order.Id}:{amount:0.##}",
            UserId: order.UserId, OrderId: order.Id), ct);
    }

    public Task NotifyReturnDecidedAsync(ReturnRequest request, bool approved, CancellationToken ct = default)
    {
        var rendered = EmailTemplates.ReturnDecided(request, approved, _store, _brand);

        return EnqueueAsync(new EmailDraft(
            request.Email, request.Order?.CustomerName, rendered.Subject, rendered.Html, rendered.Text,
            EmailKind.ReturnDecided,
            DedupeKey: $"return-decided:{request.Id}:{(approved ? "approved" : "rejected")}",
            UserId: request.UserId, OrderId: request.OrderId), ct);
    }

    public Task NotifyReturnRefundedAsync(ReturnRequest request, CancellationToken ct = default)
    {
        var rendered = EmailTemplates.ReturnRefunded(request, _store, _brand);

        return EnqueueAsync(new EmailDraft(
            request.Email, request.Order?.CustomerName, rendered.Subject, rendered.Html, rendered.Text,
            EmailKind.ReturnRefunded, DedupeKey: $"return-refunded:{request.Id}",
            UserId: request.UserId, OrderId: request.OrderId), ct);
    }

    public Task NotifyConversationMessageAsync(
        Conversation conversation, ConversationMessage message, CancellationToken ct = default)
    {
        var toCustomer = message.Author != ConversationAuthor.Customer;
        var toAddress = toCustomer ? conversation.CustomerEmail : conversation.Shop?.Email;

        if (string.IsNullOrWhiteSpace(toAddress))
        {
            // No contact email on the shop — nothing to send to. Choosing another address (the
            // owner's, say) is the caller's decision, not this service's — the same rule the
            // payout notification applies.
            return Task.CompletedTask;
        }

        var rendered = EmailTemplates.ConversationMessage(conversation, message, _store, _brand);

        // Keyed per message: one message reaching the other side is one fact, however many times
        // the command that produced it is retried.
        return EnqueueAsync(new EmailDraft(
            toAddress, toCustomer ? null : conversation.Shop?.Name,
            rendered.Subject, rendered.Html, rendered.Text,
            EmailKind.ConversationMessage, DedupeKey: $"conversation-message:{message.Id}",
            UserId: toCustomer ? conversation.UserId : conversation.Shop?.OwnerUserId,
            OrderId: conversation.OrderId), ct);
    }

    public Task NotifyPayoutIssuedAsync(VendorPayout payout, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(payout.Shop?.Email))
        {
            // No contact email on the shop — nothing to send to. Choosing another address (the
            // owner's, say) is the caller's decision, not this service's.
            return Task.CompletedTask;
        }

        var rendered = EmailTemplates.PayoutIssued(payout, _store, _brand);

        // Keyed per payout: the record of one settlement is one fact, however many times the
        // command that produced it is retried.
        return EnqueueAsync(new EmailDraft(
            payout.Shop!.Email, payout.Shop.Name, rendered.Subject, rendered.Html, rendered.Text,
            EmailKind.PayoutIssued, DedupeKey: $"payout-issued:{payout.Id}",
            UserId: payout.Shop.OwnerUserId), ct);
    }

    public Task NotifyShopDecisionAsync(Shop shop, string ownerEmail, string change, CancellationToken ct = default)
    {
        var rendered = EmailTemplates.ShopStatusDecision(shop.Name, change, shop.StatusReason, _store, _brand);

        // Keyed per shop, change and reason: a retried suspension mails once, while an
        // approval now and a reinstatement later each get their own message.
        return EnqueueAsync(new EmailDraft(
            ownerEmail, shop.Name, rendered.Subject, rendered.Html, rendered.Text,
            EmailKind.ShopStatus,
            DedupeKey: $"shop-status:{shop.Id}:{change}:{shop.StatusReason}",
            UserId: shop.OwnerUserId), ct);
    }

    public Task NotifyListingDecisionAsync(
        Product product, string vendorEmail, string decision, CancellationToken ct = default)
    {
        var rendered = EmailTemplates.ListingDecision(
            product.Name, decision, product.ModerationReason, _store, _brand);

        // Keyed per listing, decision and reason: a retried approval mails once, while an approval
        // now and a later rejection each get their own message.
        return EnqueueAsync(new EmailDraft(
            vendorEmail, null, rendered.Subject, rendered.Html, rendered.Text,
            EmailKind.ListingDecision,
            DedupeKey: $"listing-decision:{product.Id}:{decision}:{product.ModerationReason}",
            UserId: product.Shop?.OwnerUserId), ct);
    }

    public Task NotifyPasswordResetAsync(ApplicationUser user, string resetUrl, CancellationToken ct = default)
    {
        var rendered = EmailTemplates.PasswordReset(
            user.DisplayName, resetUrl, _options.PasswordResetMinutes, _store, _brand);

        // Deliberately no dedupe key: a shopper may legitimately ask for a fresh link, and each one
        // mails a distinct token.
        return EnqueueAsync(new EmailDraft(
            user.Email, user.DisplayName, rendered.Subject, rendered.Html, rendered.Text,
            EmailKind.PasswordReset, UserId: user.Id), ct);
    }

    public Task EnqueueTestAsync(string toAddress, CancellationToken ct = default)
    {
        var rendered = EmailTemplates.Test(_store, _brand);

        return EnqueueAsync(new EmailDraft(
            toAddress, null, rendered.Subject, rendered.Html, rendered.Text, EmailKind.Test), ct);
    }

    // ------------------------------------------------------------------
    // Dispatch
    // ------------------------------------------------------------------

    public async Task<int> ProcessQueueAsync(int? batchSize = null, CancellationToken ct = default)
    {
        var take = Math.Clamp(batchSize ?? _options.BatchSize, 1, 500);
        var now = DateTime.UtcNow;

        var pending = await _db.EmailMessages
            .Where(m => m.Status == EmailStatus.Queued && (m.NextAttemptUtc == null || m.NextAttemptUtc <= now))
            .OrderBy(m => m.CreatedUtc)
            .Take(take)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            return 0;
        }

        var delivered = 0;

        foreach (var message in pending)
        {
            var envelope = new EmailEnvelope(
                _options.FromAddress,
                _options.FromName,
                _options.ReplyTo,
                message.ToAddress,
                message.ToName,
                message.Subject,
                message.BodyHtml,
                message.BodyText);

            try
            {
                await _transport.SendAsync(envelope, ct);
                message.Status = EmailStatus.Sent;
                message.SentUtc = DateTime.UtcNow;
                message.LastError = null;
                message.NextAttemptUtc = null;
                delivered++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                message.Attempts++;
                message.LastError = Truncate(ex.Message, 1000);

                if (message.Attempts >= Math.Max(1, _options.MaxAttempts))
                {
                    message.Status = EmailStatus.Failed;
                    message.NextAttemptUtc = null;
                    _logger.LogError(ex,
                        "Email {Id} ({Kind}) to {To} failed permanently after {Attempts} attempts",
                        message.Id, message.Kind, message.ToAddress, message.Attempts);
                }
                else
                {
                    // Exponential backoff, capped so a stuck message still retries within the hour.
                    var delay = TimeSpan.FromSeconds(
                        Math.Min(_options.RetryBaseSeconds * Math.Pow(2, message.Attempts - 1), 3600));
                    message.NextAttemptUtc = DateTime.UtcNow.Add(delay);
                    _logger.LogWarning(ex,
                        "Email {Id} ({Kind}) to {To} failed (attempt {Attempts}); retrying after {Delay}",
                        message.Id, message.Kind, message.ToAddress, message.Attempts, delay);
                }
            }
        }

        await _db.SaveChangesAsync(ct);

        if (delivered > 0)
        {
            _logger.LogInformation(
                "Email dispatcher delivered {Delivered} of {Attempted} message(s) via {Transport}",
                delivered, pending.Count, _transport.Name);
        }

        return delivered;
    }

    // ------------------------------------------------------------------
    // Admin views
    // ------------------------------------------------------------------

    public async Task<PagedResult<EmailMessage>> GetOutboxAsync(
        EmailStatus? status, string? kind, int page, int pageSize, CancellationToken ct = default)
    {
        var size = Math.Clamp(pageSize, 1, 100);

        var query = _db.EmailMessages.AsNoTracking();

        if (status is { } s)
        {
            query = query.Where(m => m.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            var k = kind.Trim();
            query = query.Where(m => m.Kind == k);
        }

        var total = await query.CountAsync(ct);
        var clamped = Paging.ClampPage(page, size, total);

        var items = await query
            .OrderByDescending(m => m.CreatedUtc)
            .ThenByDescending(m => m.Id)
            .Skip(Paging.Skip(clamped, size))
            .Take(size)
            .ToListAsync(ct);

        return new PagedResult<EmailMessage>
        {
            Items = items,
            Page = clamped,
            PageSize = size,
            TotalCount = total
        };
    }

    public async Task<EmailOutboxCounts> GetCountsAsync(CancellationToken ct = default)
    {
        var grouped = await _db.EmailMessages
            .AsNoTracking()
            .GroupBy(m => m.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int Count(EmailStatus status) => grouped.FirstOrDefault(g => g.Status == status)?.Count ?? 0;

        return new EmailOutboxCounts(
            Count(EmailStatus.Queued),
            Count(EmailStatus.Sent),
            Count(EmailStatus.Failed),
            Count(EmailStatus.Skipped));
    }

    public Task<EmailMessage?> GetMessageAsync(int id, CancellationToken ct = default) =>
        _db.EmailMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);

    public async Task<bool> ResendAsync(int messageId, CancellationToken ct = default)
    {
        var message = await _db.EmailMessages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null)
        {
            return false;
        }

        message.Status = _options.Enabled ? EmailStatus.Queued : EmailStatus.Skipped;
        message.Attempts = 0;
        message.LastError = null;
        message.NextAttemptUtc = null;
        message.SentUtc = null;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Email {Id} ({Kind}) re-queued by {Action}", message.Id, message.Kind, "admin");
        return true;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
