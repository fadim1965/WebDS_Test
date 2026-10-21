using Microsoft.EntityFrameworkCore;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services.Export;

namespace WebDS_Test.Services.Imports;

/// <summary>The outcome of one import attempt, row by row.</summary>
public sealed class ProductImportResult
{
    public bool Success => Errors.Count == 0 && Created + Updated > 0;
    public int Created { get; init; }
    public int Updated { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public string Summary => Success
        ? $"Imported {Created + Updated} product(s): {Created} created, {Updated} updated."
        : $"Nothing was imported. {Errors.Count} problem(s) found.";
}

public interface IProductImportService
{
    /// <summary>
    /// Validates every row before writing anything. An import is all-or-nothing on purpose: a bulk
    /// load that half-succeeds leaves a catalogue nobody can reason about.
    /// </summary>
    Task<ProductImportResult> ImportAsync(string csv, int shopId, bool createMissing, CancellationToken ct = default);

    /// <summary>The exact header line the importer expects, for a downloadable template.</summary>
    string TemplateHeader { get; }
}

public sealed class ProductImportService : IProductImportService
{
    private readonly ApplicationDbContext _db;
    private readonly ISlugService _slugs;
    private readonly ILogger<ProductImportService> _logger;

    public ProductImportService(ApplicationDbContext db, ISlugService slugs, ILogger<ProductImportService> logger)
    {
        _db = db;
        _slugs = slugs;
        _logger = logger;
    }

    public string TemplateHeader =>
        "Name,Slug,Category,Brand,ShortDescription,Description,SKU,VariantName,Price,CompareAtPrice,StockQuantity,IsPublished,IsFeatured,Images";

