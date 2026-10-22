using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebDS_Test.Areas.Admin.Models;
using WebDS_Test.Domain;
using WebDS_Test.Services.Images;

namespace WebDS_Test.Areas.Vendor.Controllers;

/// <summary>Product, variant and image administration.</summary>
[Area("Vendor")]
[Authorize(Policy = "VendorOnly")]
public class ProductsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly ISlugService _slugs;
    private readonly IShopService _shops;
    private readonly IImageStore _images;
    private readonly ImageStorageOptions _imageOptions;
    private readonly IListingModerationService _moderation;
    private readonly IAuditTrail _audit;
    private readonly ILogger<ProductsController> _logger;
    protected StoreSettings Settings { get; }

    public ProductsController(
        ApplicationDbContext db,
        IOptions<StoreSettings> settings,
        ISlugService slugs,
        IShopService shops,
        IImageStore images,
        IOptions<ImageStorageOptions> imageOptions,
        IListingModerationService moderation,
        IAuditTrail audit,
        ILogger<ProductsController> logger)
    {
        _db = db;
        Settings = settings.Value;
        _slugs = slugs;
        _shops = shops;
        _images = images;
        _imageOptions = imageOptions.Value;
        _moderation = moderation;
        _audit = audit;
        _logger = logger;
    }

    protected ApplicationDbContext Db => _db;

    protected void Flash(string message, bool success = true)
    {
        TempData["Message"] = message;
        TempData["MessageType"] = success ? "success" : "danger";
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? q, int? categoryId, string? state, CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });

        // Admin listings see soft-deleted rows too.
        var query = Db.Products.AsNoTracking()
            .Include(p => p.Category)
            .Include(p => p.Variants)
            .AsQueryable();

        query = query.Where(p => p.ShopId == shop.Id);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = $"%{q.Trim().Replace("%", "\\%").Replace("_", "\\_")}%";
            query = query.Where(p => EF.Functions.Like(p.Name, term, "\\") ||
                                     p.Variants.Any(v => EF.Functions.Like(v.Sku, term, "\\")));
        }

        if (categoryId is { } cid && cid > 0)
        {
            query = query.Where(p => p.CategoryId == cid);
        }

        query = state switch
        {
            "draft" => query.Where(p => !p.IsPublished && !p.IsDeleted),
            "pending" => query.Where(p => p.ModerationStatus == ListingStatus.PendingReview && !p.IsDeleted),
            "rejected" => query.Where(p => p.ModerationStatus == ListingStatus.Rejected && !p.IsDeleted),
            "deleted" => query.Where(p => p.IsDeleted),
            "outofstock" => query.Where(p => !p.IsDeleted && p.Variants.All(v => !v.IsActive || v.StockQuantity <= 0)),
            _ => query.Where(p => !p.IsDeleted)
        };

        var rows = await query
            .OrderByDescending(p => p.UpdatedUtc)
            .Select(p => new AdminProductRow
            {
                Id = p.Id,
                Name = p.Name,
                Slug = p.Slug,
                CategoryName = p.Category != null ? p.Category.Name : "Uncategorised",
                VariantCount = p.Variants.Count,
                TotalStock = p.Variants.Where(v => v.IsActive).Sum(v => (int?)v.StockQuantity) ?? 0,
                // Cast to double inside the projection: SQLite cannot aggregate a decimal column
                // (see CatalogService for the same workaround), and these values are only shown,
                // never used for arithmetic.
                MinPrice = p.Variants.Any() ? (decimal)p.Variants.Min(v => (double)v.Price) : 0m,
                MaxPrice = p.Variants.Any() ? (decimal)p.Variants.Max(v => (double)v.Price) : 0m,
                IsPublished = p.IsPublished,
                ModerationStatus = p.ModerationStatus,
                ModerationReason = p.ModerationReason,
                IsDeleted = p.IsDeleted,
                IsFeatured = p.IsFeatured,
                UpdatedUtc = p.UpdatedUtc
            })
            .ToListAsync(ct);

        ViewData["Title"] = "Products";
        return View(new AdminProductListViewModel
        {
            Rows = rows,
            Search = q,
            CategoryId = categoryId,
            State = state,
            Categories = await Db.Categories.AsNoTracking().Where(c => c.IsGlobal || c.ShopId == shop.Id).OrderBy(c => c.Name).ToListAsync(ct)
        });
    }

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });

        var model = new ProductEditViewModel
        {
            Categories = await Db.Categories.AsNoTracking().Where(c => c.IsGlobal || c.ShopId == shop.Id).OrderBy(c => c.Name).ToListAsync(ct),
            Variants = { new VariantInput { Sku = string.Empty, Name = string.Empty, StockQuantity = 0 } }
        };

        // Default to the first category so the form validates on first submit.
        if (model.Categories.Count > 0)
        {
            model.CategoryId = model.Categories[0].Id;
        }

        ViewData["Title"] = "New product";
        return View("Edit", model);
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });

        var accessCheck = await _shops.ResolveAccessAsync(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value, shop.Id, ct);
        if (accessCheck?.CanManageCatalogue != true) return Forbid();

        var product = await Db.Products
            .Include(p => p.Variants)
            .Include(p => p.Images)
            .FirstOrDefaultAsync(p => p.Id == id && p.ShopId == shop.Id, ct);

        if (product is null)
        {
            return NotFound();
        }

        ViewData["Title"] = $"Edit — {product.Name}";

        return View(new ProductEditViewModel
        {
            Id = product.Id,
            Name = product.Name,
            Slug = product.Slug,
            ShortDescription = product.ShortDescription,
            Description = product.Description,
            Brand = product.Brand,
            CategoryId = product.CategoryId,
            IsPublished = product.IsPublished,
            ModerationStatus = product.ModerationStatus,
            ModerationReason = product.ModerationReason,
            IsFeatured = product.IsFeatured,
            ImageUrls = string.Join(Environment.NewLine, product.Images.OrderByDescending(i => i.IsPrimary).ThenBy(i => i.DisplayOrder).Select(i => i.Url)),
            Variants = product.Variants.OrderBy(v => v.Id).Select(v => new VariantInput
            {
                Id = v.Id,
                Sku = v.Sku,
                Name = v.Name,
                Price = v.Price,
                CompareAtPrice = v.CompareAtPrice,
                StockQuantity = v.StockQuantity,
                IsActive = v.IsActive,
                LowStockThreshold = v.LowStockThreshold
            }).ToList(),
            Categories = await Db.Categories.AsNoTracking().Where(c => c.IsGlobal || c.ShopId == shop.Id).OrderBy(c => c.Name).ToListAsync(ct)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(ProductEditViewModel model, CancellationToken ct)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return Challenge();
        var shop = await _shops.GetPrimaryShopForUserAsync(userId, ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });
        var access = await _shops.ResolveAccessAsync(userId, shop.Id, ct);
        if (access?.CanManageCatalogue != true) return Forbid();

        // Variant rows the administrator added but left blank, or ticked for removal, are dropped
        // here. Their ModelState entries must be cleared too: model binding has already recorded
        // a "Sku is required" error for a blank row, and a stale entry would keep ModelState
        // invalid forever, making the product unsaveable.
        var discardedIndexes = new List<int>();

        for (var i = 0; i < model.Variants.Count; i++)
        {
            var v = model.Variants[i];

            if (v.Delete ||
                v.Id == 0 && string.IsNullOrWhiteSpace(v.Sku) && string.IsNullOrWhiteSpace(v.Name) && v.Price == 0m)
            {
                discardedIndexes.Add(i);
            }
        }

        foreach (var index in discardedIndexes)
        {
            // Keys look like "Variants[3].Sku"; drop every entry for that row.
            var prefix = $"Variants[{index}].";
            var keys = ModelState.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();

            foreach (var key in keys)
            {
                ModelState.Remove(key);
            }
        }

        model.Variants = model.Variants
            .Where((v, index) => !discardedIndexes.Contains(index))
            .ToList();

        var keptVariants = model.Variants.Where(v => !v.Delete).ToList();

        if (keptVariants.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "A product needs at least one variant.");
        }

        var duplicateSkus = keptVariants
            .Where(v => !string.IsNullOrWhiteSpace(v.Sku))
            .GroupBy(v => v.Sku.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateSkus.Count > 0)
        {
            ModelState.AddModelError(string.Empty, $"Duplicate SKU(s) in this form: {string.Join(", ", duplicateSkus)}.");
        }

        if (!ModelState.IsValid)
        {
            model.Categories = await Db.Categories.AsNoTracking().Where(c => c.IsGlobal || c.ShopId == shop.Id).OrderBy(c => c.Name).ToListAsync(ct);
            ViewData["Title"] = model.IsNew ? "New product" : $"Edit — {model.Name}";
            return View("Edit", model);
        }

        Product product;
        if (model.Id == 0)
        {
            product = new Product { CreatedUtc = DateTime.UtcNow, ShopId = shop.Id };
            Db.Products.Add(product);
        }
        else
        {
            var existing = await Db.Products
                .Include(p => p.Variants)
                .Include(p => p.Images)
                .FirstOrDefaultAsync(p => p.Id == model.Id && p.ShopId == shop.Id, ct);

            if (existing is null)
            {
                return NotFound();
            }

            product = existing;
        }

        if (product.ShopId == 0)
        {
            var defaultShopId = await Db.Shops.OrderBy(s => s.Id).Select(s => s.Id).FirstOrDefaultAsync(ct);
            product.ShopId = defaultShopId;
        }

        product.ShopId = shop.Id;
        product.Name = model.Name.Trim();
        product.ShortDescription = string.IsNullOrWhiteSpace(model.ShortDescription) ? null : model.ShortDescription.Trim();
        product.Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim();
        product.Brand = string.IsNullOrWhiteSpace(model.Brand) ? null : model.Brand.Trim();
        product.CategoryId = model.CategoryId;
        product.IsFeatured = model.IsFeatured;
        product.UpdatedUtc = DateTime.UtcNow;
        product.Slug = await EnsureUniqueSlugAsync(model.Slug, model.Name, product.Id, ct);

        // A vendor cannot put a listing on sale by itself — the marketplace publishes it once an
        // administrator has approved it — so the form's "published" box is ignored here and the
        // review state is decided by these rules instead.
        var previousStatus = product.ModerationStatus;

        if (model.IsNew)
        {
            // A new listing starts as a draft and has to be submitted for review.
            product.ModerationStatus = ListingStatus.Draft;
            product.ModerationReason = null;
            product.IsPublished = false;
            product.ModeratedUtc = DateTime.UtcNow;
        }
        else
        {
            switch (product.ModerationStatus)
            {
                case ListingStatus.PendingReview:
                    // The vendor changed a listing that was waiting for a decision, so it is no
                    // longer the version that was submitted: it goes back to draft and has to be
                    // submitted again.
                    product.ModerationStatus = ListingStatus.Draft;
                    product.ModerationReason = null;
                    product.IsPublished = false;
                    product.ModeratedUtc = DateTime.UtcNow;
                    break;

                case ListingStatus.Rejected:
                    // The listing stays rejected and keeps the reason the administrator gave, so
                    // the vendor can still read it while they fix the listing.
                    product.IsPublished = false;
                    break;

                case ListingStatus.Draft:
                    product.IsPublished = false;
                    break;

                case ListingStatus.Published:
                    // A live listing stays live when it is edited; it does not go back into the queue.
                    product.IsPublished = true;
                    break;
            }
        }

        // ---- Variants -------------------------------------------------
        foreach (var input in model.Variants.Where(v => v.Delete && v.Id != 0))
        {
            var variant = product.Variants.FirstOrDefault(v => v.Id == input.Id);
            if (variant is null)
            {
                continue;
            }

            // A variant that has been sold cannot be removed without orphaning order history.
            var hasSales = await Db.OrderLines.AnyAsync(l => l.ProductVariantId == variant.Id, ct);
            if (hasSales)
            {
                variant.IsActive = false;
            }
            else
            {
                Db.ProductVariants.Remove(variant);
            }
        }

        foreach (var input in keptVariants)
        {
            var sku = input.Sku.Trim();

            var skuOwner = await Db.ProductVariants
                .FirstOrDefaultAsync(v => v.Sku == sku && v.Id != input.Id, ct);

            if (skuOwner is not null)
            {
                ModelState.AddModelError(string.Empty, $"SKU '{sku}' is already used by another variant.");
                model.Categories = await Db.Categories.AsNoTracking().Where(c => c.IsGlobal || c.ShopId == shop.Id).OrderBy(c => c.Name).ToListAsync(ct);
                ViewData["Title"] = model.IsNew ? "New product" : $"Edit — {model.Name}";
                return View("Edit", model);
            }

            if (input.Id == 0)
            {
                product.Variants.Add(new ProductVariant
                {
                    Sku = sku,
                    Name = input.Name.Trim(),
                    Price = input.Price,
                    CompareAtPrice = input.CompareAtPrice is > 0 ? input.CompareAtPrice : null,
                    StockQuantity = input.StockQuantity,
                    IsActive = input.IsActive,
                    LowStockThreshold = input.LowStockThreshold
                });
            }
            else
            {
                var variant = product.Variants.FirstOrDefault(v => v.Id == input.Id);
                if (variant is null)
                {
                    continue;
                }

                variant.Sku = sku;
                variant.Name = input.Name.Trim();
                variant.Price = input.Price;
                variant.CompareAtPrice = input.CompareAtPrice is > 0 ? input.CompareAtPrice : null;
                variant.StockQuantity = input.StockQuantity;
                variant.IsActive = input.IsActive;
                variant.LowStockThreshold = input.LowStockThreshold;
            }
        }

        // ---- Images ---------------------------------------------------
        // Uploads are stored first, then merged with the typed URLs; the gallery is rebuilt in the
        // resulting order so the first line of the textarea still decides the primary image.
        var previousUrls = product.Images.Select(i => i.Url).ToList();

        IReadOnlyList<string> urls;
        try
        {
            urls = await ProductImageInput.CollectAsync(
                model.ImageUrls, model.ImageFiles, _images, _imageOptions.MaxPerProduct, ct);
        }
        catch (ImageRejectedException ex)
        {
            ModelState.AddModelError(nameof(model.ImageFiles), ex.Message);
            model.Categories = await Db.Categories.AsNoTracking().Where(c => c.IsGlobal || c.ShopId == shop.Id).OrderBy(c => c.Name).ToListAsync(ct);
            ViewData["Title"] = model.IsNew ? "New product" : $"Edit — {model.Name}";
            return View("Edit", model);
        }

        // Rebuild the gallery in the order given; the first URL becomes the primary image.
        Db.ProductImages.RemoveRange(product.Images);
        product.Images.Clear();

        for (var i = 0; i < urls.Count; i++)
        {
            product.Images.Add(new ProductImage
            {
                Url = urls[i],
                AltText = product.Name,
                DisplayOrder = i,
                IsPrimary = i == 0
            });
        }

        try
        {
            await Db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Saving product {ProductId} failed", product.Id);
            ModelState.AddModelError(string.Empty, "That could not be saved — a SKU or slug may already be in use.");
            model.Categories = await Db.Categories.AsNoTracking().Where(c => c.IsGlobal || c.ShopId == shop.Id).OrderBy(c => c.Name).ToListAsync(ct);
            ViewData["Title"] = model.IsNew ? "New product" : $"Edit — {model.Name}";
            return View("Edit", model);
        }

        // Files that dropped out of the gallery are removed once the save is committed, and only if
        // no other product still points at them (a URL can in principle be reused).
        await ProductImageInput.RemoveOrphansAsync(
            previousUrls, urls, _images,
            url => Db.ProductImages.AnyAsync(i => i.Url == url, ct),
            ct);

        // Ensure the product is sellable: give the owning shop a listing that mirrors the cheapest
        // variant price and the aggregate stock, so cart and checkout can resolve price and stock.
        if (product.ShopId != 0)
        {
            var listing = await Db.ShopListings.FirstOrDefaultAsync(
                l => l.ShopId == product.ShopId && l.ProductId == product.Id, ct);
            if (listing is null)
            {
                var cheapest = product.Variants.Where(v => v.IsActive).Select(v => (decimal?)v.Price).Min()
                               ?? product.Variants.FirstOrDefault()?.Price;
                var stock = product.Variants.Where(v => v.IsActive).Sum(v => v.StockQuantity);
                Db.ShopListings.Add(new ShopListing
                {
                    ShopId = product.ShopId,
                    ProductId = product.Id,
                    Price = cheapest,
                    StockQuantity = stock > 0 ? stock : product.Variants.Sum(v => v.StockQuantity),
                    IsActive = true,
                    UpdatedUtc = DateTime.UtcNow
                });
                await Db.SaveChangesAsync(ct);
            }
        }

        _logger.LogInformation("Saved product {ProductId} ({Name})", product.Id, product.Name);

        var message = model.IsNew
            ? $"'{product.Name}' was created as a draft. Submit it for review when it is ready."
            : previousStatus == ListingStatus.PendingReview
                ? $"'{product.Name}' was saved. It is back in draft, so submit it for review again."
                : $"'{product.Name}' was saved.";

        Flash(message);

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Puts a listing into the review queue. The marketplace publishes it once an administrator has
    /// approved it.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(int id, CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });

        var result = await _moderation.SubmitAsync(id, shop.Id, ct);
        Flash(result.Message, result.Success);

        if (result.Success && result.Product is { } submitted)
        {
            await _audit.RecordAsync(User.Identity?.Name ?? "vendor", "listing.submitted", "Product", id,
                $"Submitted the listing '{submitted.Name}' for review.");
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>Takes a live listing off sale, as its owner. It stays editable as a draft.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unpublish(int id, CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });

        var result = await _moderation.UnpublishAsync(id, shop.Id, ct);
        Flash(result.Message, result.Success);

        if (result.Success && result.Product is { } taken)
        {
            await _audit.RecordAsync(User.Identity?.Name ?? "vendor", "listing.unpublished", "Product", id,
                $"Took the listing '{taken.Name}' off sale.");
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>Soft-deletes a product so it disappears from the storefront but history survives.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });
        var product = await Db.Products.FirstOrDefaultAsync(p => p.Id == id && p.ShopId == shop.Id, ct);
        if (product is null)
        {
            return NotFound();
        }

        product.IsDeleted = true;
        product.IsPublished = false;
        product.ModerationStatus = ListingStatus.Draft;
        product.UpdatedUtc = DateTime.UtcNow;
        await Db.SaveChangesAsync(ct);

        Flash($"'{product.Name}' deleted. It can be restored from the Deleted filter.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(int id, CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });
        var product = await Db.Products.FirstOrDefaultAsync(p => p.Id == id && p.ShopId == shop.Id, ct);
        if (product is null)
        {
            return NotFound();
        }

        product.IsDeleted = false;
        product.IsPublished = false;
        product.ModerationStatus = ListingStatus.Draft;
        product.UpdatedUtc = DateTime.UtcNow;
        await Db.SaveChangesAsync(ct);

        Flash($"'{product.Name}' restored as an unpublished draft. Submit it for review to put it on sale.");
        return RedirectToAction(nameof(Index), new { state = "draft" });
    }

    private async Task<Shop?> ResolveShopAsync(CancellationToken ct)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return null;
        return await _shops.GetPrimaryShopForUserAsync(userId, ct);
    }

    /// <summary>Derives a slug from the name when none is supplied, and keeps it unique.</summary>
    private async Task<string> EnsureUniqueSlugAsync(string? requested, string name, int currentProductId, CancellationToken ct)
    {
        var baseSlug = _slugs.Slugify(string.IsNullOrWhiteSpace(requested) ? name : requested);

        if (string.IsNullOrEmpty(baseSlug))
        {
            baseSlug = $"product-{Guid.NewGuid():N}"[..16];
        }

        var candidate = baseSlug;
        var suffix = 2;

        while (await Db.Products
                   .AnyAsync(p => p.Slug == candidate && p.Id != currentProductId, ct))
        {
            candidate = $"{baseSlug}-{suffix++}";
        }

        return candidate;
    }
}
