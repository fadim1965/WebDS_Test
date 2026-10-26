using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Infrastructure.Middleware;
using WebDS_Test.Services;
using WebDS_Test.Services.Email;
using WebDS_Test.Services.Images;
using WebDS_Test.Services.Localization;
using WebDS_Test.Services.Logging;
using WebDS_Test.Services.Shipping;
using WebDS_Test.Services.Shopify;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var dbLogOptions = builder.Configuration.GetSection(DbLogOptions.SectionName).Get<DbLogOptions>() ?? new DbLogOptions();

if (dbLogOptions.Enabled)
{
    builder.Services.AddSingleton(dbLogOptions);
    builder.Services.AddSingleton(new DbLogQueue(dbLogOptions.QueueCapacity));
    builder.Services.AddSingleton<ILoggerProvider, DbLoggerProvider>();
    builder.Services.AddHostedService<DbLogWriter>();
}

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------
var storeSettings = builder.Configuration.GetSection(StoreSettings.SectionName).Get<StoreSettings>() ?? new StoreSettings();
builder.Services.Configure<StoreSettings>(builder.Configuration.GetSection(StoreSettings.SectionName));
builder.Services.Configure<BrandingOptions>(builder.Configuration.GetSection(BrandingOptions.SectionName));

// ---------------------------------------------------------------------------
// Data Providers
// ---------------------------------------------------------------------------
var sqliteConnectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=App_Data/store.db";
var sqlServerConnectionString = builder.Configuration.GetConnectionString("MultiVendorsStoresConn");
var useSqlServer = builder.Configuration["Database:Provider"] is { } provider
                   && provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase)
                   && !string.IsNullOrWhiteSpace(sqlServerConnectionString);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
{
    if (useSqlServer)
    {
        options.UseSqlServer(sqlServerConnectionString!, sql => sql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
    }
    else
    {
        options.UseSqlite(sqliteConnectionString, sqlite => sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
    }

    options.EnableSensitiveDataLogging(builder.Environment.IsDevelopment());
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = ".WebDS.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.IdleTimeout = TimeSpan.FromDays(7);
});

// ---------------------------------------------------------------------------
// Authentication & Security
// ---------------------------------------------------------------------------
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.Cookie.Name = ".WebDS.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole(AccountService.AdminRole));
    // NOTE: these two only require a signed-in user. Shop membership is enforced by
    // VendorControllerBase, not by these policies.
    options.AddPolicy("VendorOnly", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("CustomerOnly", policy => policy.RequireAuthenticatedUser());
});

// ---------------------------------------------------------------------------
// Application services
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<ISlugService, SlugService>();
builder.Services.AddSingleton<IThemeCatalog, ThemeCatalog>();
builder.Services.AddScoped<ICartService, CartService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<IInventoryService, InventoryService>();

// Cache decorator: IShopService resolves to CachedShopService wrapping the real ShopService.
builder.Services.AddScoped<ShopService>();
builder.Services.AddScoped<IShopService>(sp =>
    new CachedShopService(
        sp.GetRequiredService<ShopService>(),
        sp.GetRequiredService<IMemoryCache>()));

builder.Services.AddScoped<IFulfilmentService, FulfilmentService>();
builder.Services.AddScoped<IReviewService, ReviewService>();
builder.Services.AddScoped<ICouponService, CouponService>();
builder.Services.AddScoped<IReturnService, ReturnService>();
builder.Services.AddScoped<IPostSaleService, PostSaleService>();
builder.Services.AddScoped<IAuditTrail, AuditTrail>();
builder.Services.AddScoped<IConversationService, ConversationService>();
builder.Services.AddScoped<WebDS_Test.Services.Imports.IProductImportService, WebDS_Test.Services.Imports.ProductImportService>();
builder.Services.AddSingleton<ITaxCalculator, TaxCalculator>();

