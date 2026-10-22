using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebDS_Test.Areas.Admin.Models;
using WebDS_Test.Domain;

namespace WebDS_Test.Areas.Admin.Controllers;

/// <summary>Category tree administration.</summary>
public class CategoriesController : AdminControllerBase
{
    private readonly ISlugService _slugs;

    public CategoriesController(ApplicationDbContext db, IOptions<StoreSettings> settings, ISlugService slugs)
        : base(db, settings)
    {
        _slugs = slugs;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var model = await BuildTreeAsync(ct);

        ViewData["Title"] = "Categories";
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Create(int? parentId, CancellationToken ct)
    {
        ViewData["Title"] = "New category";

        return View("Edit", new CategoryEditViewModel
        {
            ParentId = parentId,
            AllCategories = await Db.Categories.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct)
        });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var category = await Db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (category is null)
        {
            return NotFound();
        }

        ViewData["Title"] = $"Edit — {category.Name}";

        return View(new CategoryEditViewModel
        {
            Id = category.Id,
            Name = category.Name,
            Slug = category.Slug,
            Description = category.Description,
            ParentId = category.ParentId,
            DisplayOrder = category.DisplayOrder,
            IsActive = category.IsActive,
            CommissionRatePercent = category.CommissionRate.HasValue
                ? Math.Round(category.CommissionRate.Value * 100m, 2)
                : null,
            AllCategories = await Db.Categories.AsNoTracking()
                .Where(c => c.Id != id)
                .OrderBy(c => c.Name)
                .ToListAsync(ct),
            ProductCount = await Db.Products.CountAsync(p => p.CategoryId == id, ct)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(CategoryEditViewModel model, CancellationToken ct)
    {
        // A category cannot be its own parent, nor sit under one of its own descendants.
        if (model.ParentId is { } parentId)
        {
            if (parentId == model.Id)
            {
                ModelState.AddModelError(nameof(model.ParentId), "A category cannot be its own parent.");
            }
            else if (model.Id != 0 && await IsDescendantAsync(parentId, model.Id, ct))
            {
                ModelState.AddModelError(nameof(model.ParentId), "That parent would create a loop in the category tree.");
            }
        }

        if (!ModelState.IsValid)
        {
            model.AllCategories = await Db.Categories.AsNoTracking()
                .Where(c => c.Id != model.Id)
                .OrderBy(c => c.Name)
                .ToListAsync(ct);

            ViewData["Title"] = model.IsNew ? "New category" : $"Edit — {model.Name}";
            return View("Edit", model);
        }

        Category category;
        if (model.Id == 0)
        {
            category = new Category();
            Db.Categories.Add(category);
        }
        else
        {
            var existing = await Db.Categories.FirstOrDefaultAsync(c => c.Id == model.Id, ct);
            if (existing is null)
            {
                return NotFound();
            }

            category = existing;
        }

        category.Name = model.Name.Trim();
        category.Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim();
        category.ParentId = model.ParentId;
        category.DisplayOrder = model.DisplayOrder;
        category.IsActive = model.IsActive;
        category.CommissionRate = model.CommissionRatePercent.HasValue
            ? Math.Clamp(model.CommissionRatePercent.Value / 100m, 0m, 1m)
            : null;
        category.Slug = await EnsureUniqueSlugAsync(model.Slug, model.Name, category.Id, ct);

        await Db.SaveChangesAsync(ct);

        Flash(model.IsNew ? $"Category '{category.Name}' created." : $"Category '{category.Name}' saved.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var category = await Db.Categories
            .Include(c => c.Children)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (category is null)
        {
            return NotFound();
        }

        if (category.Children.Count > 0)
        {
            Flash($"'{category.Name}' still has sub-categories. Remove or reassign them first.", success: false);
            return RedirectToAction(nameof(Index));
        }

        var productCount = await Db.Products.CountAsync(p => p.CategoryId == id, ct);
        if (productCount > 0)
        {
            // Deleting would orphan live products, so deactivate instead.
            category.IsActive = false;
            await Db.SaveChangesAsync(ct);

            Flash($"'{category.Name}' is used by {productCount} product(s), so it was deactivated instead of deleted.", success: false);
            return RedirectToAction(nameof(Index));
        }

        Db.Categories.Remove(category);
        await Db.SaveChangesAsync(ct);

        Flash($"Category '{category.Name}' deleted.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>True when <paramref name="candidateId"/> sits beneath <paramref name="ancestorId"/>.</summary>
    private async Task<bool> IsDescendantAsync(int candidateId, int ancestorId, CancellationToken ct)
    {
        var edges = await Db.Categories.AsNoTracking()
            .Select(c => new { c.Id, c.ParentId })
            .ToListAsync(ct);

        var cursor = edges.FirstOrDefault(e => e.Id == candidateId);
        var guard = 0;

        while (cursor?.ParentId is { } parent && guard++ < 32)
        {
            if (parent == ancestorId)
            {
                return true;
            }

            cursor = edges.FirstOrDefault(e => e.Id == parent);
        }

        return false;
    }

    private async Task<string> EnsureUniqueSlugAsync(string? requested, string name, int currentId, CancellationToken ct)
    {
        var baseSlug = _slugs.Slugify(string.IsNullOrWhiteSpace(requested) ? name : requested);
        if (string.IsNullOrEmpty(baseSlug))
        {
            baseSlug = $"category-{Guid.NewGuid():N}"[..16];
        }

        var candidate = baseSlug;
        var suffix = 2;

        while (await Db.Categories.AnyAsync(c => c.Slug == candidate && c.Id != currentId, ct))
        {
            candidate = $"{baseSlug}-{suffix++}";
        }

        return candidate;
    }

    /// <summary>Builds the category tree for display, with product counts.</summary>
    private async Task<CategoryTreeViewModel> BuildTreeAsync(CancellationToken ct)
    {
        var categories = await Db.Categories.AsNoTracking()
            .OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name)
            .ToListAsync(ct);

        var counts = await Db.Products.AsNoTracking()
            .GroupBy(p => p.CategoryId)
            .Select(g => new { CategoryId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CategoryId, x => x.Count, ct);

        var byParent = categories.ToLookup(c => c.ParentId);

        // Flatten into depth-ordered rows so the view can render an indented table.
        var rows = new List<CategoryRow>();
        void Walk(int? parentId, int depth)
        {
            foreach (var category in byParent[parentId])
            {
                rows.Add(new CategoryRow(category, depth, counts.GetValueOrDefault(category.Id)));
                Walk(category.Id, depth + 1);
            }
        }

        Walk(null, 0);

        return new CategoryTreeViewModel { Rows = rows, TotalCategories = categories.Count };
    }
}

public sealed record CategoryRow(Category Category, int Depth, int ProductCount);

public sealed class CategoryTreeViewModel
{
    public IReadOnlyList<CategoryRow> Rows { get; init; } = Array.Empty<CategoryRow>();
    public int TotalCategories { get; init; }
}
