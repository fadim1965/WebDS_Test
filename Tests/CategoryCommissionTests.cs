using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services;
using WebDS_Test.Services.Email;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// Per-category commission: a category can override the shop's default commission rate.
/// The ledger calculates commission per order line using the product's category rate.
/// </summary>
public class CategoryCommissionTests
{
    private static ApplicationDbContext NewDb() => TestSupport.NewDb();

    private static async Task<ApplicationDbContext> SeededAsync()
    {
        var db = NewDb();

        var owner = TestSupport.User(email: "vendor@example.com", displayName: "Vera Vendor");
        db.Users.Add(owner);

        db.Shops.Add(new Shop
        {
            Id = 1,
            Name = "Electronics Plus",
            Slug = "electronics-plus",
            OwnerUserId = owner.Id,
            Email = "vendor@example.com",
            CommissionRate = 0.10m,
            Status = ShopStatus.Approved
        });

        db.Categories.AddRange(
            new Category { Id = 1, Name = "Phones", Slug = "phones", IsGlobal = true, CommissionRate = 0.05m },
            new Category { Id = 2, Name = "Accessories", Slug = "accessories", IsGlobal = true, CommissionRate = 0.15m },
            new Category { Id = 3, Name = "Default Rate", Slug = "default-rate", IsGlobal = true }
        );

        db.Products.AddRange(
            new Product { Id = 1, ShopId = 1, CategoryId = 1, Name = "Phone", Slug = "phone", IsPublished = true },
            new Product { Id = 2, ShopId = 1, CategoryId = 2, Name = "Case", Slug = "case", IsPublished = true },
            new Product { Id = 3, ShopId = 1, CategoryId = 3, Name = "Generic", Slug = "generic", IsPublished = true }
        );

        db.ProductVariants.AddRange(
            new ProductVariant { Id = 1, ProductId = 1, Sku = "PHONE-1", Name = "Phone", Price = 100m, StockQuantity = 5, IsActive = true },
            new ProductVariant { Id = 2, ProductId = 2, Sku = "CASE-1", Name = "Case", Price = 20m, StockQuantity = 10, IsActive = true },
            new ProductVariant { Id = 3, ProductId = 3, Sku = "GEN-1", Name = "Generic", Price = 50m, StockQuantity = 3, IsActive = true }
        );

        await db.SaveChangesAsync();
        return db;
    }

    private static VendorLedgerService Ledger(ApplicationDbContext db) => new(
        db, NullLogger<VendorLedgerService>.Instance);

    private static Order MakeOrder(int id, string number, decimal subtotal, params (int variantId, int qty, decimal price)[] lines)
    {
        var order = TestSupport.Order(id: id, number: number);
        order.ShopId = 1;
        order.Lines.Clear();
        foreach (var (variantId, qty, price) in lines)
        {
            order.Lines.Add(new OrderLine
            {
                ProductVariantId = variantId,
                Quantity = qty,
                UnitPrice = price,
                LineTotal = price * qty,
                ProductName = "Product",
                Sku = "SKU",
                VariantName = "Variant"
            });
        }
        order.Subtotal = subtotal;
        return order;
    }

    [Fact]
    public async Task Category_commission_overrides_shop_rate()
    {
        await using var db = await SeededAsync();

        var order = MakeOrder(1, "ORD-1", 120m,
            (1, 1, 100m), // Phone
            (2, 1, 20m)   // Case
        );
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        await Ledger(db).RecordSaleAsync(order, "test");

        var entry = await db.VendorLedgerEntries.SingleAsync();
        // Phone: 100 * 5% = 5.00, Case: 20 * 15% = 3.00, Total commission = 8.00
        Assert.Equal(120m, entry.GrossAmount);
        Assert.Equal(8.00m, entry.CommissionAmount);
        Assert.Equal(112.00m, entry.NetAmount);
    }

    [Fact]
    public async Task Product_without_category_rate_uses_shop_rate()
    {
        await using var db = await SeededAsync();

        var order = MakeOrder(2, "ORD-2", 50m,
            (3, 1, 50m)   // Generic
        );
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        await Ledger(db).RecordSaleAsync(order, "test");

        var entry = await db.VendorLedgerEntries.SingleAsync();
        // Generic has no category rate, uses shop 10%: 50 * 10% = 5.00
        Assert.Equal(50m, entry.GrossAmount);
        Assert.Equal(5.00m, entry.CommissionAmount);
        Assert.Equal(45.00m, entry.NetAmount);
    }

