using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services;
using WebDS_Test.Services.Email;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// Regressions for three defects in the marketing dashboard, all of which shipped past a green
/// build and a passing suite because no test asserted on the figure they got wrong.
/// </summary>
/// <remarks>
/// The coupon case is the one worth reading twice. The dashboard reported the discount a coupon
/// "gave" by multiplying the post-discount order total by the coupon's own rules: a percentage was
/// applied to the wrong base, and a fixed-amount code fell into a default branch that multiplied by
/// one — so a $5 code on a $30 order reported $30 of discount. The fix reads the discount checkout
/// already recorded on the order, which is the only number that cannot disagree with the order.
/// </remarks>
public class MarketingDashboardTests
{
    private static OrderService Orders(ApplicationDbContext db)
    {
        var gateway = new Mock<IPaymentGateway>(MockBehavior.Loose);
        var emails = new Mock<IEmailService>(MockBehavior.Loose);

        return new OrderService(
            db,
            gateway.Object,
            emails.Object,
            TestSupport.Options(TestSupport.Store()),
            NullLogger<OrderService>.Instance);
    }

    /// <summary>A paid order, because the dashboard counts only money actually collected.</summary>
    private static Order Paid(int id, decimal subtotal, decimal discount = 0m, string? coupon = null)
    {
        var order = TestSupport.Order(id: id, number: $"ORD-2026-{id:D4}", status: OrderStatus.Paid);
        order.Subtotal = subtotal;
        order.DiscountTotal = discount;
        order.GrandTotal = subtotal - discount;
        order.CouponCode = coupon;
        return order;
    }

    // ------------------------------------------------------------------
    // Bug 1 — the discount given was reconstructed instead of read
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_fixed_amount_coupon_reports_the_amount_it_actually_took_off()
    {
        // The defect: DiscountGiven was computed as `min(coupon rules * GrandTotal, GrandTotal)`.
        // For a fixed amount the rule branch multiplied by 1, so a $5 code on this order claimed to
        // have given $25 — the entire order value.
        await using var db = TestSupport.NewDb();

        db.Coupons.Add(new Coupon
        {
            Code = "POST5",
            Type = DiscountType.FixedAmount,
            Value = 5.00m,
            IsActive = true
        });

        // Checkout applied exactly $5.00 off a $30.00 order.
        db.Orders.Add(Paid(id: 1, subtotal: 30.00m, discount: 5.00m, coupon: "POST5"));
        await db.SaveChangesAsync();

        var stats = await Orders(db).GetMarketingDashboardAsync();

        var coupon = Assert.Single(stats.CouponPerformance);
        Assert.Equal(5.00m, coupon.DiscountGiven);
    }

    [Fact]
    public async Task A_percentage_coupon_reports_the_discount_not_a_share_of_the_discounted_total()
    {
        await using var db = TestSupport.NewDb();

        db.Coupons.Add(new Coupon
        {
            Code = "WELCOME10",
            Type = DiscountType.Percentage,
            Value = 10.00m,
            MaxUsesPerUser = 1,
            IsActive = true
        });

        // 10% off a $100.00 subtotal = $10.00 off; the order total is then $90.00.
        db.Orders.Add(Paid(id: 1, subtotal: 100.00m, discount: 10.00m, coupon: "WELCOME10"));
        await db.SaveChangesAsync();

        var stats = await Orders(db).GetMarketingDashboardAsync();

        var coupon = Assert.Single(stats.CouponPerformance);
        Assert.Equal(10.00m, coupon.DiscountGiven);

        // Influenced revenue is the order total as collected, not the pre-discount subtotal.
        Assert.Equal(90.00m, coupon.InfluencedRevenue);
    }

