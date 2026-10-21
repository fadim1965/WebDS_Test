using Microsoft.EntityFrameworkCore;
using WebDS_Test.Areas.Admin.Models;

namespace WebDS_Test.Services;

/// <summary>Customer-supplied checkout input.</summary>
public sealed class CheckoutRequest
{
    public string Email { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string? Phone { get; set; }

    public string ShippingName { get; set; } = string.Empty;
    public string ShippingLine1 { get; set; } = string.Empty;
    public string? ShippingLine2 { get; set; }
    public string ShippingCity { get; set; } = string.Empty;
    public string? ShippingState { get; set; }
    public string ShippingPostalCode { get; set; } = string.Empty;
    public string ShippingCountry { get; set; } = string.Empty;

    public string? CustomerNote { get; set; }

    /// <summary>Guards against a double submission creating two orders.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// How the shopper chose to pay. Cash on delivery is the default the marketplace was built
    /// around; card hands the order to a hosted provider and is confirmed by its webhook.
    /// </summary>
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.CashOnDelivery;

    public bool SaveAddressToBook { get; set; }
    public string? CouponCode { get; set; }
    public int? CouponId { get; set; }
}

/// <summary>Outcome of placing an order (or several, one per shop).</summary>
public sealed record OrderResult(bool Success, string Message, IReadOnlyList<Order> Orders)
{
    /// <summary>The first order placed, for callers that expect a single result (checkout is single-shop today).</summary>
    public Order? Order => Orders.Count > 0 ? Orders[0] : null;

    public static OrderResult Ok(Order order, string message) => new(true, message, new[] { order });
    public static OrderResult Ok(IEnumerable<Order> orders, string message) =>
        new(true, message, orders?.ToList() ?? new List<Order>());
    public static OrderResult Fail(string message) => new(false, message, Array.Empty<Order>());
}

/// <summary>Order placement, history and fulfilment transitions.</summary>
public interface IOrderService
{
    Task<OrderResult> PlaceOrderAsync(CheckoutRequest request, Cart cart, CancellationToken ct = default);

    Task<IReadOnlyList<Order>> GetOrdersForUserAsync(string userId, CancellationToken ct = default);

    /// <summary>Loads an order with its lines, restricted to its owner when <paramref name="userId"/> is given.</summary>
    Task<Order?> GetOrderAsync(int id, string? userId = null, CancellationToken ct = default);

    Task<Order?> GetByNumberAsync(string orderNumber, string? userId = null, CancellationToken ct = default);

    Task<IReadOnlyList<Order>> GetAllAsync(OrderStatus? status = null, string? search = null, int take = 100, CancellationToken ct = default);

    /// <summary>Moves an order to a new status, adjusting inventory when the change warrants it.</summary>
    Task<OrderResult> UpdateStatusAsync(int orderId, OrderStatus target, string? trackingNumber = null, CancellationToken ct = default);

    /// <summary>
    /// Captures a hosted payment: marks the order paid and its pending payment succeeded. Called
    /// from the provider webhook, so it is idempotent — a repeated event must not double-apply.
    /// </summary>
    Task<OrderResult> MarkPaidAsync(
        int orderId, string provider, string? transactionId, decimal amount, CancellationToken ct = default);

    /// <summary>
    /// Records a declined hosted payment. The order is deliberately left pending so the shopper can
    /// try again; only a successful capture moves it forward.
    /// </summary>
    Task<OrderResult> MarkPaymentFailedAsync(
        int orderId, string provider, string? reason, CancellationToken ct = default);

    Task<OrderDashboardStats> GetDashboardStatsAsync(CancellationToken ct = default);

    Task<MarketingDashboardStats> GetMarketingDashboardAsync(CancellationToken ct = default);
}

/// <summary>Headline numbers for the admin dashboard.</summary>
public sealed class OrderDashboardStats
{
    public int TotalOrders { get; init; }
    public int PendingOrders { get; init; }
    public int PaidOrders { get; init; }
    public int ShippedOrders { get; init; }
    public decimal Revenue { get; init; }
    public decimal RevenueLast30Days { get; init; }
    public int OrdersLast30Days { get; init; }
    public int LowStockVariants { get; init; }
    public int OutOfStockVariants { get; init; }
    public int ProductCount { get; init; }
    public int CustomerCount { get; init; }

    /// <summary>Retail value of everything currently on the shelf.</summary>
    public decimal InventoryValue { get; init; }

    public IReadOnlyList<Order> RecentOrders { get; init; } = Array.Empty<Order>();

    /// <summary>Variants at or below their low-stock threshold, emptiest first.</summary>
    public IReadOnlyList<LowStockRow> LowStock { get; init; } = Array.Empty<LowStockRow>();
}

/// <summary>A low-stock variant projected for the dashboard, so the view needs no extra query.</summary>
public sealed class LowStockRow
{
    public string Sku { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public int StockQuantity { get; init; }
    public int LowStockThreshold { get; init; }
}
