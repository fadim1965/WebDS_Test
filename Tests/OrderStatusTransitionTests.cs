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
/// Fulfilment is a state machine, and it is the one place in the store where a wrong move either
/// refunds money it should not or releases stock it should not. These tests pin the allowed moves
/// down, then drive <see cref="OrderService.UpdateStatusAsync"/> for the side effects that matter:
/// stock on cancellation, refunds, tracking and notifications.
/// </summary>
public class OrderStatusTransitionTests
{
    // ------------------------------------------------------------------
    // The transition table itself
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.Paid)]
    [InlineData(OrderStatus.Pending, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Paid, OrderStatus.Shipped)]
    [InlineData(OrderStatus.Paid, OrderStatus.Refunded)]
    [InlineData(OrderStatus.Paid, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Shipped, OrderStatus.Delivered)]
    [InlineData(OrderStatus.Shipped, OrderStatus.Refunded)]
    [InlineData(OrderStatus.Delivered, OrderStatus.Refunded)]
    public void The_expected_moves_are_allowed(OrderStatus from, OrderStatus to) =>
        Assert.Contains(to, from.AllowedTransitions());

    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.Shipped)]
    [InlineData(OrderStatus.Pending, OrderStatus.Delivered)]
    [InlineData(OrderStatus.Pending, OrderStatus.Refunded)]
    [InlineData(OrderStatus.Shipped, OrderStatus.Cancelled)]     // goods are gone; refund instead
    [InlineData(OrderStatus.Delivered, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Paid)]
    [InlineData(OrderStatus.Refunded, OrderStatus.Paid)]
    public void The_forbidden_moves_are_refused(OrderStatus from, OrderStatus to) =>
        Assert.DoesNotContain(to, from.AllowedTransitions());

    [Fact]
    public void A_terminal_state_has_no_way_out()
    {
        Assert.Empty(OrderStatus.Cancelled.AllowedTransitions());
        Assert.Empty(OrderStatus.Refunded.AllowedTransitions());
    }

    [Theory]
    [InlineData(OrderStatus.Pending, true)]
    [InlineData(OrderStatus.Paid, true)]
    [InlineData(OrderStatus.Shipped, true)]
    [InlineData(OrderStatus.Delivered, true)]
    [InlineData(OrderStatus.Cancelled, false)]
    [InlineData(OrderStatus.Refunded, false)]
    public void Stock_is_held_only_until_an_order_is_closed(OrderStatus status, bool holds) =>
        Assert.Equal(holds, status.HoldsStock());

    // ------------------------------------------------------------------
    // UpdateStatusAsync side effects
    // ------------------------------------------------------------------

    private sealed class Harness
    {
        public required ApplicationDbContext Db { get; init; }
        public required OrderService Service { get; init; }
        public required Mock<IEmailService> Emails { get; init; }
        public required Mock<IPaymentGateway> Payments { get; init; }
    }

    private static Harness Build(ApplicationDbContext db)
    {
        var emails = new Mock<IEmailService>(MockBehavior.Loose);
        emails.Setup(e => e.NotifyOrderShippedAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);
        emails.Setup(e => e.NotifyOrderCancelledAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        var payments = new Mock<IPaymentGateway>(MockBehavior.Loose);
        payments.Setup(p => p.RefundAsync(It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PaymentReceipt(true, "refund-1"));

        var service = new OrderService(
            db,
            payments.Object,
            emails.Object,
            TestSupport.Options(TestSupport.Store()),
            NullLogger<OrderService>.Instance);

        return new Harness { Db = db, Service = service, Emails = emails, Payments = payments };
    }

    private static ProductVariant Variant(int id, int stock) =>
        new() { Id = id, ProductId = 1, Sku = $"SKU-{id}", Name = "Pack of 10", Price = 24.50m, StockQuantity = stock };

    [Fact]
    public async Task Moving_to_paid_stamps_the_time()
    {
        using var db = TestSupport.NewDb();
        var order = TestSupport.Order(status: OrderStatus.Pending);
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        var h = Build(db);

        var result = await h.Service.UpdateStatusAsync(order.Id, OrderStatus.Paid);

        Assert.True(result.Success);
        var stored = await db.Orders.SingleAsync();
        Assert.Equal(OrderStatus.Paid, stored.Status);
        Assert.NotNull(stored.PaidUtc);
    }

    [Fact]
    public async Task An_illegal_move_is_refused_and_leaves_the_order_alone()
    {
        using var db = TestSupport.NewDb();
        var order = TestSupport.Order(status: OrderStatus.Pending);
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        var h = Build(db);

        var result = await h.Service.UpdateStatusAsync(order.Id, OrderStatus.Delivered);

        Assert.False(result.Success);
        Assert.Contains("cannot move to", result.Message);
        var stored = await db.Orders.SingleAsync();
        Assert.Equal(OrderStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task An_unknown_order_is_reported_rather_than_thrown()
    {
        using var db = TestSupport.NewDb();
        var h = Build(db);

        var result = await h.Service.UpdateStatusAsync(999, OrderStatus.Paid);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public async Task Moving_to_the_state_it_is_already_in_is_a_no_op_success()
    {
        using var db = TestSupport.NewDb();
        var order = TestSupport.Order(status: OrderStatus.Paid);
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        var h = Build(db);

        var result = await h.Service.UpdateStatusAsync(order.Id, OrderStatus.Paid);

        Assert.True(result.Success);
        Assert.Contains("already", result.Message);
    }

    [Fact]
    public async Task Shipping_records_the_tracking_number_and_tells_the_shopper()
    {
        using var db = TestSupport.NewDb();
        var order = TestSupport.Order(status: OrderStatus.Paid);
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        var h = Build(db);

        var result = await h.Service.UpdateStatusAsync(order.Id, OrderStatus.Shipped, "  LP123456789LB  ");

        Assert.True(result.Success);
        var stored = await db.Orders.SingleAsync();
        Assert.Equal(OrderStatus.Shipped, stored.Status);
        Assert.NotNull(stored.ShippedUtc);
        Assert.Equal("LP123456789LB", stored.TrackingNumber);   // trimmed
        h.Emails.Verify(
            e => e.NotifyOrderShippedAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delivering_does_not_send_mail()
    {
        using var db = TestSupport.NewDb();
        var order = TestSupport.Order(status: OrderStatus.Shipped);
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        var h = Build(db);

        var result = await h.Service.UpdateStatusAsync(order.Id, OrderStatus.Delivered);

        Assert.True(result.Success);
        h.Emails.Verify(
            e => e.NotifyOrderShippedAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Emails.Verify(
            e => e.NotifyOrderCancelledAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cancelling_a_paid_order_returns_the_stock_and_ledgers_it()
    {
        using var db = TestSupport.NewDb();
        var variant = Variant(id: 1, stock: 5);
        db.ProductVariants.Add(variant);

        var order = TestSupport.Order(status: OrderStatus.Paid);   // line quantity is 2
        order.Lines.Single().ProductVariantId = 1;
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        var h = Build(db);

        var result = await h.Service.UpdateStatusAsync(order.Id, OrderStatus.Cancelled);

        Assert.True(result.Success);
        var storedVariant = await db.ProductVariants.SingleAsync();
        Assert.Equal(7, storedVariant.StockQuantity);              // 5 + 2 back

        var movement = await db.StockMovements.SingleAsync();
        Assert.Equal(StockMovementReason.Cancellation, movement.Reason);
        Assert.Equal(2, movement.QuantityDelta);
        Assert.Equal(7, movement.StockAfter);

        h.Emails.Verify(
            e => e.NotifyOrderCancelledAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Refunding_a_paid_order_calls_the_gateway_for_each_successful_payment()
    {
        // The service runs in its own context, the way a request-scoped one would; the payment rows
        // therefore have to be loaded by the query, not by change-tracker luck.
        var name = TestSupport.NewDbName();
        await using (var seed = TestSupport.NewDb(name))
        {
            var order = TestSupport.Order(status: OrderStatus.Paid);
            order.Payments.Add(new Payment
            {
                OrderId = order.Id,
                Provider = "sandbox",
                TransactionId = "txn-1",
                Amount = 58.89m,
                Status = PaymentStatus.Succeeded
            });
            seed.Orders.Add(order);
            await seed.SaveChangesAsync();
        }

        using var db = TestSupport.NewDb(name);
        var h = Build(db);

        var result = await h.Service.UpdateStatusAsync(1, OrderStatus.Refunded);

        Assert.True(result.Success);
        h.Payments.Verify(
            p => p.RefundAsync("txn-1", 58.89m, It.IsAny<CancellationToken>()), Times.Once);

        var payment = await db.Payments.SingleAsync();
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.NotNull(payment.CompletedUtc);
    }

    [Fact]
    public async Task Refunding_does_not_return_stock()
    {
        // Refund and cancel differ deliberately: a refund leaves inventory alone.
        using var db = TestSupport.NewDb();
        var variant = Variant(id: 1, stock: 5);
        db.ProductVariants.Add(variant);

        var order = TestSupport.Order(status: OrderStatus.Paid);
        order.Lines.Single().ProductVariantId = 1;
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        var h = Build(db);

        await h.Service.UpdateStatusAsync(order.Id, OrderStatus.Refunded);

        var storedVariant = await db.ProductVariants.SingleAsync();
        Assert.Equal(5, storedVariant.StockQuantity);
        Assert.Equal(0, await db.StockMovements.CountAsync());
    }

    [Fact]
    public async Task A_notification_failure_does_not_undo_the_status_change()
    {
        using var db = TestSupport.NewDb();
        var order = TestSupport.Order(status: OrderStatus.Paid);
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var h = Build(db);
        h.Emails
            .Setup(e => e.NotifyOrderShippedAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no mail server"));

        var result = await h.Service.UpdateStatusAsync(order.Id, OrderStatus.Shipped);

        Assert.True(result.Success);
        var stored = await db.Orders.SingleAsync();
        Assert.Equal(OrderStatus.Shipped, stored.Status);
    }
}