    [Fact]
    public async Task Coupons_discounts_add_up_across_orders()
    {
        await using var db = TestSupport.NewDb();

        db.Coupons.Add(new Coupon
        {
            Code = "POST5",
            Type = DiscountType.FixedAmount,
            Value = 5.00m,
            IsActive = true
        });

        db.Orders.Add(Paid(id: 1, subtotal: 40.00m, discount: 5.00m, coupon: "POST5"));
        db.Orders.Add(Paid(id: 2, subtotal: 60.00m, discount: 5.00m, coupon: "POST5"));
        await db.SaveChangesAsync();

        var stats = await Orders(db).GetMarketingDashboardAsync();

        var coupon = Assert.Single(stats.CouponPerformance);
        Assert.Equal(2, coupon.UsedCount);
        Assert.Equal(10.00m, coupon.DiscountGiven);
    }

    [Fact]
    public async Task An_order_with_no_discount_contributes_nothing_to_the_discount_total()
    {
        await using var db = TestSupport.NewDb();

        db.Coupons.Add(new Coupon
        {
            Code = "WELCOME10",
            Type = DiscountType.Percentage,
            Value = 10.00m,
            MaxUsesPerUser = 1,
            IsActive = true
        });

        db.Orders.Add(Paid(id: 1, subtotal: 50.00m, discount: 0m, coupon: "WELCOME10"));
        await db.SaveChangesAsync();

        var stats = await Orders(db).GetMarketingDashboardAsync();

        Assert.Equal(0m, Assert.Single(stats.CouponPerformance).DiscountGiven);
    }

    // ------------------------------------------------------------------
    // Bug 2 — a category row carried a product's slug
    // ------------------------------------------------------------------

    [Fact]
    public async Task Category_revenue_sums_every_products_revenue_in_that_category()
    {
        await using var db = TestSupport.NewDb();

        var stamp = new Product
        {
            Id = 1,
            Name = "Registered Mail Kit",
            Slug = "registered-mail-kit",
            Category = new Category { Id = 1, Name = "Registered Mail", Slug = "registered-mail" },
            ShopId = 1,
            IsPublished = true
        };
        var label = new Product
        {
            Id = 2,
            Name = "Thermal Labels",
            Slug = "thermal-labels",
            Category = new Category { Id = 2, Name = "Labels", Slug = "labels" },
            ShopId = 1,
            IsPublished = true
        };
        db.Products.AddRange(stamp, label);

        stamp.Variants.Add(new ProductVariant
        {
            Id = 1,
            ProductId = 1,
            Sku = "REG-1",
            Price = 18.75m,
            StockQuantity = 10,
            IsActive = true
        });
        label.Variants.Add(new ProductVariant
        {
            Id = 2,
            ProductId = 2,
            Sku = "LBL-1",
            Price = 21.40m,
            StockQuantity = 10,
            IsActive = true
        });

        var first = Paid(1, 18.75m);
        first.Lines.Clear();
        first.Lines.Add(new OrderLine
        {
            ProductId = 1,
            ProductVariantId = 1,
            ProductName = stamp.Name,
            Sku = "REG-1",
            Quantity = 1,
            UnitPrice = 18.75m,
            LineTotal = 18.75m
        });

        var second = Paid(2, 21.40m);
        second.Lines.Clear();
        second.Lines.Add(new OrderLine
        {
            ProductId = 2,
            ProductVariantId = 2,
            ProductName = label.Name,
            Sku = "LBL-1",
            Quantity = 1,
            UnitPrice = 21.40m,
            LineTotal = 21.40m
        });


        db.Orders.AddRange(first, second);
        await db.SaveChangesAsync();

        var stats = await Orders(db).GetMarketingDashboardAsync();

        Assert.Equal(2, stats.RevenueByCategory.Count);
        Assert.Equal(18.75m, stats.RevenueByCategory.Single(c => c.Name == "Registered Mail").Revenue);
        Assert.Equal(21.40m, stats.RevenueByCategory.Single(c => c.Name == "Labels").Revenue);
    }

