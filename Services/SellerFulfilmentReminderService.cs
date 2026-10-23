using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services.Email;

namespace WebDS_Test.Services;

/// <summary>
/// Runs daily to remind sellers to confirm their packages before the 3 pm cut-off.
/// LibanPost's process requires the seller to confirm package count, attach nylon pockets
/// and the invoice by 3 pm so labels can be generated and the shipper can collect.
/// </summary>
public sealed class SellerFulfilmentReminderService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SellerFulfilmentReminderService> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeOnly _cutOffTime;

    public SellerFulfilmentReminderService(
        IServiceScopeFactory scopeFactory,
        IOptions<SellerFulfilmentReminderOptions> options,
        ILogger<SellerFulfilmentReminderService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _interval = options.Value.CheckInterval;
        _cutOffTime = TimeOnly.Parse(options.Value.CutOffTime);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run once on startup, then on the interval.
        await RunOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var email = scope.ServiceProvider.GetRequiredService<IEmailService>();
            var store = scope.ServiceProvider.GetRequiredService<IOptions<StoreSettings>>().Value;
            var brand = scope.ServiceProvider.GetRequiredService<IOptions<BrandingOptions>>().Value;

            var now = DateTime.UtcNow;
            var todayCutOff = DateTime.SpecifyKind(DateTime.Today.Add(_cutOffTime.ToTimeSpan()), DateTimeKind.Utc);

            // Only send reminders before the daily cut-off.
            if (now > todayCutOff)
            {
                _logger.LogDebug("Past cut-off ({CutOff}), skipping reminder run", _cutOffTime);
                return;
            }

            // Find orders that are paid/pending but have no seller fulfilment confirmed yet.
            var ordersNeedingConfirmation = await db.Orders
                .Include(o => o.Shop)
                .Where(o => (o.Status == OrderStatus.Pending || o.Status == OrderStatus.Paid)
                         && o.Shop != null
                         && !string.IsNullOrWhiteSpace(o.Shop.Email))
                .ToListAsync(ct);

            var shopsToRemind = new List<Shop>();

            foreach (var order in ordersNeedingConfirmation)
            {
                var hasFulfilment = await db.SellerFulfilments
                    .AnyAsync(f => f.OrderId == order.Id, ct);

                if (!hasFulfilment)
                {
                    shopsToRemind.Add(order.Shop!);
                }
            }

            // Deduplicate by shop - one email per shop per day.
            var uniqueShops = shopsToRemind
                .GroupBy(s => s.Id)
                .Select(g => g.First())
                .ToList();

            foreach (var shop in uniqueShops)
            {
                var dedupeKey = $"seller-fulfilment-reminder:{shop.Id}:{DateTime.UtcNow:yyyyMMdd}";
                var alreadySent = await db.EmailMessages
                    .AnyAsync(m => m.DedupeKey == dedupeKey, ct);

                if (alreadySent)
                {
                    continue;
                }

                var html = $"""
                    <p style="margin:0 0 12px">Hi {Escape(shop.Name)},</p>
                    <p style="margin:0 0 12px">
                      You have paid orders waiting for package confirmation. LibanPost's daily
                      cut-off is <strong>3:00&nbsp;PM</strong> — please confirm your packages
                      before then so we can generate labels and schedule pickup.
                    </p>
                    <p style="margin:0 0 12px">
                      <a href="{store.BaseUrl.TrimEnd('/')}/Vendor/Orders" style="display:inline-block;
                          padding:10px 16px;background:{brand.Accent};color:{brand.Ink};
                          text-decoration:none;border-radius:4px;font-weight:600">
                        Confirm packages now
                      </a>
                    </p>
                    <p style="margin:16px 0 0;color:{brand.InkSubtle};font-size:13px">
                      After 3&nbsp;PM, packages confirmed today will be picked up tomorrow.
                    </p>
                    """;

                var text = $"""
                    Hi {shop.Name},

                    You have paid orders waiting for package confirmation. LibanPost's daily
                    cut-off is 3:00 PM — please confirm your packages before then so we can
                    generate labels and schedule pickup.

                    {store.BaseUrl.TrimEnd('/')}/Vendor/Orders

                    After 3 PM, packages confirmed today will be picked up tomorrow.

                    {store.Name}
                    """;

                await email.EnqueueAsync(new EmailDraft(
                    shop.Email!, shop.Name,
                    "Packages waiting — confirm before 3 PM cut-off",
                    html, text,
                    "seller-fulfilment-reminder",
                    DedupeKey: dedupeKey,
                    UserId: shop.OwnerUserId), ct);
            }

            if (uniqueShops.Count > 0)
            {
                _logger.LogInformation("Sent {Count} seller fulfilment reminders", uniqueShops.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Seller fulfilment reminder run failed");
        }
    }

    private static string Escape(string value) => System.Net.WebUtility.HtmlEncode(value);
}

/// <summary>Configuration for the seller fulfilment reminder service.</summary>
public sealed class SellerFulfilmentReminderOptions
{
    public const string SectionName = "SellerFulfilmentReminder";

    /// <summary>How often to check for unconfirmed packages. Default: 24 hours.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Daily cut-off time in UTC. Default: 15:00 (3 PM UTC).</summary>
    public string CutOffTime { get; set; } = "15:00";
}