    [Fact]
    public async Task Mixed_order_applies_correct_rate_per_line()
    {
        await using var db = await SeededAsync();

        var order = MakeOrder(3, "ORD-3", 170m,
            (1, 1, 100m), // Phone
            (2, 1, 20m),  // Case
            (3, 1, 50m)   // Generic
        );
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        await Ledger(db).RecordSaleAsync(order, "test");

        var entry = await db.VendorLedgerEntries.SingleAsync();
        // Phone 5% = 5.00, Case 15% = 3.00, Generic 10% = 5.00, Total = 13.00
        Assert.Equal(170m, entry.GrossAmount);
        Assert.Equal(13.00m, entry.CommissionAmount);
        Assert.Equal(157.00m, entry.NetAmount);
    }

    [Fact]
    public async Task Category_commission_is_clamped_to_valid_range()
    {
        await using var db = NewDb();

        var owner = TestSupport.User();
        db.Users.Add(owner);

        db.Shops.Add(new Shop { Id = 1, Name = "Test", Slug = "test", OwnerUserId = owner.Id, CommissionRate = 0.10m, Status = ShopStatus.Approved });

        // Category with invalid rate (negative) should be clamped to 0
        db.Categories.Add(new Category { Id = 1, Name = "Zero Rate", Slug = "zero-rate", IsGlobal = true, CommissionRate = -0.05m });
        // Category with rate > 1 should be clamped to 1
        db.Categories.Add(new Category { Id = 2, Name = "Max Rate", Slug = "max-rate", IsGlobal = true, CommissionRate = 1.5m });

        db.Products.AddRange(
            new Product { Id = 1, ShopId = 1, CategoryId = 1, Name = "Zero", Slug = "zero", IsPublished = true },
            new Product { Id = 2, ShopId = 1, CategoryId = 2, Name = "Max", Slug = "max", IsPublished = true }
        );

        db.ProductVariants.AddRange(
            new ProductVariant { Id = 1, ProductId = 1, Sku = "ZERO-1", Name = "Zero", Price = 100m, StockQuantity = 1, IsActive = true },
            new ProductVariant { Id = 2, ProductId = 2, Sku = "MAX-1", Name = "Max", Price = 100m, StockQuantity = 1, IsActive = true }
        );

        await db.SaveChangesAsync();

        var order = MakeOrder(1, "ORD-1", 200m,
            (1, 1, 100m), // Zero
            (2, 1, 100m)  // Max
        );
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        await Ledger(db).RecordSaleAsync(order, "test");

        var entry = await db.VendorLedgerEntries.SingleAsync();
        // Zero rate (clamped from -0.05) = 0% on 100 = 0.00
        // Max rate (clamped from 1.5) = 100% on 100 = 100.00
        // Total commission = 100.00
        Assert.Equal(200m, entry.GrossAmount);
        Assert.Equal(100.00m, entry.CommissionAmount);
        Assert.Equal(100.00m, entry.NetAmount);
    }

    [Fact]
    public async Task Order_without_lines_falls_back_to_subtotal()
    {
        await using var db = await SeededAsync();

        var order = MakeOrder(99, "ORD-99", 100m);
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        await Ledger(db).RecordSaleAsync(order, "test");

        var entry = await db.VendorLedgerEntries.SingleAsync();
        // Falls back to shop rate 10% on subtotal 100
        Assert.Equal(100m, entry.GrossAmount);
        Assert.Equal(10.00m, entry.CommissionAmount);
        Assert.Equal(90.00m, entry.NetAmount);
    }

    [Fact]
    public async Task Lines_gross_mismatch_falls_back_to_shop_rate()
    {
        await using var db = await SeededAsync();

        var order = MakeOrder(4, "ORD-4", 200m,
            (1, 1, 100m), // Phone
            (2, 1, 20m)   // Case
        );
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        await Ledger(db).RecordSaleAsync(order, "test");

        var entry = await db.VendorLedgerEntries.SingleAsync();
        // Lines gross (120) != Subtotal (200) → fallback to shop rate 10% on 200 = 20.00
        Assert.Equal(200m, entry.GrossAmount);
        Assert.Equal(20.00m, entry.CommissionAmount);
        Assert.Equal(180.00m, entry.NetAmount);
    }
}