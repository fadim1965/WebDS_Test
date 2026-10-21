using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services.Email;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// The outbox is the part of email that must not be wrong: it decides whether a shopper is told
/// anything, and it must never lose a message or send one twice. These tests drive
/// <see cref="EmailService"/> against an InMemory store and a recording transport.
/// </summary>
public class EmailOutboxTests
{
    private static EmailService NewService(
        ApplicationDbContext db,
        IEmailTransport transport,
        EmailOptions? options = null) =>
        new(db,
            transport,
            TestSupport.Options(options ?? TestSupport.EmailOptions()),
            TestSupport.Options(TestSupport.Store()),
            TestSupport.Options(TestSupport.Brand()),
            NullLogger<EmailService>.Instance);

    private static EmailDraft Draft(string? dedupeKey = null, string to = "shopper@example.com") =>
        new(to, "Sam Shopper", "Subject line", "<p>html</p>", "text", EmailKind.Test, dedupeKey);

    [Fact]
    public async Task Enqueue_writes_a_queued_row()
    {
        using var db = TestSupport.NewDb();
        var service = NewService(db, new RecordingTransport());

        var message = await service.EnqueueAsync(Draft());

        Assert.NotNull(message);
        Assert.Equal(EmailStatus.Queued, message!.Status);
        Assert.Equal("shopper@example.com", message.ToAddress);
        Assert.Equal(1, await db.EmailMessages.CountAsync());
    }

    [Fact]
    public async Task Enqueue_trims_the_address_and_drops_a_blank_display_name()
    {
        using var db = TestSupport.NewDb();
        var service = NewService(db, new RecordingTransport());

        var message = await service.EnqueueAsync(
            new EmailDraft("  shopper@example.com  ", "   ", "s", "<p></p>", "t", EmailKind.Test));

        Assert.Equal("shopper@example.com", message!.ToAddress);
        Assert.Null(message.ToName);
    }

    [Fact]
    public async Task A_repeated_dedupe_key_reuses_the_row_instead_of_sending_twice()
    {
        using var db = TestSupport.NewDb();
        var service = NewService(db, new RecordingTransport());

        var first = await service.EnqueueAsync(Draft("order-shipped:7"));
        var second = await service.EnqueueAsync(Draft("order-shipped:7"));

        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal(1, await db.EmailMessages.CountAsync());
    }

    [Fact]
    public async Task With_mail_switched_off_a_message_is_recorded_as_skipped_not_dropped()
    {
        using var db = TestSupport.NewDb();
        var service = NewService(db, new RecordingTransport(), TestSupport.EmailOptions(o => o.Enabled = false));

        var message = await service.EnqueueAsync(Draft());

        Assert.Equal(EmailStatus.Skipped, message!.Status);
        Assert.Equal(1, await db.EmailMessages.CountAsync());
    }

    [Fact]
    public async Task Processing_a_queued_message_hands_it_to_the_transport_and_marks_it_sent()
    {
        using var db = TestSupport.NewDb();
        var transport = new RecordingTransport();
        var service = NewService(db, transport);
        await service.EnqueueAsync(Draft());

        var delivered = await service.ProcessQueueAsync();

        Assert.Equal(1, delivered);
        var sent = Assert.Single(transport.Sent);
        Assert.Equal("Subject line", sent.Subject);
        Assert.Equal("<p>html</p>", sent.HtmlBody);

        var stored = await db.EmailMessages.SingleAsync();
        Assert.Equal(EmailStatus.Sent, stored.Status);
        Assert.NotNull(stored.SentUtc);
        Assert.Null(stored.LastError);
    }

    [Fact]
    public async Task The_envelope_carries_the_configured_sender()
    {
        using var db = TestSupport.NewDb();
        var transport = new RecordingTransport();
        var service = NewService(db, transport, TestSupport.EmailOptions(o =>
        {
            o.FromAddress = "orders@libanpost.example";
            o.FromName = "LibanPost Orders";
            o.ReplyTo = "support@libanpost.example";
        }));
        await service.EnqueueAsync(Draft());

        await service.ProcessQueueAsync();

        var sent = Assert.Single(transport.Sent);
        Assert.Equal("orders@libanpost.example", sent.FromAddress);
        Assert.Equal("LibanPost Orders", sent.FromName);
        Assert.Equal("support@libanpost.example", sent.ReplyTo);
    }

    [Fact]
    public async Task A_transport_failure_is_retried_rather_than_lost()
    {
        using var db = TestSupport.NewDb();
        var transport = new RecordingTransport();
        transport.FailNext(new InvalidOperationException("mail server said no"));
        var service = NewService(db, transport, TestSupport.EmailOptions(o => o.MaxAttempts = 4));
        await service.EnqueueAsync(Draft());

        var delivered = await service.ProcessQueueAsync();

        Assert.Equal(0, delivered);
        var stored = await db.EmailMessages.SingleAsync();
        Assert.Equal(EmailStatus.Queued, stored.Status);        // still owed
        Assert.Equal(1, stored.Attempts);
        Assert.Equal("mail server said no", stored.LastError);
        Assert.NotNull(stored.NextAttemptUtc);                  // backed off
    }

