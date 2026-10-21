using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebDS_Test.Domain;
using WebDS_Test.Services.Export;

namespace WebDS_Test.Areas.Admin.Controllers;

/// <summary>
/// Tax classes. Products may point at one; a product that does not is taxed at the store-wide
/// <c>Store:TaxRate</c>, which is how every order behaved before this feature existed.
/// </summary>
public class TaxController : AdminControllerBase
{
    private readonly ILogger<TaxController> _logger;

    public TaxController(
        ApplicationDbContext db,
        IOptions<StoreSettings> settings,
        ILogger<TaxController> logger)
        : base(db, settings)
    {
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var classes = await Db.TaxClasses.AsNoTracking()
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        // How many products point at each class, so staff can see what a deletion would orphan.
        var usage = await Db.Products.AsNoTracking()
            .Where(p => p.TaxClassId != null)
            .GroupBy(p => p.TaxClassId!.Value)
            .Select(g => new { TaxClassId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TaxClassId, x => x.Count, ct);

        var unassigned = await Db.Products.AsNoTracking().CountAsync(p => !p.IsDeleted && p.TaxClassId == null, ct);

        ViewData["Title"] = "Tax classes";
        return View(new TaxListViewModel
        {
            Classes = classes.Select(t => new TaxClassRow
            {
                Id = t.Id,
                Name = t.Name,
                Rate = t.Rate,
                IsActive = t.IsActive,
                IsExempt = t.IsExempt,
                ProductCount = usage.TryGetValue(t.Id, out var n) ? n : 0
            }).ToList(),
            UnassignedProducts = unassigned,
            StoreRate = Settings.TaxRate
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name, decimal rate, bool isExempt, CancellationToken ct)
    {
        name = (name ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            Flash("A tax class needs a name.", false);
            return RedirectToAction(nameof(Index));
        }

        if (await Db.TaxClasses.AnyAsync(t => t.Name == name, ct))
        {
            Flash($"A tax class called '{name}' already exists.", false);
            return RedirectToAction(nameof(Index));
        }

        if (rate is < 0m or > 1m)
        {
            Flash("The rate must be a fraction between 0 and 1 — 0.11 for eleven percent.", false);
            return RedirectToAction(nameof(Index));
        }

        Db.TaxClasses.Add(new TaxClass
        {
            Name = name,
            Rate = isExempt ? 0m : rate,
            IsActive = true,
            IsExempt = isExempt
        });

        await Db.SaveChangesAsync(ct);
        Flash($"Tax class '{name}' created.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Deactivates rather than deletes whenever products still reference the class. Deleting would
    /// silently move those products onto the store default rate — a real change to what a shopper
    /// pays, made without anyone deciding to make it.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Deactivate(int id, CancellationToken ct)
    {
        var taxClass = await Db.TaxClasses.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (taxClass is null)
        {
            Flash("That tax class no longer exists.", false);
            return RedirectToAction(nameof(Index));
        }

        if (await Db.Products.AnyAsync(p => p.TaxClassId == id, ct))
        {
            taxClass.IsActive = false;
            await Db.SaveChangesAsync(ct);
            Flash(
                $"'{taxClass.Name}' is still used by products, so it was deactivated rather than deleted. " +
                "Those products now fall back to the store rate until they are reassigned.",
                false);
            return RedirectToAction(nameof(Index));
        }

        Db.TaxClasses.Remove(taxClass);
        await Db.SaveChangesAsync(ct);
        Flash($"Tax class '{taxClass.Name}' deleted.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Export(string? format, CancellationToken ct)
    {
        var classes = await Db.TaxClasses.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);

        var data = new TabularData(
            "TaxClasses",
            "tax-classes",
            new[] { "Name", "Rate", "Percent", "Active", "Exempt" },
            classes.Select(t => new object?[]
            {
                t.Name,
                t.Rate,
                Math.Round(t.Rate * 100m, 2),
                t.IsActive,
                t.IsExempt
            }).ToList());

        return ExportFile(data, format);
    }

    public sealed class TaxListViewModel
    {
        public IReadOnlyList<TaxClassRow> Classes { get; init; } = Array.Empty<TaxClassRow>();
        public int UnassignedProducts { get; init; }
        public decimal StoreRate { get; init; }
    }

    public sealed class TaxClassRow
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public decimal Rate { get; init; }
        public bool IsActive { get; init; }
        public bool IsExempt { get; init; }
        public int ProductCount { get; init; }
    }
}
