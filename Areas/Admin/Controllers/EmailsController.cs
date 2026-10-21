using Microsoft.AspNetCore.Mvc;
using WebDS_Test.Areas.Admin.Models;
using WebDS_Test.Services.Email;

namespace WebDS_Test.Areas.Admin.Controllers;

/// <summary>
/// The transactional email outbox: what the store has sent, what it is waiting to send, and what
/// failed. This is the window onto a background process that would otherwise be invisible.
/// </summary>
public class EmailsController : AdminControllerBase
{
    private readonly IEmailService _emails;
    private readonly EmailOptions _options;

    public EmailsController(
        ApplicationDbContext db,
        IOptions<StoreSettings> settings,
        IEmailService emails,
        IOptions<EmailOptions> options)
        : base(db, settings)
    {
        _emails = emails;
        _options = options.Value;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        EmailStatus? status, string? kind, int page = 1, CancellationToken ct = default)
    {
        // Two rows per catalogue page: a touch more history than a shopper-facing grid, without
        // being an unbounded list.
        var pageSize = Math.Clamp(Settings.ProductsPerPage * 2, 10, 100);

        var messages = await _emails.GetOutboxAsync(status, kind, page, pageSize, ct);
        var counts = await _emails.GetCountsAsync(ct);

        var kinds = await Db.EmailMessages.AsNoTracking()
            .Select(m => m.Kind)
            .Distinct()
            .OrderBy(k => k)
            .ToListAsync(ct);

        return View(new AdminEmailListViewModel
        {
            Messages = new PagedResult<AdminEmailRow>
            {
                Items = messages.Items.Select(ToRow).ToList(),
                Page = messages.Page,
                PageSize = messages.PageSize,
                TotalCount = messages.TotalCount
            },
            Counts = counts,
            Status = status,
            Kind = kind,
            Kinds = kinds,
            TestAddress = CurrentUserEmail ?? Settings.SupportEmail
        });
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var message = await _emails.GetMessageAsync(id, ct);
        if (message is null)
        {
            return NotFound();
        }

        ViewData["Transport"] = _options.Transport;
        return View(message);
    }

    /// <summary>Returns a message to the queue and clears its failure history.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resend(
        int id, EmailStatus? status, string? kind, int page, CancellationToken ct)
    {
        var queued = await _emails.ResendAsync(id, ct);

        if (queued)
        {
            Flash(_options.Enabled
                ? "Message re-queued. The dispatcher will attempt it on its next pass."
                : "Transactional email is disabled, so the message was recorded but not queued for sending.");
        }
        else
        {
            Flash("That message no longer exists.", success: false);
        }

        return RedirectToAction(nameof(Index), new { status, kind, page });
    }

    /// <summary>Queues a fixed test message, so an administrator can confirm delivery end to end.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendTest(string to, EmailStatus? status, string? kind, int page, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(to) || !to.Contains('@'))
        {
            Flash("Enter an email address to send the test to.", success: false);
            return RedirectToAction(nameof(Index), new { status, kind, page });
        }

        await _emails.EnqueueTestAsync(to.Trim(), ct);
        Flash($"Test message queued for {to.Trim()}.");
        return RedirectToAction(nameof(Index), new { status, kind, page });
    }

    private string? CurrentUserEmail => User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;

    private static AdminEmailRow ToRow(EmailMessage message) => new()
    {
        Id = message.Id,
        ToAddress = message.ToAddress,
        ToName = message.ToName,
        Subject = message.Subject,
        Kind = message.Kind,
        Status = message.Status,
        Attempts = message.Attempts,
        LastError = message.LastError,
        CreatedUtc = message.CreatedUtc,
        SentUtc = message.SentUtc,
        OrderId = message.OrderId
    };
}