    [Fact]
    public async Task A_message_that_keeps_failing_is_parked_as_failed_after_max_attempts()
    {
        using var db = TestSupport.NewDb();
        var transport = new RecordingTransport();
        transport.FailNext(
            new InvalidOperationException("boom 1"),
            new InvalidOperationException("boom 2"));
        var service = NewService(db, transport, TestSupport.EmailOptions(o => o.MaxAttempts = 2));
        await service.EnqueueAsync(Draft());

        await service.ProcessQueueAsync();
        // Clear the backoff so the next pass may try again, the way the dispatcher would once the
        // delay has elapsed.
        var stored = await db.EmailMessages.SingleAsync();
        stored.NextAttemptUtc = null;
        await db.SaveChangesAsync();

        await service.ProcessQueueAsync();

        stored = await db.EmailMessages.SingleAsync();
        Assert.Equal(EmailStatus.Failed, stored.Status);
        Assert.Equal(2, stored.Attempts);
        Assert.Null(stored.NextAttemptUtc);
    }

    [Fact]
    public async Task A_message_inside_its_backoff_window_is_not_retried_yet()
    {
        using var db = TestSupport.NewDb();
        var transport = new RecordingTransport();
        transport.FailNext(new InvalidOperationException("boom"));
        var service = NewService(db, transport);
        await service.EnqueueAsync(Draft());

        await service.ProcessQueueAsync();          // fails, sets NextAttemptUtc in the future
        var delivered = await service.ProcessQueueAsync();

        Assert.Equal(0, delivered);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed_as_a_delivery_failure()
    {
        using var db = TestSupport.NewDb();
        var service = NewService(db, new CancellingTransport());
        await service.EnqueueAsync(Draft());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ProcessQueueAsync(ct: cts.Token));
    }

    [Fact]
    public async Task Resend_returns_a_failed_message_to_the_queue_with_a_clean_history()
    {
        using var db = TestSupport.NewDb();
        var transport = new RecordingTransport();
        var service = NewService(db, transport);

        var message = await service.EnqueueAsync(Draft());
        message!.Status = EmailStatus.Failed;
        message.Attempts = 3;
        message.LastError = "hopeless";
        message.SentUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var ok = await service.ResendAsync(message.Id);

        Assert.True(ok);
        var stored = await db.EmailMessages.SingleAsync();
        Assert.Equal(EmailStatus.Queued, stored.Status);
        Assert.Equal(0, stored.Attempts);
        Assert.Null(stored.LastError);
        Assert.Null(stored.SentUtc);
    }

    [Fact]
    public async Task Resend_reports_failure_for_a_message_that_does_not_exist()
    {
        using var db = TestSupport.NewDb();
        var service = NewService(db, new RecordingTransport());

        Assert.False(await service.ResendAsync(4242));
    }

    [Fact]
    public async Task Counts_summarise_the_outbox_by_state()
    {
        using var db = TestSupport.NewDb();
        var service = NewService(db, new RecordingTransport());

        await service.EnqueueAsync(Draft());                                  // oldest: will be failed
        await service.EnqueueAsync(Draft(to: "a@example.com"));
        await service.EnqueueAsync(Draft(to: "b@example.com"));
        await service.EnqueueAsync(Draft(to: "c@example.com"));

        // Deliver-able? No: the transport is told to throw, and MaxAttempts is 1, so the oldest
        // message is parked as Failed on its very first pass.
        var transport = new RecordingTransport();
        transport.FailNext(new InvalidOperationException("nope"));
        var failing = NewService(db, transport, TestSupport.EmailOptions(o => o.MaxAttempts = 1));
        await failing.ProcessQueueAsync(1);

        var counts = await service.GetCountsAsync();

        Assert.Equal(4, counts.Total);
        Assert.Equal(1, counts.Failed);
        Assert.True(counts.Queued >= 1);
        Assert.Equal(0, counts.Skipped);
    }

    [Fact]
    public async Task The_outbox_page_filters_by_status()
    {
        using var db = TestSupport.NewDb();
        var service = NewService(db, new RecordingTransport());

        var queued = await service.EnqueueAsync(Draft(to: "q@example.com"));
        var failed = await service.EnqueueAsync(Draft(to: "f@example.com"));
        failed!.Status = EmailStatus.Failed;
        await db.SaveChangesAsync();

        var page = await service.GetOutboxAsync(EmailStatus.Failed, kind: null, page: 1, pageSize: 10);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(failed.Id, Assert.Single(page.Items).Id);
        Assert.NotNull(queued);
    }

    private sealed class CancellingTransport : IEmailTransport
    {
        public string Name => "cancelling";

        public Task SendAsync(EmailEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