    public async Task<ProductImportResult> ImportAsync(
        string csv, int shopId, bool createMissing, CancellationToken ct = default)
    {
        var errors = new List<string>();

        IReadOnlyList<CsvRow> all;
        try
        {
            all = CsvReader.ReadRows(csv);
        }
        catch (ArgumentException ex)
        {
            return new ProductImportResult { Errors = new[] { ex.Message } };
        }

        if (all.Count == 0)
        {
            return new ProductImportResult { Errors = new[] { "The file is empty." } };
        }

        var map = CsvReader.MapHeaders(all[0].Fields);

        // Skip blank trailing lines, which spreadsheets leave behind more often than you would think.
        var dataRows = all.Skip(1).Where(r => r.Fields.Any(f => !string.IsNullOrWhiteSpace(f))).ToList();

        if (dataRows.Count == 0)
        {
            return new ProductImportResult { Errors = new[] { "The file has a header but no product rows." } };
        }

        foreach (var required in new[] { "Name", "Price", "SKU" })
        {
            if (!map.ContainsKey(required))
            {
                errors.Add($"Missing required column '{required}'.");
            }
        }

        if (errors.Count > 0)
        {
            return new ProductImportResult { Errors = errors };
        }

        // ---- Validate everything first ------------------------------------
        var shopExists = await _db.Shops.AsNoTracking().AnyAsync(s => s.Id == shopId, ct);
        if (!shopExists)
        {
            return new ProductImportResult { Errors = new[] { $"Shop {shopId} does not exist." } };
        }

        var categories = await _db.Categories.AsNoTracking().ToListAsync(ct);
        var categoriesByName = categories.ToDictionary(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase);

        var existing = await _db.Products.AsNoTracking()
            .Include(p => p.Variants)
            .Where(p => p.ShopId == shopId)
            .ToListAsync(ct);

        var existingBySlug = existing
            .Where(p => !string.IsNullOrWhiteSpace(p.Slug))
            .GroupBy(p => p.Slug, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var seenSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<Pending>();

        foreach (var row in dataRows)
        {
            string Field(string name) =>
                map.TryGetValue(name, out var index) && index < row.Fields.Count ? row.Fields[index].Trim() : string.Empty;

            var line = row.LineNumber;

            var name = Field("Name");
            if (name.Length == 0)
            {
                errors.Add($"Line {line}: Name is required.");
                continue;
            }

            var sku = Field("SKU");
            if (sku.Length == 0)
            {
                errors.Add($"Line {line}: SKU is required.");
                continue;
            }

            if (!seenSkus.Add(sku))
            {
                errors.Add($"Line {line}: SKU '{sku}' appears more than once in the file.");
                continue;
            }

            if (!decimal.TryParse(Field("Price"), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var price))
            {
                errors.Add($"Line {line}: Price '{Field("Price")}' is not a number.");
                continue;
            }

            if (price < 0m)
            {
                errors.Add($"Line {line}: Price cannot be negative.");
                continue;
            }

            var compareAtRaw = Field("CompareAtPrice");
            decimal? compareAt = null;
            if (compareAtRaw.Length > 0)
            {
                if (!decimal.TryParse(compareAtRaw, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                {
                    errors.Add($"Line {line}: CompareAtPrice '{compareAtRaw}' is not a number.");
                    continue;
                }

                compareAt = parsed;
            }

            var stockRaw = Field("StockQuantity");
            var stock = 0;
            if (stockRaw.Length > 0 && !int.TryParse(stockRaw, out stock))
            {
                errors.Add($"Line {line}: StockQuantity '{stockRaw}' is not a whole number.");
                continue;
            }

            var categoryName = Field("Category");
            Category? category = null;
            if (categoryName.Length > 0 && !categoriesByName.TryGetValue(categoryName, out category))
            {
                errors.Add($"Line {line}: Category '{categoryName}' does not exist. Create it first, or leave the column empty.");
                continue;
            }

            // Match on SKU first: that is the stable business key. Fall back to slug, then name.
            var product = await _db.Products.AsNoTracking()
                .Include(p => p.Variants)
                .FirstOrDefaultAsync(p => p.ShopId == shopId && p.Variants.Any(v => v.Sku == sku), ct);

            if (product is null && Field("Slug").Length > 0)
            {
                existingBySlug.TryGetValue(Field("Slug"), out product);
            }

            if (product is null)
            {
                product = existing.FirstOrDefault(p =>
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            }

            if (product is null && !createMissing)
            {
                errors.Add($"Line {line}: '{name}' (SKU {sku}) is not in the catalogue and creating new products is switched off.");
                continue;
            }

            var slug = product?.Slug ?? SlugifyUnique(Field("Slug").Length > 0 ? Field("Slug") : name, seenSlugs);
            seenSlugs.Add(slug);

            pending.Add(new Pending
            {
                Line = line,
                Name = name,
                Slug = slug,
                Brand = Null(Field("Brand")),
                ShortDescription = Null(Field("ShortDescription")),
                Description = Null(Field("Description")),
                Images = SplitImages(Field("Images")),
                IsPublished = ParseBool(Field("IsPublished"), true),
                IsFeatured = ParseBool(Field("IsFeatured"), false),
                Category = category,
                Sku = sku,
                VariantName = Field("VariantName").Length > 0 ? Field("VariantName") : "Default",
                Price = price,
                CompareAtPrice = compareAt,
                StockQuantity = stock,
                ExistingProductId = product?.Id
            });
        }

        if (errors.Count > 0)
        {
            _logger.LogWarning("Product import rejected: {Count} problem(s), nothing written", errors.Count);
            return new ProductImportResult { Errors = errors.Take(50).ToList() };
        }

        // ---- Apply ----------------------------------------------------------
        var created = 0;
        var updated = 0;

        foreach (var row in pending)
        {
            Product? product = row.ExistingProductId is { } id
                ? await _db.Products.Include(p => p.Variants).FirstOrDefaultAsync(p => p.Id == id, ct)
                : null;

            if (product is null)
            {
                product = new Product
                {
                    ShopId = shopId,
                    CreatedUtc = DateTime.UtcNow
                };
                _db.Products.Add(product);
                created++;
            }
            else
            {
                updated++;
            }

            product.Name = row.Name;
            product.Slug = row.Slug;
            product.Brand = row.Brand;
            product.ShortDescription = row.ShortDescription;
            product.Description = row.Description;
            product.IsPublished = row.IsPublished;
            product.IsFeatured = row.IsFeatured;
            product.UpdatedUtc = DateTime.UtcNow;

            if (row.Category is not null)
            {
                product.CategoryId = row.Category.Id;
            }

            var variant = product.Variants.FirstOrDefault(v =>
                string.Equals(v.Sku, row.Sku, StringComparison.OrdinalIgnoreCase));

            if (variant is null)
            {
                product.Variants.Add(new ProductVariant
                {
                    Sku = row.Sku,
                    Name = row.VariantName,
                    Price = row.Price,
                    CompareAtPrice = row.CompareAtPrice,
                    StockQuantity = row.StockQuantity,
                    IsActive = true
                });
            }
            else
            {
                variant.Name = row.VariantName;
                variant.Price = row.Price;
                variant.CompareAtPrice = row.CompareAtPrice;
                variant.StockQuantity = row.StockQuantity;
            }

            // Images: replace the gallery only when the file actually supplied some, so an
            // import that omits the column cannot silently wipe a product's photographs.
            if (row.Images.Count > 0)
            {
                product.Images.Clear();

                for (var i = 0; i < row.Images.Count; i++)
                {
                    product.Images.Add(new ProductImage
                    {
                        Url = row.Images[i],
                        DisplayOrder = i,
                        IsPrimary = i == 0
                    });
                }
            }
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Product import for shop {ShopId}: {Created} created, {Updated} updated", shopId, created, updated);

        return new ProductImportResult { Created = created, Updated = updated };
    }

    private sealed class Pending
    {
        public int Line { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Slug { get; init; } = string.Empty;
        public string? Brand { get; init; }
        public string? ShortDescription { get; init; }
        public string? Description { get; init; }
        public List<string> Images { get; init; } = new();
        public bool IsPublished { get; init; }
        public bool IsFeatured { get; init; }
        public Category? Category { get; init; }
        public string Sku { get; init; } = string.Empty;
        public string VariantName { get; init; } = string.Empty;
        public decimal Price { get; init; }
        public decimal? CompareAtPrice { get; init; }
        public int StockQuantity { get; init; }
        public int? ExistingProductId { get; init; }
    }

    private string SlugifyUnique(string source, HashSet<string> taken)
    {
        var candidate = _slugs.Slugify(source);
        if (string.IsNullOrEmpty(candidate))
        {
            candidate = $"product-{Guid.NewGuid():N}"[..16];
        }

        var slug = candidate;
        var suffix = 2;
        while (taken.Contains(slug))
        {
            slug = $"{candidate}-{suffix++}";
        }

        return slug;
    }

    private static List<string> SplitImages(string raw) =>
        raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool ParseBool(string raw, bool fallback) =>
        raw.Length == 0
            ? fallback
            : raw.Equals("true", StringComparison.OrdinalIgnoreCase)
              || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
              || raw == "1";

    private static string? Null(string value) => value.Length == 0 ? null : value;
}
