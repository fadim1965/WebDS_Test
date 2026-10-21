using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebDS_Test.Domain;
using WebDS_Test.Services;
using WebDS_Test.Services.Export;

namespace WebDS_Test.Areas.Admin.Controllers;

/// <summary>
/// Base for every administration page. The whole area is restricted to the <c>Admin</c> role
/// here rather than by convention, so a new controller cannot accidentally ship unprotected.
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public abstract class AdminControllerBase : Controller
{
    protected AdminControllerBase(ApplicationDbContext db, IOptions<StoreSettings> settings)
    {
        Db = db;
        Settings = settings.Value;
    }

    protected ApplicationDbContext Db { get; }
    protected StoreSettings Settings { get; }

    protected string? CurrentUserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    protected string CurrentUserName => User.Identity?.Name ?? "unknown";

    /// <summary>Sets the flash banner rendered by the admin layout.</summary>
    protected void Flash(string message, bool success = true)
    {
        TempData["Message"] = message;
        TempData["MessageType"] = success ? "success" : "danger";
    }

    /// <summary>
    /// Sends a report as a download. The <paramref name="format"/> value comes straight from the
    /// query string, so it is parsed leniently — an unknown value falls back to CSV rather than
    /// failing. A UTC timestamp in the name keeps repeated downloads from colliding in the
    /// browser's download folder.
    /// </summary>
    protected IActionResult ExportFile(TabularData data, string? format)
    {
        var file = ExportBuilder.Build(data, ExportBuilder.ParseFormat(format));
        var name = $"{data.FileStem}-{DateTime.UtcNow:yyyyMMdd-HHmm}.{file.Extension}";

        return File(file.Content, file.ContentType, name);
    }
}