builder.Services.Configure<LanguageOptions>(builder.Configuration.GetSection(LanguageOptions.SectionName));
builder.Services.AddSingleton<ITranslationStore, TranslationStore>();
builder.Services.AddScoped<ITranslator, Translator>();

builder.Services.AddScoped<IVendorLedgerService, VendorLedgerService>();
builder.Services.AddScoped<IPayoutService, PayoutService>();
builder.Services.AddScoped<ICodRemittanceService, CodRemittanceService>();
builder.Services.Configure<PayoutOptions>(builder.Configuration.GetSection(PayoutOptions.SectionName));

builder.Services.AddScoped<IListingModerationService, ListingModerationService>();
builder.Services.AddScoped<IReportingService, ReportingService>();
builder.Services.AddSingleton<ISiteSettingStore, SiteSettingStore>();
builder.Services.AddSingleton<ICurrencyFormatter, CurrencyFormatter>();
builder.Services.Configure<ReviewSettings>(builder.Configuration.GetSection(ReviewSettings.SectionName));
builder.Services.Configure<ShippingCarrierOptions>(builder.Configuration.GetSection(ShippingCarrierOptions.SectionName));

builder.Services.AddSingleton<IShippingCarrier, SimulatorShippingCarrier>();
builder.Services.AddHttpClient<LibanPostCarrier>();
builder.Services.AddSingleton<IShippingCarrier>(sp => sp.GetRequiredService<LibanPostCarrier>());
builder.Services.AddSingleton<ICarrierRegistry, CarrierRegistry>();

// ---------------------------------------------------------------------------
// Transactional Mail Channels
// ---------------------------------------------------------------------------
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
var emailOptions = builder.Configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>() ?? new EmailOptions();

builder.Services.AddDataProtection();
builder.Services.AddSingleton<IEmailTransport>(sp =>
{
    if (emailOptions.Transport.Equals("Smtp", StringComparison.OrdinalIgnoreCase))
    {
        return new SmtpEmailTransport(
            sp.GetRequiredService<IOptions<EmailOptions>>(),
            sp.GetRequiredService<ILogger<SmtpEmailTransport>>());
    }

    return new FileEmailTransport(
        sp.GetRequiredService<IOptions<EmailOptions>>(),
        sp.GetRequiredService<IHostEnvironment>(),
        sp.GetRequiredService<ILogger<FileEmailTransport>>());
});

builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IPasswordResetService, PasswordResetService>();

if (emailOptions.Enabled)
{
    builder.Services.AddHostedService<EmailDispatcher>();
}

// Seller fulfilment reminders (daily, before 3 PM cut-off).
builder.Services.Configure<SellerFulfilmentReminderOptions>(
    builder.Configuration.GetSection(SellerFulfilmentReminderOptions.SectionName));
builder.Services.AddHostedService<SellerFulfilmentReminderService>();

// ---------------------------------------------------------------------------
// Pipeline Rate Limiting Defenses
// ---------------------------------------------------------------------------
var rateLimits = builder.Configuration.GetSection(RateLimitSettings.SectionName).Get<RateLimitSettings>() ?? new RateLimitSettings();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, _) =>
    {
        context.HttpContext.Response.Headers["Retry-After"] = Math.Max(1, rateLimits.Auth.WindowSeconds).ToString();
        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RateLimiting")
            .LogWarning("Rate limit rejected {Method} {Path}", context.HttpContext.Request.Method, context.HttpContext.Request.Path);

        context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
        await context.HttpContext.Response.WriteAsync("Too many requests. Please wait a moment and try again.").ConfigureAwait(false);
    };

    static RateLimitPartition<string> Partition(HttpContext http, string policy, RateLimitRule rule)
    {
        var who = http.User.Identity?.IsAuthenticated == true
            ? http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "user"
            : http.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

        return RateLimitPartition.GetFixedWindowLimiter($"{policy}:{who}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, rule.PermitLimit),
            Window = TimeSpan.FromSeconds(Math.Max(1, rule.WindowSeconds)),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    }

    options.AddPolicy(RateLimitPolicies.Auth, http => Partition(http, RateLimitPolicies.Auth, rateLimits.Auth));
    options.AddPolicy(RateLimitPolicies.Checkout, http => Partition(http, RateLimitPolicies.Checkout, rateLimits.Checkout));
    options.AddPolicy(RateLimitPolicies.Review, http => Partition(http, RateLimitPolicies.Review, rateLimits.Review));
    options.AddPolicy(RateLimitPolicies.Messaging, http => Partition(http, RateLimitPolicies.Messaging, rateLimits.Messaging));
    options.AddPolicy(RateLimitPolicies.Webhook, http => Partition(http, RateLimitPolicies.Webhook, rateLimits.Webhook));
});

