using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services;
using WebDS_Test.Services.Export;
using WebDS_Test.Services.Imports;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// The CSV reader and the bulk importer. The reader is written to be the exact inverse of the export
/// writer, so the strongest thing it can be tested against is a round trip: export a report, feed the
/// bytes back, and get the same numbers.
/// </summary>
public class CsvReaderTests
{
    [Fact]
    public void Reads_plain_rows()
    {
        var rows = CsvReader.ReadRows("a,b,c\r\n1,2,3\r\n");

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "a", "b", "c" }, rows[0].Fields);
        Assert.Equal(new[] { "1", "2", "3" }, rows[1].Fields);
    }

    [Fact]
    public void Quoted_fields_keep_their_commas()
    {
        var rows = CsvReader.ReadRows("name,desc\r\n\"Widget, large\",\"A \"\"great\"\" widget\"\r\n");

        Assert.Equal(2, rows.Count);
        Assert.Equal("Widget, large", rows[1].Fields[0]);
        Assert.Equal("A \"great\" widget", rows[1].Fields[1]);
    }

    [Fact]
    public void A_newline_inside_quotes_is_part_of_the_value()
    {
        var rows = CsvReader.ReadRows("a,b\r\n\"line one\nline two\",x\r\n");

        Assert.Equal(2, rows.Count);
        Assert.Equal("line one\nline two", rows[1].Fields[0]);
    }

    [Fact]
    public void A_utf8_bom_does_not_glue_itself_to_the_first_header()
    {
        // Excel always writes a BOM. Without this the first column is named "﻿Name" and never matches.
        var rows = CsvReader.ReadRows("\uFEFFName,Price\r\nWidget,9.99\r\n");

        Assert.Equal("Name", rows[0].Fields[0]);
        Assert.Equal("Widget", rows[1].Fields[0]);
    }

    [Fact]
    public void A_trailing_newline_does_not_create_a_phantom_row()
    {
        var rows = CsvReader.ReadRows("a,b\r\n1,2\r\n");

        // Header plus one data row, and nothing for the trailing newline.
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Handles_bare_line_feeds_from_unix_tools()
    {
        var rows = CsvReader.ReadRows("a,b\n1,2\n3,4\n");

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "3", "4" }, rows[2].Fields);
    }

    [Fact]
    public void An_unclosed_quote_is_reported_rather_than_swallowed()
    {
        // Silently returning the rest of the file as one value would import garbage as data.
        var ex = Assert.Throws<ArgumentException>(() => CsvReader.ReadRows("a,b\r\n\"never closed,2\r\n"));
        Assert.Contains("quoted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Header_matching_is_case_insensitive_and_order_independent()
    {
        var map = CsvReader.MapHeaders(new[] { "SKU", "name", "Price" });

        Assert.Equal(0, map["sku"]);
        Assert.Equal(1, map["Name"]);
        Assert.Equal(2, map["PRICE"]);
    }

    [Fact]
    public void Round_trips_through_the_export_writer()
    {
        // The point of a hand-written reader: what the store exports must come back unchanged.
        var original = new TabularData(
            "Products",
            "products",
            new[] { "Name", "Note" },
            new List<object?[]>
            {
                new object?[] { "Widget, large", "He said \"buy two\"" },
                new object?[] { "=SUM(A1:A9)", "line one\nline two" },
                new object?[] { "Cédric's box", "plain" }
            });

        var bytes = ExportBuilder.ToCsv(original);
        var rows = CsvReader.ReadRows(System.Text.Encoding.UTF8.GetString(bytes));

        // Row 0 is the header the writer emitted.
        Assert.Equal(4, rows.Count);
        Assert.Equal("Widget, large", rows[1].Fields[0]);
        Assert.Equal("He said \"buy two\"", rows[1].Fields[1]);
        Assert.Equal("line one\nline two", rows[2].Fields[1]);
        Assert.Equal("Cédric's box", rows[3].Fields[0]);
    }
}

public class ProductImportTests
{
    private static ProductImportService Importer(ApplicationDbContext db)
    {
        var slugs = new Mock<ISlugService>();
        slugs.Setup(s => s.Slugify(It.IsAny<string>()))
            .Returns((string value) => new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()));

        return new ProductImportService(db, slugs.Object, NullLogger<ProductImportService>.Instance);
    }

    private static void SeedShop(ApplicationDbContext db, int shopId = 1)
    {
        db.Shops.Add(new Shop
        {
            Id = shopId,
            Name = $"Shop {shopId}",
            Slug = $"shop-{shopId}",
            OwnerUserId = "owner",
            CommissionRate = 0.10m,
            Status = ShopStatus.Approved
        });
    }

    private const string Header = "Name,SKU,Price,StockQuantity";

    [Fact]
    public async Task Creates_a_product_with_its_variant()
    {
        await using var db = TestSupport.NewDb();
        SeedShop(db);
        await db.SaveChangesAsync();

        var result = await Importer(db).ImportAsync(
            $"{Header}\r\nRegistered Mail Kit,REG-1,18.75,40\r\n", 1, createMissing: true);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(1, result.Created);

        var product = await db.Products.Include(p => p.Variants).SingleAsync();
        Assert.Equal("Registered Mail Kit", product.Name);
        Assert.Equal(18.75m, product.Variants.Single().Price);
        Assert.Equal(40, product.Variants.Single().StockQuantity);
    }

    [Fact]
    public async Task Matches_an_existing_product_by_sku_and_updates_it()
    {
        await using var db = TestSupport.NewDb();
        SeedShop(db);

        var existing = new Product
        {
            Id = 1,
            ShopId = 1,
            Name = "Old name",
            Slug = "old-name",
            CreatedUtc = DateTime.UtcNow
        };
        existing.Variants.Add(new ProductVariant
        {
            Id = 1,
            ProductId = 1,
            Sku = "REG-1",
            Price = 10.00m,
            StockQuantity = 5,
            IsActive = true
        });
        db.Products.Add(existing);
        await db.SaveChangesAsync();

        var result = await Importer(db).ImportAsync(
            $"{Header}\r\nCorrected name,REG-1,22.50,9\r\n", 1, createMissing: true);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Created);

        Assert.Single(await db.Products.ToListAsync());
        var product = await db.Products.Include(p => p.Variants).SingleAsync();
        Assert.Equal("Corrected name", product.Name);
        Assert.Equal(22.50m, product.Variants.Single().Price);
    }

    [Fact]
    public async Task One_bad_row_rejects_the_whole_file()
    {
        // The whole point: a bulk load that half-succeeds leaves a catalogue nobody can reason about.
        await using var db = TestSupport.NewDb();
        SeedShop(db);
        await db.SaveChangesAsync();

        var result = await Importer(db).ImportAsync(
            $"{Header}\r\nGood one,SKU-A,10.00,5\r\nBad one,SKU-B,not-a-price,5\r\n", 1, createMissing: true);

        Assert.False(result.Success);
        Assert.Empty(await db.Products.ToListAsync());
        Assert.Contains(result.Errors, e => e.Contains("not a number"));
    }

    [Fact]
    public async Task Refuses_to_create_new_products_when_asked_not_to()
    {
        await using var db = TestSupport.NewDb();
        SeedShop(db);
        await db.SaveChangesAsync();

        var result = await Importer(db).ImportAsync($"{Header}\r\nBrand New,SKU-N,10.00,5\r\n", 1, createMissing: false);

        Assert.False(result.Success);
        Assert.Empty(await db.Products.ToListAsync());
        Assert.Contains(result.Errors, e => e.Contains("switched off"));
    }

    [Fact]
    public async Task Rejects_a_missing_required_column()
    {
        await using var db = TestSupport.NewDb();
        SeedShop(db);
        await db.SaveChangesAsync();

        var result = await Importer(db).ImportAsync("Name,SKU\r\nWidget,SKU-1\r\n", 1, createMissing: true);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("Price"));
    }

    [Fact]
    public async Task Rejects_a_duplicate_sku_within_the_file()
    {
        await using var db = TestSupport.NewDb();
        SeedShop(db);
        await db.SaveChangesAsync();

        var result = await Importer(db).ImportAsync(
            $"{Header}\r\nOne,DUP,10.00,1\r\nTwo,DUP,12.00,2\r\n", 1, createMissing: true);

        Assert.False(result.Success);
        Assert.Empty(await db.Products.ToListAsync());
        Assert.Contains(result.Errors, e => e.Contains("more than once"));
    }

    [Fact]
    public async Task Rejects_a_category_that_does_not_exist()
    {
        await using var db = TestSupport.NewDb();
        SeedShop(db);
        db.Categories.Add(new Category { Id = 1, Name = "Packaging", Slug = "packaging" });
        await db.SaveChangesAsync();

        var result = await Importer(db).ImportAsync(
            "Name,SKU,Price,Category\r\nWidget,SKU-1,10.00,Nonexistent\r\n", 1, createMissing: true);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("Nonexistent"));
    }

    [Fact]
    public async Task Column_order_does_not_matter()
    {
        await using var db = TestSupport.NewDb();
        SeedShop(db);
        await db.SaveChangesAsync();

        var result = await Importer(db).ImportAsync(
            "Price,Name,SKU\r\n9.99,Widget,SKU-1\r\n", 1, createMissing: true);

        Assert.True(result.Success, result.Summary);
        var product = await db.Products.Include(p => p.Variants).SingleAsync();
        Assert.Equal("Widget", product.Name);
        Assert.Equal(9.99m, product.Variants.Single().Price);
    }

    [Fact]
    public async Task Parses_a_decimal_point_even_under_a_comma_decimal_locale()
    {
        await using var db = TestSupport.NewDb();
        SeedShop(db);
        await db.SaveChangesAsync();

        var result = await Importer(db).ImportAsync($"{Header}\r\nWidget,SKU-1,1234.56,5\r\n", 1, createMissing: true);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(1234.56m, (await db.Products.Include(p => p.Variants).SingleAsync()).Variants.Single().Price);
    }

    [Fact]
    public async Task Omitting_the_images_column_leaves_the_existing_gallery_alone()
    {
        // An import that silently wiped product photographs because the operator did not know about a
        // column would be a genuinely nasty surprise.
        await using var db = TestSupport.NewDb();
        SeedShop(db);

        var existing = new Product { Id = 1, ShopId = 1, Name = "Widget", Slug = "widget", CreatedUtc = DateTime.UtcNow };
        existing.Images.Add(new ProductImage { Url = "https://example.com/keep.jpg", IsPrimary = true });
        existing.Variants.Add(new ProductVariant { Id = 1, ProductId = 1, Sku = "SKU-1", Price = 5m, StockQuantity = 1, IsActive = true });
        db.Products.Add(existing);
        await db.SaveChangesAsync();

        var result = await Importer(db).ImportAsync($"{Header}\r\nWidget,SKU-1,7.50,3\r\n", 1, createMissing: true);

        Assert.True(result.Success, result.Summary);
        var product = await db.Products.Include(p => p.Images).SingleAsync();
        Assert.Equal("https://example.com/keep.jpg", Assert.Single(product.Images).Url);
    }

    [Fact]
    public async Task Supplying_images_replaces_the_gallery_with_the_first_as_primary()
    {
        await using var db = TestSupport.NewDb();
        SeedShop(db);

        var existing = new Product { Id = 1, ShopId = 1, Name = "Widget", Slug = "widget", CreatedUtc = DateTime.UtcNow };
        existing.Images.Add(new ProductImage { Url = "https://example.com/old.jpg", IsPrimary = true });
        existing.Variants.Add(new ProductVariant { Id = 1, ProductId = 1, Sku = "SKU-1", Price = 5m, StockQuantity = 1, IsActive = true });
        db.Products.Add(existing);
        await db.SaveChangesAsync();

        var csv = $"{Header},Images\r\nWidget,SKU-1,7.50,3,https://example.com/a.jpg;https://example.com/b.jpg\r\n";
        var result = await Importer(db).ImportAsync(csv, 1, createMissing: true);

        Assert.True(result.Success, result.Summary);
        var product = await db.Products.Include(p => p.Images).SingleAsync();
        Assert.Equal(2, product.Images.Count);
        Assert.True(product.Images.OrderBy(i => i.DisplayOrder).First().IsPrimary);
        Assert.Equal("https://example.com/a.jpg", product.Images.OrderBy(i => i.DisplayOrder).First().Url);
    }

    [Fact]
    public async Task Two_products_in_one_file_both_import()
    {
        await using var db = TestSupport.NewDb();
        SeedShop(db);
        await db.SaveChangesAsync();

        var csv =
            $"{Header}\r\nFirst,SKU-1,10.00,5\r\nSecond,SKU-2,20.00,6\r\nThird,SKU-3,30.00,7\r\n";
        var result = await Importer(db).ImportAsync(csv, 1, createMissing: true);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(3, result.Created);
        Assert.Equal(3, await db.Products.CountAsync());
    }

    [Fact]
    public async Task Reports_the_line_number_of_a_problem_row()
    {
        await using var db = TestSupport.NewDb();
        SeedShop(db);
        await db.SaveChangesAsync();

        // Line 1 is the header, so the bad row is line 3.
        var csv = $"{Header}\r\nGood,SKU-1,10.00,1\r\nBad,,20.00,1\r\n";
        var result = await Importer(db).ImportAsync(csv, 1, createMissing: true);

        Assert.Contains(result.Errors, e => e.StartsWith("Line 3"));
    }

    [Fact]
    public async Task An_empty_file_is_rejected_rather_than_silently_doing_nothing()
    {
        await using var db = TestSupport.NewDb();
        SeedShop(db);
        await db.SaveChangesAsync();

        var result = await Importer(db).ImportAsync("", 1, createMissing: true);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task A_header_only_file_is_rejected()
    {
        await using var db = TestSupport.NewDb();
        SeedShop(db);
        await db.SaveChangesAsync();

        var result = await Importer(db).ImportAsync($"{Header}\r\n", 1, createMissing: true);

        Assert.False(result.Success);
    }
}
