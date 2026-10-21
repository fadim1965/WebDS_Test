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
/// The hosted-payment path is the one code path a third party can reach anonymously, so these tests
/// pin down its contract: a signature is required before anything is trusted, an event is applied at
/// most once, and only a successful capture moves an order forward.
/// </summary>
public class PaymentWebhookTests
{
    private const string Secret = "test-webhook-secret";

    private sealed class Harness
    {
        public required ApplicationDbContext Db { get; init; }
        public required SimulatorPaymentGateway Gateway { get; init; }
        public required PaymentWebhookHandler Handler { get; init; }
        public required OrderService Orders { get; init; }

        /// <summary>A signed delivery, exactly as the provider would post it.</summary>
        public string Sign(string body) => Gateway.Sign(body);

        public string Payload(
            string orderNumber,
            bool success = true,
            string? eventId = null,
            decimal amount = 58.89m,
            string? transactionId = "sim_txn_1") =>
            Gateway.Serialize(new SimulatorPaymentGateway.SimulatorEvent(
                EventId: eventId ?? Guid.NewGuid().ToString("N"),
                Type: success ? "payment.succeeded" : "payment.failed",
                OrderNumber: orderNumber,
                TransactionId: transactionId,
                Amount: amount,
                Currency: "USD",
                Success: success,
                FailureReason: success ? null : "The card was declined."));
    }

    private static Harness Build(ApplicationDbContext db)
    {
        var gateway = new SimulatorPaymentGateway(
            TestSupport.Options(new PaymentSettings { Provider = SimulatorPaymentGateway.Name, WebhookSecret = Secret }),
            NullLogger<SimulatorPaymentGateway>.Instance);

        var emails = new Mock<IEmailService>(MockBehavior.Loose);

        var orders = new OrderService(
            db,
            gateway,
            emails.Object,
            TestSupport.Options(TestSupport.Store()),
            NullLogger<OrderService>.Instance);

        var handler = new PaymentWebhookHandler(
            db,
            new IPaymentGateway[] { gateway },
            orders,
            NullLogger<PaymentWebhookHandler>.Instance);

        return new Harness { Db = db, Gateway = gateway, Handler = handler, Orders = orders };
    }

    /// <summary>A pending card order with the pending payment row checkout would have written.</summary>
    private static Order PendingCardOrder(string number = "ORD-2026-0001")
    {
        var order = TestSupport.Order(number: number);
        order.PaymentMethod = PaymentMethod.Card;
        order.CollectionStatus = CollectionStatus.NotApplicable;
        order.CodAmount = 0m;
        order.Payments.Add(new Payment
        {
            Provider = SimulatorPaymentGateway.Name,
            Amount = order.GrandTotal,
            Currency = "USD",
            Status = PaymentStatus.Pending
        });
        return order;
    }

    // ------------------------------------------------------------------
    // The gateway's own contract
    // ------------------------------------------------------------------

    [Fact]
    public void A_signature_only_matches_the_body_it_was_created_for()
    {
        var h = Build(TestSupport.NewDb());
        var body = h.Payload("ORD-2026-0001");
        var signature = h.Sign(body);

        Assert.True(h.Gateway.VerifySignature(body, signature));
        Assert.False(h.Gateway.VerifySignature(body + " ", signature));
        Assert.False(h.Gateway.VerifySignature(h.Payload("ORD-2026-0002"), signature));
        Assert.False(h.Gateway.VerifySignature(body, "not-a-signature"));
        Assert.False(h.Gateway.VerifySignature(body, null));
        Assert.False(h.Gateway.VerifySignature(body, ""));
    }

    [Fact]
    public void A_signature_is_case_insensitive_so_a_provider_header_still_verifies()
    {
        var h = Build(TestSupport.NewDb());
        var body = h.Payload("ORD-2026-0001");

        Assert.True(h.Gateway.VerifySignature(body, h.Sign(body).ToUpperInvariant()));
    }

