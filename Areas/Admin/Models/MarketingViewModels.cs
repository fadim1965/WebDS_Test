using Microsoft.EntityFrameworkCore;
using WebDS_Test.Domain;

namespace WebDS_Test.Areas.Admin.Models;

/// <summary>All figures a marketing department needs to steer the store: revenue trend,
/// best sellers, category mix, order funnel, customer acquisition and coupon performance.</summary>
public sealed class MarketingDashboardStats
{
    public IReadOnlyList<DailyRevenue> DailyRevenue { get; init; } = Array.Empty<DailyRevenue>();
    public decimal TotalRevenue { get; init; }
    public decimal RevenueLast30Days { get; init; }
    public int TotalOrders { get; init; }
    public int OrdersLast30Days { get; init; }
    public decimal AverageOrderValue { get; init; }
    public decimal AverageOrderValueLast30Days { get; init; }

    public IReadOnlyList<OrderStatusCount> StatusBreakdown { get; init; } = Array.Empty<OrderStatusCount>();
    public decimal CancellationRate { get; init; }
    public decimal RefundRate { get; init; }

    public IReadOnlyList<ProductSalesRank> TopProductsByRevenue { get; init; } = Array.Empty<ProductSalesRank>();
    public IReadOnlyList<ProductSalesRank> TopProductsByUnits { get; init; } = Array.Empty<ProductSalesRank>();
    public IReadOnlyList<CategorySales> RevenueByCategory { get; init; } = Array.Empty<CategorySales>();

    public int NewCustomersLast30Days { get; init; }
    public int ReturningCustomersLast30Days { get; init; }
    public IReadOnlyList<CustomerRank> TopCustomers { get; init; } = Array.Empty<CustomerRank>();

    public IReadOnlyList<PaymentMethodBreakdown> PaymentMethods { get; init; } = Array.Empty<PaymentMethodBreakdown>();
    public IReadOnlyList<CouponPerformance> CouponPerformance { get; init; } = Array.Empty<CouponPerformance>();
    public IReadOnlyList<FastMovingVariant> FastMoving { get; init; } = Array.Empty<FastMovingVariant>();
}

public sealed class DailyRevenue
{
    public DateTime Date { get; init; }
    public decimal Revenue { get; init; }
    public int Orders { get; init; }
}

public sealed class OrderStatusCount
{
    public OrderStatus Status { get; init; }
    public int Count { get; init; }
    public decimal Revenue { get; init; }
    public string Label { get; init; } = string.Empty;
    public string CssClass { get; init; } = string.Empty;
}

public sealed class ProductSalesRank
{
    public int ProductId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Sku { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public int Units { get; init; }
    public int Orders { get; init; }
    public decimal Revenue { get; init; }

    public decimal AverageOrderValue => Units > 0 ? Revenue / Units : 0m;
}

public sealed class CategorySales
{
    public string Name { get; init; } = string.Empty;
    public int Products { get; init; }
    public int Orders { get; init; }
    public int Units { get; init; }
    public decimal Revenue { get; init; }
}

public sealed class CustomerRank
{
    public string UserId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public int Orders { get; init; }
    public decimal Spend { get; init; }
    public DateTime? LastOrder { get; init; }
}

public sealed class PaymentMethodBreakdown
{
    public PaymentMethod Method { get; init; }
    public string Label { get; init; } = string.Empty;
    public int Orders { get; init; }
    public decimal RevenueRaw { get; init; }
    public decimal Revenue => Orders > 0 ? RevenueRaw / Orders : 0m;
}

public sealed class CouponPerformance
{
    public int CouponId { get; init; }
    public string Code { get; init; } = string.Empty;
    public int MaxUses { get; init; }
    public int UsedCount => Uses.Count;
    public decimal DiscountGiven { get; init; }
    public decimal InfluencedRevenue { get; init; }
    public double RedemptionRate => MaxUses > 0 ? (double)Uses.Count / MaxUses * 100 : 0;
    public IReadOnlyList<CouponUse> Uses { get; init; } = Array.Empty<CouponUse>();
}

public sealed class CouponUse
{
    public string OrderNumber { get; init; } = string.Empty;
    public string Customer { get; init; } = string.Empty;
    public string CouponCode { get; init; } = string.Empty;
    public decimal OrderTotal { get; init; }

    /// <summary>The discount checkout actually applied, from <c>Order.DiscountTotal</c>.</summary>
    public decimal Discount { get; init; }
    public DateTime PlacedUtc { get; init; }
}

public sealed class FastMovingVariant
{
    public int ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string VariantSku { get; init; } = string.Empty;
    public int Units { get; init; }
    public int Orders { get; init; }
    public int StockRemaining { get; init; }
    public bool LowStock => StockRemaining <= 5;
}