// ---------------------------------------------------------------------------
// Catalogue Providers (Local DB / Shopify Sync)
// ---------------------------------------------------------------------------
builder.Services.Configure<ShopifyOptions>(builder.Configuration.GetSection(ShopifyOptions.SectionName));
var shopifyOptions = builder.Configuration.GetSection(ShopifyOptions.SectionName).Get<ShopifyOptions>() ?? new ShopifyOptions();
shopifyOptions.Validate();
builder.Services.AddMemoryCache();

if (shopifyOptions.Enabled)
{
    if (shopifyOptions.UseMockData || !shopifyOptions.IsLive)
    {
        builder.Services.AddSingleton<IShopifyStorefrontClient, MockShopifyStorefrontClient>();
    }
    else
    {
        builder.Services.AddHttpClient<IShopifyStorefrontClient, ShopifyStorefrontClient>();
    }

    builder.Services.AddScoped<ShopifyCatalogService>();
    builder.Services.AddScoped<ICatalogService>(sp => sp.GetRequiredService<ShopifyCatalogService>());
}
else
{
    builder.Services.AddScoped<ICatalogService, CatalogService>();
}

// ---------------------------------------------------------------------------
// Payments
// ---------------------------------------------------------------------------
builder.Services.Configure<PaymentSettings>(builder.Configuration.GetSection(PaymentSettings.SectionName));
var paymentProvider = builder.Configuration.GetValue("Payments:Provider", "sandbox");

switch (paymentProvider?.Trim().ToLowerInvariant())
{
    case "sandbox":
        builder.Services.AddSingleton<IPaymentGateway, SandboxPaymentGateway>();
        break;

    case "simulator":
        // One instance, exposed as itself and as both interfaces, so signatures verify with the same secret.
        builder.Services.AddSingleton<SimulatorPaymentGateway>();
        builder.Services.AddSingleton<IPaymentGateway>(sp => sp.GetRequiredService<SimulatorPaymentGateway>());
        builder.Services.AddSingleton<IHostedPaymentGateway>(sp => sp.GetRequiredService<SimulatorPaymentGateway>());
        break;

    default:
        throw new InvalidOperationException($"Unknown payment provider '{paymentProvider}'.");
}

builder.Services.AddScoped<IPaymentWebhookHandler, PaymentWebhookHandler>();

// ---------------------------------------------------------------------------
// Image Asset Store Processing Thresholds
// ---------------------------------------------------------------------------
builder.Services.Configure<ImageStorageOptions>(builder.Configuration.GetSection(ImageStorageOptions.SectionName));
var imageOptions = builder.Configuration.GetSection(ImageStorageOptions.SectionName).Get<ImageStorageOptions>() ?? new ImageStorageOptions();
var uploadLimit = Math.Max(imageOptions.MaxBytes, (long)Math.Max(1, imageOptions.MaxPerProduct) * imageOptions.MaxBytes);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = uploadLimit);
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o => o.Limits.MaxRequestBodySize = uploadLimit);
builder.Services.AddSingleton<IImageStore, LocalImageStore>();

builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute());
});

var app = builder.Build();