    [Fact]
    public void A_payload_round_trips_through_the_wire_format()
    {
        var h = Build(TestSupport.NewDb());
        var body = h.Payload("ORD-2026-0042", transactionId: "txn-42", amount: 12.34m);

        var parsed = h.Gateway.ParseWebhook(body);

        Assert.NotNull(parsed);
        Assert.Equal("ORD-2026-0042", parsed.OrderNumber);
        Assert.True(parsed.Success);
        Assert.Equal("txn-42", parsed.TransactionId);
        Assert.Equal(12.34m, parsed.Amount);
        Assert.Equal("payment.succeeded", parsed.Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"type\":\"payment.succeeded\"}")]
    public void An_unrecognisable_body_does_not_parse(string body)
    {
        var h = Build(TestSupport.NewDb());
        Assert.Null(h.Gateway.ParseWebhook(body));
    }

    [Fact]
    public async Task Creating_an_intent_points_at_the_simulators_payment_page()
    {
        var h = Build(TestSupport.NewDb());
        var order = PendingCardOrder();

        var intent = await h.Gateway.CreateIntentAsync(order, "/Payments/Return?id=1");

        Assert.Equal(order.OrderNumber, intent.Reference);
        Assert.Contains("/Payments/Simulator", intent.RedirectUrl);
        Assert.Contains(Uri.EscapeDataString(order.OrderNumber), intent.RedirectUrl);
        Assert.Contains(Uri.EscapeDataString("/Payments/Return?id=1"), intent.RedirectUrl);
    }

    // ------------------------------------------------------------------
    // The webhook handler
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_signed_capture_pays_the_order_and_the_pending_payment()
    {
        using var db = TestSupport.NewDb();
        db.Orders.Add(PendingCardOrder());
        await db.SaveChangesAsync();
        var h = Build(db);

        var body = h.Payload("ORD-2026-0001");
        var result = await h.Handler.HandleAsync(SimulatorPaymentGateway.Name, body, h.Sign(body));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("paid", result.Outcome);

        var order = await db.Orders.Include(o => o.Payments).SingleAsync();
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.NotNull(order.PaidUtc);

        var payment = order.Payments.Single();
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal("sim_txn_1", payment.TransactionId);
        Assert.True(payment.IsTest);
        Assert.NotNull(payment.CompletedUtc);

        var record = await db.PaymentEvents.SingleAsync();
        Assert.Equal("paid", record.Outcome);
        Assert.True(record.SignatureValid);
        Assert.Equal(order.Id, record.OrderId);
    }

    [Fact]
    public async Task A_replayed_event_is_acknowledged_but_applied_only_once()
    {
        using var db = TestSupport.NewDb();
        db.Orders.Add(PendingCardOrder());
        await db.SaveChangesAsync();
        var h = Build(db);

        var body = h.Payload("ORD-2026-0001", eventId: "evt-fixed");
        var signature = h.Sign(body);

        var first = await h.Handler.HandleAsync(SimulatorPaymentGateway.Name, body, signature);
        var second = await h.Handler.HandleAsync(SimulatorPaymentGateway.Name, body, signature);

        Assert.Equal("paid", first.Outcome);
        Assert.Equal("duplicate", second.Outcome);
        Assert.Equal(200, second.StatusCode);

        // One delivery recorded, one payment row, one capture.
        Assert.Equal(1, await db.PaymentEvents.CountAsync());
        Assert.Equal(1, await db.Payments.CountAsync());
    }

    [Fact]
    public async Task A_body_that_fails_verification_is_refused_and_changes_nothing()
    {
        using var db = TestSupport.NewDb();
        db.Orders.Add(PendingCardOrder());
        await db.SaveChangesAsync();
        var h = Build(db);

        var body = h.Payload("ORD-2026-0001");
        var result = await h.Handler.HandleAsync(SimulatorPaymentGateway.Name, body, "forged-signature");

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("rejected", result.Outcome);

        var order = await db.Orders.Include(o => o.Payments).SingleAsync();
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(PaymentStatus.Pending, order.Payments.Single().Status);

        var record = await db.PaymentEvents.SingleAsync();
        Assert.False(record.SignatureValid);
        Assert.Equal("rejected", record.Outcome);
    }