    [Fact]
    public async Task Category_rows_no_longer_expose_a_slug_because_an_order_line_has_none()
    {
        // OrderLine snapshots the product but not the category tree, so the only slug reachable from
        // an order is the product's. Reporting that under a category name reads as a link and is a
        // lie, so the projection must not carry a slug at all.
        await using var db = TestSupport.NewDb();

        db.Orders.Add(Paid(1, 18.75m));
        await db.SaveChangesAsync();

        var stats = await Orders(db).GetMarketingDashboardAsync();

        Assert.Single(stats.RevenueByCategory);
        Assert.DoesNotContain(
            typeof(Areas.Admin.Models.CategorySales).GetProperties(),
            p => p.Name.Equals("Slug", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_line_whose_variant_is_gone_still_counts_its_revenue()
    {
        // Found by the category tests above failing: the projection used to filter on
        // `ProductVariant is not null`, so hard-deleting a variant silently deleted its sales from
        // every product and category figure. An order line is a historical record; it cannot vanish.
        await using var db = TestSupport.NewDb();

        var order = Paid(1, 30.00m);
        order.Lines.Clear();
        order.Lines.Add(new OrderLine
        {
            // No ProductVariantId — the variant was removed after the sale.
            ProductName = "Discontinued Widget",
            Sku = "OLD-1",
            Quantity = 1,
            UnitPrice = 30.00m,
            LineTotal = 30.00m
        });

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var stats = await Orders(db).GetMarketingDashboardAsync();

        var row = Assert.Single(stats.TopProductsByRevenue);
        Assert.Equal("Discontinued Widget", row.Name);
        Assert.Equal(30.00m, row.Revenue);
        Assert.Equal(1, row.Units);
    }

    [Fact]
    public async Task A_renamed_product_does_not_rewrite_the_name_on_an_old_order()
    {
        // The projection used to read the name from the live product, so renaming a product rewrote
        // every historical figure. OrderLine snapshots the name for exactly this reason.
        await using var db = TestSupport.NewDb();

        var product = new Product
        {
            Id = 1,
            Name = "Registered Mail Kit",
            Slug = "registered-mail-kit",
            Category = new Category { Id = 1, Name = "Registered Mail", Slug = "registered-mail" },
            ShopId = 1,
            IsPublished = true
        };
        product.Variants.Add(new ProductVariant
        {
            Id = 1,
            ProductId = 1,
            Sku = "REG-1",
            Price = 18.75m,
            StockQuantity = 10,
            IsActive = true
        });
        db.Products.Add(product);

        var order = Paid(1, 18.75m);
        order.Lines.Clear();
        order.Lines.Add(new OrderLine
        {
            ProductId = 1,
            ProductVariantId = 1,
            ProductName = "Registered Mail Kit",
            Sku = "REG-1",
            Quantity = 1,
            UnitPrice = 18.75m,
            LineTotal = 18.75m
        });
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        // The vendor renames the product after the sale.
        product.Name = "Registered Mail Kit (2026 Edition)";
        await db.SaveChangesAsync();

        var stats = await Orders(db).GetMarketingDashboardAsync();

        Assert.Equal("Registered Mail Kit", Assert.Single(stats.TopProductsByRevenue).Name);
    }

    // ------------------------------------------------------------------
    // Figures that were correct, pinned so they stay correct
    // ------------------------------------------------------------------

    [Fact]
    public async Task Only_collected_money_counts_as_revenue()
    {
        await using var db = TestSupport.NewDb();

        db.Orders.Add(Paid(1, 100.00m));                                     // Paid
        db.Orders.Add(Paid(2, 999.00m));                                     // Paid
        var pending = TestSupport.Order(id: 3, number: "ORD-2026-0003", status: OrderStatus.Pending);
        pending.GrandTotal = 5000m;
        db.Orders.Add(pending);
        var cancelled = TestSupport.Order(id: 4, number: "ORD-2026-0004", status: OrderStatus.Cancelled);
        cancelled.GrandTotal = 7000m;
        db.Orders.Add(cancelled);

        await db.SaveChangesAsync();

        var stats = await Orders(db).GetMarketingDashboardAsync();

        Assert.Equal(1099.00m, stats.TotalRevenue);
        Assert.Equal(2, stats.OrdersLast30Days);
        Assert.Equal(100.00m + 999.00m, stats.TotalRevenue);
    }

    [Fact]
    public async Task Average_order_value_divides_by_the_orders_that_were_paid()
    {
        await using var db = TestSupport.NewDb();

        db.Orders.Add(Paid(1, 100.00m));
        db.Orders.Add(Paid(2, 300.00m));
        await db.SaveChangesAsync();

        var stats = await Orders(db).GetMarketingDashboardAsync();

        Assert.Equal(200.00m, stats.AverageOrderValue);
    }

    [Fact]
    public async Task Cancellation_and_refund_rates_are_shares_of_all_orders()
    {
        await using var db = TestSupport.NewDb();

        db.Orders.Add(Paid(1, 10.00m));
        db.Orders.Add(Paid(2, 10.00m));
        db.Orders.Add(Paid(3, 10.00m));
        db.Orders.Add(Paid(4, 10.00m));
        db.Orders.Add(TestSupport.Order(id: 5, number: "ORD-2026-0005", status: OrderStatus.Cancelled));
        db.Orders.Add(TestSupport.Order(id: 6, number: "ORD-2026-0006", status: OrderStatus.Refunded));

        await db.SaveChangesAsync();

        var stats = await Orders(db).GetMarketingDashboardAsync();

        Assert.Equal(6, stats.TotalOrders);
        Assert.Equal(100m / 6m, stats.CancellationRate, 4);
        Assert.Equal(100m / 6m, stats.RefundRate, 4);
    }

    [Fact]
    public async Task Revenue_by_category_is_empty_rather_than_wrong_when_nothing_has_sold()
    {
        await using var db = TestSupport.NewDb();
        await db.SaveChangesAsync();

        var stats = await Orders(db).GetMarketingDashboardAsync();

        Assert.Empty(stats.RevenueByCategory);
        Assert.Empty(stats.TopProductsByRevenue);
        Assert.Equal(0m, stats.TotalRevenue);
        Assert.Equal(0m, stats.AverageOrderValue);
    }

    [Fact]
    public async Task A_new_customer_is_one_whose_only_order_is_recent()
    {
        await using var db = TestSupport.NewDb();

        var newShopper = TestSupport.User(email: "new@example.com");
        newShopper.Id = "user-new";
        var returning = TestSupport.User(email: "returning@example.com");
        returning.Id = "user-returning";
        db.Users.AddRange(newShopper, returning);

        var firstOrder = Paid(1, 50.00m);
        firstOrder.UserId = "user-new";
        firstOrder.Email = "new@example.com";

        var oldOrder = Paid(2, 60.00m);
        oldOrder.UserId = "user-returning";
        oldOrder.Email = "returning@example.com";
        oldOrder.PlacedUtc = DateTime.UtcNow.AddDays(-90);

        var recentSecond = Paid(3, 70.00m);
        recentSecond.UserId = "user-returning";
        recentSecond.Email = "returning@example.com";

        db.Orders.AddRange(firstOrder, oldOrder, recentSecond);
        await db.SaveChangesAsync();

        var stats = await Orders(db).GetMarketingDashboardAsync();

        Assert.Equal(1, stats.NewCustomersLast30Days);
        Assert.Equal(1, stats.ReturningCustomersLast30Days);
    }

    [Fact]
    public async Task Payment_methods_break_down_by_revenue_not_by_order_count()
    {
        await using var db = TestSupport.NewDb();

        var cod = Paid(1, 20.00m);
        cod.PaymentMethod = PaymentMethod.CashOnDelivery;

        var cardA = Paid(2, 30.00m);
        cardA.PaymentMethod = PaymentMethod.Card;

        var cardB = Paid(3, 50.00m);
        cardB.PaymentMethod = PaymentMethod.Card;

        db.Orders.AddRange(cod, cardA, cardB);
        await db.SaveChangesAsync();

        var stats = await Orders(db).GetMarketingDashboardAsync();

        var card = stats.PaymentMethods.Single(p => p.Method == PaymentMethod.Card);
        Assert.Equal(2, card.Orders);
        Assert.Equal(80.00m, card.RevenueRaw);

        var cash = stats.PaymentMethods.Single(p => p.Method == PaymentMethod.CashOnDelivery);
        Assert.Equal(20.00m, cash.RevenueRaw);
    }
}