var shippingOptions = builder.Configuration.GetSection(ShippingCarrierOptions.SectionName).Get<ShippingCarrierOptions>() ?? new ShippingCarrierOptions();
shippingOptions.Validate(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Shipping"));

// ---------------------------------------------------------------------------
// HTTP Context Execution Pipeline
// ---------------------------------------------------------------------------
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
else
{
    app.UseDeveloperExceptionPage();
}

app.UseStatusCodePagesWithReExecute("/Home/NotFound", "?code={0}");
app.UseHttpsRedirection();
app.UseStaticFiles();

var imageFolder = Path.IsPathRooted(imageOptions.Folder)
    ? imageOptions.Folder
    : Path.GetFullPath(imageOptions.Folder, app.Environment.ContentRootPath);
Directory.CreateDirectory(imageFolder);

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(imageFolder),
    RequestPath = "/" + imageOptions.RequestPath.Trim('/', '\\', ' ')
});

app.UseRouting();
app.UseSession();

// Multi-tenant theme resolution runs after routing so the shop slug is available.
app.UseTenantThemes();

app.UseAuthentication();
app.UseAuthorization();

if (rateLimits.Enabled)
{
    app.UseRateLimiter();
}

app.MapControllerRoute(name: "areas", pattern: "{area:exists}/{controller=Dashboard}/{action=Index}/{id?}");
app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");

// ---------------------------------------------------------------------------
// Runtime Relational Storage Bootstrapper
// ---------------------------------------------------------------------------
await using (var scope = app.Services.CreateAsyncScope())
{
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    try
    {
        var db = services.GetRequiredService<ApplicationDbContext>();
        var dataSource = db.Database.GetConnectionString();
        var providerName = db.Database.ProviderName ?? "unknown";

        if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(dataSource))
        {
            var file = dataSource.Split('=', 2).LastOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(file) && !file.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(file, app.Environment.ContentRootPath));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }
        }

        if (useSqlServer && app.Configuration.GetValue<bool>("Database:MigrateFromSqlite"))
        {
            var sourcePath = app.Configuration["Database:SqliteSource"] ?? "Data Source=App_Data/store.db";
            sourcePath = Path.IsPathRooted(sourcePath)
                ? sourcePath
                : Path.GetFullPath(sourcePath, app.Environment.ContentRootPath);

            if (File.Exists(sourcePath))
            {
                await SqliteToSqlServerMigrator.MigrateAsync(db, sourcePath, logger).ConfigureAwait(false);
            }
        }

        await DbSeeder.SeedAsync(db, services.GetRequiredService<IPasswordHasher>(), app.Configuration, logger).ConfigureAwait(false);

        // CONFIRM these two types: the original upload had lost the type names on these lines.
        await services.GetRequiredService<ITranslationStore>().EnsureLoadedAsync().ConfigureAwait(false);
        await services.GetRequiredService<ISiteSettingStore>().EnsureLoadedAsync().ConfigureAwait(false);

        await LogTable.EnsureAsync(db).ConfigureAwait(false);
        await EmailSchema.EnsureAsync(db).ConfigureAwait(false);

        logger.LogInformation("Store database ready at {Provider}", providerName);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database bootstrap failed");
        throw;
    }
}

app.MapGet("/set-language", (string code, HttpContext http) =>
{
    var options = http.RequestServices.GetRequiredService<IOptions<LanguageOptions>>().Value;
    if (options.Enabled && options.Supported.Any(s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase)))
    {
        http.Response.Cookies.Append("WebDS.Culture", code, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddYears(1)
        });
    }

    // Only redirect to a local path. LocalRedirect throws (HTTP 500) on an external URL.
    var target = http.Request.Query["returnUrl"].ToString();
    var isLocal = !string.IsNullOrWhiteSpace(target)
                  && target.StartsWith('/')
                  && !target.StartsWith("//", StringComparison.Ordinal)
                  && !target.StartsWith("/\\", StringComparison.Ordinal);

    return Results.LocalRedirect(isLocal ? target : "/");
});

app.Run();