    [Fact]
    public async Task A_declined_payment_leaves_the_order_pending_so_it_can_be_retried()
    {
        using var db = TestSupport.NewDb();
        db.Orders.Add(PendingCardOrder());
        await db.SaveChangesAsync();
        var h = Build(db);

        var body = h.Payload("ORD-2026-0001", success: false, transactionId: null);
        var result = await h.Handler.HandleAsync(SimulatorPaymentGateway.Name, body, h.Sign(body));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("payment-failed", result.Outcome);

        var order = await db.Orders.Include(o => o.Payments).SingleAsync();
        Assert.Equal(OrderStatus.Pending, order.Status);

        var payment = order.Payments.Single();
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Contains("declined", payment.FailureReason);
    }

    [Fact]
    public async Task An_event_for_an_unknown_order_is_recorded_and_acknowledged()
    {
        using var db = TestSupport.NewDb();
        var h = Build(db);

        var body = h.Payload("ORD-2026-9999");
        var result = await h.Handler.HandleAsync(SimulatorPaymentGateway.Name, body, h.Sign(body));

        // A permanent 4xx would make the provider retry forever, so an unknown order is a 200 with
        // the fact recorded.
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("order-not-found", result.Outcome);

        var record = await db.PaymentEvents.SingleAsync();
        Assert.Null(record.OrderId);
        Assert.Equal("ORD-2026-9999", record.OrderNumber);
    }

    [Fact]
    public async Task An_unknown_provider_is_a_404()
    {
        using var db = TestSupport.NewDb();
        var h = Build(db);

        var result = await h.Handler.HandleAsync("stripe", "{}", "whatever");

        Assert.Equal(404, result.StatusCode);
        Assert.Equal("unknown-provider", result.Outcome);
    }

    // ------------------------------------------------------------------
    // OrderService capture semantics
    // ------------------------------------------------------------------

    [Fact]
    public async Task Capturing_twice_does_not_move_the_paid_timestamp()
    {
        using var db = TestSupport.NewDb();
        db.Orders.Add(PendingCardOrder());
        await db.SaveChangesAsync();
        var h = Build(db);

        var first = await h.Orders.MarkPaidAsync(1, SimulatorPaymentGateway.Name, "txn-a", 58.89m);
        var paidAt = (await db.Orders.SingleAsync()).PaidUtc;

        await Task.Delay(10);
        var second = await h.Orders.MarkPaidAsync(1, SimulatorPaymentGateway.Name, "txn-b", 58.89m);

        Assert.True(first.Success);
        Assert.True(second.Success);

        var order = await db.Orders.SingleAsync();
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal(paidAt, order.PaidUtc);                 // unchanged by the replay
        Assert.Equal("txn-a", (await db.Payments.SingleAsync()).TransactionId);
    }

    [Fact]
    public async Task A_cancelled_order_cannot_be_captured()
    {
        using var db = TestSupport.NewDb();
        var order = PendingCardOrder();
        order.Status = OrderStatus.Cancelled;
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        var h = Build(db);

        var result = await h.Orders.MarkPaidAsync(1, SimulatorPaymentGateway.Name, "txn-late", 58.89m);

        Assert.False(result.Success);
        Assert.Equal(OrderStatus.Cancelled, (await db.Orders.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_late_decline_does_not_downgrade_a_paid_order()
    {
        using var db = TestSupport.NewDb();
        var order = PendingCardOrder();
        order.Status = OrderStatus.Paid;
        order.Payments.Single().Status = PaymentStatus.Succeeded;
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        var h = Build(db);

        var result = await h.Orders.MarkPaymentFailedAsync(1, SimulatorPaymentGateway.Name, "stale decline");

        Assert.True(result.Success);
        var stored = await db.Orders.SingleAsync();
        Assert.Equal(OrderStatus.Paid, stored.Status);
        Assert.Equal(PaymentStatus.Succeeded, (await db.Payments.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_capture_for_an_order_with_no_payment_row_still_records_one()
    {
        using var db = TestSupport.NewDb();
        var order = TestSupport.Order();
        order.PaymentMethod = PaymentMethod.Card;
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        var h = Build(db);

        var result = await h.Orders.MarkPaidAsync(1, SimulatorPaymentGateway.Name, "txn-orphan", 58.89m);

        Assert.True(result.Success);
        var payment = await db.Payments.SingleAsync();
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(58.89m, payment.Amount);
    }
}
