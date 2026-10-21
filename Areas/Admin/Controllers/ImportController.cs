using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebDS_Test.Services.Export;
using WebDS_Test.Services.Imports;

namespace WebDS_Test.Areas.Admin.Controllers;

/// <summary>Bulk catalogue import from CSV, and the template to build it from.</summary>
public class ImportController : AdminControllerBase
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private readonly IProductImportService _imports;

    public ImportController(
        ApplicationDbContext db,
        IOptions<StoreSettings> settings,
        IProductImportService imports)
        : base(db, settings)
    {
        _imports = imports;
    }

    [HttpGet]
    public async Task<IActionResult> Index(int? shopId, CancellationToken ct)
    {
        ViewData["Title"] = "Import products";
        return View(new ImportViewModel
        {
            Shops = await Db.Shops.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct),
            ShopId = shopId ?? (await Db.Shops.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct))?.Id,
            Header = _imports.TemplateHeader
        });
    }

    /// <summary>A blank CSV with the expected header, so nobody has to guess the column names.</summary>
    [HttpGet]
    public IActionResult Template() =>
        File(
            new byte[] { 0xEF, 0xBB, 0xBF }.Concat(
                System.Text.Encoding.UTF8.GetBytes(
                    _imports.TemplateHeader + "\r\n" +
                    "Sample Product,sample-product,Packaging,Acme,A short line,The long description,SKU-001,Default,9.99,,25,true,false,https://example.com/a.jpg;https://example.com/b.jpg\r\n"))
                .ToArray(),
            "text/csv; charset=utf-8",
            "product-import-template.csv");

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(int shopId, IFormFile? file, bool createMissing, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            Flash("Choose a CSV file to import.", false);
            return await Index(shopId, ct);
        }

        if (file.Length > MaxBytes)
        {
            Flash($"That file is {file.Length / 1024 / 1024} MB. The limit is 5 MB.", false);
            return await Index(shopId, ct);
        }

        if (!Path.GetExtension(file.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            Flash("Only .csv files can be imported.", false);
            return await Index(shopId, ct);
        }

        using var reader = new StreamReader(file.OpenReadStream());
        var csv = await reader.ReadToEndAsync(ct);

        var result = await _imports.ImportAsync(csv, shopId, createMissing, ct);

        if (result.Success)
        {
            Flash(result.Summary);
            return RedirectToAction("Index", "Products");
        }

        TempData["ImportErrors"] = string.Join("\n", result.Errors);

        ViewData["Title"] = "Import products";
        return View(new ImportViewModel
        {
            Shops = await Db.Shops.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct),
            ShopId = shopId,
            Header = _imports.TemplateHeader,
            Errors = result.Errors
        });
    }

    public sealed class ImportViewModel
    {
        public IReadOnlyList<Domain.Shop> Shops { get; init; } = Array.Empty<Domain.Shop>();
        public int? ShopId { get; init; }
        public string Header { get; init; } = string.Empty;
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    }
}
