using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services;
using WebDS_Test.Services.Localization;
using WebDS_Test.Services.Email;
using WebDS_Test.Services.Images;
using WebDS_Test.Services.Logging;
using WebDS_Test.Services.Shopify;
using WebDS_Test.Services.Shipping;

var builder = WebApplication.CreateBuilder(args);

// Machine-local secrets (SQL credentials, provider keys) live in appsettings.Local.json,
// which is gitignored — the committed appsettings files carry only empty placeholders so
// no credentials ever reach version control. The source is added here, after the built-in
// appsettings.json/appsettings.{Environment}.json, so anything defined in the Local file
// wins over them. On a machine without the file the app still boots: an empty SQL connection
// string falls back to the SQLite store (see the Database:Provider branch below).
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

// Logging to the Windows Event Log is not available to this process (the sandbox denies the
// Event Log source, and the provider turns any warning into a fatal AggregateException). The
// EventLog provider is therefore removed; console and debug logging are what this app uses
// directly — and, below, the store's own Log table.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// The database sink: everything the application logs is also written to the store's `Log` table,
// so a deployment leaves a diagnosis trail that outlives the console. Callers only format and
// enqueue — the write happens on a background service (Services/Logging) — and
// `Logging:Database:LogLevel` can filter the table independently of the console. Setting
// `Logging:Database:Enabled` to false removes the sink entirely.
var dbLogOptions = builder.Configuration.GetSection(DbLogOptions.SectionName).Get<DbLogOptions>()
                   ?? new DbLogOptions();

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
var storeSettings = builder.Configuration.GetSection(StoreSettings.SectionName).Get<StoreSettings>()
                    ?? new StoreSettings();

builder.Services.Configure<StoreSettings>(builder.Configuration.GetSection(StoreSettings.SectionName));

// Visual identity. Defaults reproduce the brand's own palette (see BrandingOptions); the
// "Branding" section overrides any token without touching CSS or markup.
builder.Services.Configure<BrandingOptions>(builder.Configuration.GetSection(BrandingOptions.SectionName));

// ---------------------------------------------------------------------------
// Data
//
// Two providers share one model. SQLite remains the development store (and the
// source of a one-time import); SQL Server ("MVStores") is the deployed store.
// The choice is configuration only — `Database:Provider` — so nothing in the
// application below the registration is provider-specific.
// ---------------------------------------------------------------------------
var sqliteConnectionString = builder.Configuration.GetConnectionString("DefaultConnection")
                             ?? "Data Source=App_Data/store.db";

var sqlServerConnectionString = builder.Configuration.GetConnectionString("MultiVendorsStoresConn");

var useSqlServer = builder.Configuration["Database:Provider"] is { } provider
                   && provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase)
                   && !string.IsNullOrWhiteSpace(sqlServerConnectionString);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
{
    // Several catalogue queries Include more than one collection (variants + images). Split
    // queries avoid the cartesian explosion that a single query would produce.
    if (useSqlServer)
    {
        options.UseSqlServer(
            sqlServerConnectionString!,
            sql => sql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
    }
    else
    {
        options.UseSqlite(
            sqliteConnectionString,
            sqlite => sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
    }

    // Surface the SQL actually issued; useful while the catalogue queries evolve.
    options.EnableSensitiveDataLogging(builder.Environment.IsDevelopment());
});

// Needed by CartService to resolve the current user and the anonymous session key.
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
// Authentication
//
// Cookie authentication backed by the local ApplicationUser table. This replaces the
// ASP.NET Core Identity stack because Microsoft.AspNetCore.Identity.EntityFrameworkCore is
// absent from the local NuGet cache and nuget.org is unreachable from this machine; the
// security-relevant parts (PBKDF2 hashing, lockout, security stamp) are implemented in
// Services/AccountService.cs and Services/PasswordHasher.cs.
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
    options.AddPolicy("VendorOnly", policy => policy.RequireAuthenticatedUser());
options.AddPolicy("CustomerOnly", policy => policy.RequireAuthenticatedUser());
});

// ---------------------------------------------------------------------------
// Application services
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<ISlugService, SlugService>();
// Storefront template themes are pure data, so one instance serves the whole process.
builder.Services.AddSingleton<IThemeCatalog, ThemeCatalog>();
builder.Services.AddScoped<ICartService, CartService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<IInventoryService, InventoryService>();
builder.Services.AddScoped<IShopService, ShopService>();
builder.Services.AddScoped<IFulfilmentService, FulfilmentService>();
builder.Services.AddScoped<IReviewService, ReviewService>();
builder.Services.AddScoped<ICouponService, CouponService>();

// Returns and the admin audit trail. ReturnService shares the payment gateway and email outbox with
// OrderService, so a refund through a return behaves like a refund through the order page.
builder.Services.AddScoped<IReturnService, ReturnService>();
builder.Services.AddScoped<IAuditTrail, AuditTrail>();

// Shopper <-> shop conversations. Shares the email outbox with the order and return services, so
// a reply on one side notifies the other the same way every other notification does.
builder.Services.AddScoped<IConversationService, ConversationService>();

// Bulk catalogue import. The CSV reader is the inverse of the export writer, so a file the store
// produced can be edited in a spreadsheet and handed straight back.
builder.Services.AddScoped<WebDS_Test.Services.Imports.IProductImportService, WebDS_Test.Services.Imports.ProductImportService>();

// Tax. Separate from the order service so the arithmetic is testable on its own, and so the checkout
// preview and the order that is actually placed cannot disagree about what is owed.
builder.Services.AddSingleton<ITaxCalculator, TaxCalculator>();

// Languages. The translator resolves strings from the database-backed catalogue (seeded from
// App_Data/i18n/*.txt), so adding a language is still a configuration entry and editing a string is
// an administrative action. Arabic is right-to-left and the layout flips for it.
builder.Services.Configure<LanguageOptions>(builder.Configuration.GetSection(LanguageOptions.SectionName));
builder.Services.AddHttpContextAccessor();
// The catalogue is cached application-wide so a Razor render is a dictionary lookup rather than a
// query per string. Registered before the translator, which reads it per lookup.
builder.Services.AddSingleton<ITranslationStore, TranslationStore>();
builder.Services.AddScoped<ITranslator, Translator>();

// Vendors are settled through an append-only ledger. The writer is shared by checkout, fulfilment
// and refunds so a sale is credited exactly once from wherever the money is actually collected.
builder.Services.AddScoped<IVendorLedgerService, VendorLedgerService>();
builder.Services.AddScoped<IPayoutService, PayoutService>();
builder.Services.Configure<PayoutOptions>(builder.Configuration.GetSection(PayoutOptions.SectionName));

// Store-wide settings live in the database and are cached application-wide, so the admin
// settings screen can change them (today: the USD/LBP display rate) without a deployment.
builder.Services.AddSingleton<ISiteSettingStore, SiteSettingStore>();
// Formatting money happens on every Razor render, so it must be synchronous and allocation-light;
// it reads the cached settings per call, which is what makes an admin's rate change live instantly.
builder.Services.AddSingleton<ICurrencyFormatter, CurrencyFormatter>();

// Review policy (auto-publish, seller-badge thresholds) lives in configuration, so the
// marketplace's rules can be tuned without a deployment.
builder.Services.Configure<ReviewSettings>(builder.Configuration.GetSection(ReviewSettings.SectionName));

builder.Services.Configure<ShippingCarrierOptions>(builder.Configuration.GetSection(ShippingCarrierOptions.SectionName));

// Both carriers are always registered. The simulator needs no credentials and stays the default;
// LibanPost is selected the moment Shipping:Provider says "libanpost" with BaseUrl and ApiKey
// supplied. Registering the real client here is what lets the registry resolve it at all —
// without this line Shipping:Provider could never pick it, however the configuration read.
builder.Services.AddSingleton<IShippingCarrier, SimulatorShippingCarrier>();
builder.Services.AddHttpClient<LibanPostCarrier>();
builder.Services.AddSingleton<IShippingCarrier>(sp => sp.GetRequiredService<LibanPostCarrier>());
builder.Services.AddSingleton<ICarrierRegistry, CarrierRegistry>();

// ---------------------------------------------------------------------------
// Transactional email
//
// Nothing sends mail inline. A request queues a row in the store's own outbox and returns; a
// background dispatcher delivers it. The transport is configuration only — "File" (the default,
// which writes .eml files and needs no mail server) or "Smtp". Data Protection backs the
// password-reset tokens, so it is registered explicitly rather than relied upon implicitly.
// ---------------------------------------------------------------------------
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));

var emailOptions = builder.Configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>()
                   ?? new EmailOptions();

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

// ---------------------------------------------------------------------------
// Rate limiting
//
// Fixed-window limits on the endpoints worth protecting: sign-in and password reset (brute force),
// checkout and review posting. Partitioned per signed-in user, or per IP address for guests. The
// limiter ships with the ASP.NET Core shared framework, so this needs no package.
// ---------------------------------------------------------------------------
var rateLimits = builder.Configuration.GetSection(RateLimitSettings.SectionName).Get<RateLimitSettings>()
                 ?? new RateLimitSettings();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.OnRejected = async (context, _) =>
    {
        // Writing a body also stops UseStatusCodePagesWithReExecute from replacing the 429 with the
        // ordinary not-found page: the caller gets the throttle response it should.
        context.HttpContext.Response.Headers["Retry-After"] =
            Math.Max(1, rateLimits.Auth.WindowSeconds).ToString();

        context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("RateLimiting")
            .LogWarning("Rate limit rejected {Method} {Path}",
                context.HttpContext.Request.Method, context.HttpContext.Request.Path);

        context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
        await context.HttpContext.Response.WriteAsync(
            "Too many requests. Please wait a moment and try again.");
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
// Catalogue provider
//
// The catalogue is the one part of the app that can be served either from the local
// database or from Shopify's Storefront API. Everything else (cart, checkout, orders,
// inventory) stays on the local implementation, which is why this is a single
// registration swap rather than a rewrite.
// ---------------------------------------------------------------------------
builder.Services.Configure<ShopifyOptions>(builder.Configuration.GetSection(ShopifyOptions.SectionName));

var shopifyOptions = builder.Configuration.GetSection(ShopifyOptions.SectionName).Get<ShopifyOptions>() ?? new ShopifyOptions();
shopifyOptions.Validate();

// Catalogue lookups that would otherwise cost an API call per request (brands, price bounds,
// collections) are cached here.
builder.Services.AddMemoryCache();

if (shopifyOptions.Enabled)
{
    if (shopifyOptions.UseMockData || !shopifyOptions.IsLive)
    {
        // No credentials yet: serve genuine Shopify response shapes from a local fixture so the
        // adapter, the search-syntax translator and the paging walk are all exercised for real.
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
// Payment provider: swap this registration for a real gateway implementation.
//   sandbox   — charges nothing and has no hosted page; the store runs on cash on delivery.
//   simulator — adds the redirect-and-webhook card flow, exercised entirely in-process.
// A production provider is the same shape as the simulator: one IPaymentGateway (and, when it
// hosts the payment page, one IHostedPaymentGateway) plus its own case here.
// ---------------------------------------------------------------------------
builder.Services.Configure<PaymentSettings>(builder.Configuration.GetSection(PaymentSettings.SectionName));

var paymentProvider = builder.Configuration.GetValue("Payments:Provider", "sandbox");

switch (paymentProvider?.Trim().ToLowerInvariant())
{
    case "sandbox":
        builder.Services.AddSingleton<IPaymentGateway, SandboxPaymentGateway>();
        break;

    case "simulator":
        // One instance, exposed as itself and as both interfaces, so the webhook handler verifies
        // signatures with the same secret the payment page signs them with.
        builder.Services.AddSingleton<SimulatorPaymentGateway>();
        builder.Services.AddSingleton<IPaymentGateway>(sp => sp.GetRequiredService<SimulatorPaymentGateway>());
        builder.Services.AddSingleton<IHostedPaymentGateway>(sp => sp.GetRequiredService<SimulatorPaymentGateway>());
        break;

    default:
        throw new InvalidOperationException(
            $"Unknown payment provider '{paymentProvider}'. Use 'sandbox' or 'simulator', or register an " +
            "IPaymentGateway (and IHostedPaymentGateway) implementation of your own before selecting another provider.");
}

builder.Services.AddScoped<IPaymentWebhookHandler, PaymentWebhookHandler>();

// ---------------------------------------------------------------------------
// Product image uploads
//
// The store writes uploads to a folder of its own and serves them as static files, so a product
// image is a URL like any other and the catalogue never has to know where the bytes came from.
// Swapping in blob storage later means one new IImageStore registration.
// ---------------------------------------------------------------------------
builder.Services.Configure<ImageStorageOptions>(builder.Configuration.GetSection(ImageStorageOptions.SectionName));

var imageOptions = builder.Configuration.GetSection(ImageStorageOptions.SectionName).Get<ImageStorageOptions>()
                   ?? new ImageStorageOptions();

// A whole gallery must fit through the pipeline in one request, so the body limits are raised to
// "every allowed file at its maximum size" rather than left at the defaults (which are smaller).
var uploadLimit = Math.Max(imageOptions.MaxBytes, (long)Math.Max(1, imageOptions.MaxPerProduct) * imageOptions.MaxBytes);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = uploadLimit);
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o => o.Limits.MaxRequestBodySize = uploadLimit);

builder.Services.AddSingleton<IImageStore, LocalImageStore>();

builder.Services.AddControllersWithViews(options =>
{
    // Every unsafe verb must carry a valid antiforgery token; checkout and cart posts included.
    options.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute());
});

var app = builder.Build();

// Fail fast when Shipping:Provider is "libanpost" but the credentials are missing: fulfilment
// would otherwise look like it worked while no consignment was ever booked. An unknown provider
// name only warns — the carrier registry falls back to the simulator.
(builder.Configuration.GetSection(ShippingCarrierOptions.SectionName).Get<ShippingCarrierOptions>()
 ?? new ShippingCarrierOptions())
 .Validate(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Shipping"));

// ---------------------------------------------------------------------------
// Pipeline
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

// Uploaded product images live outside wwwroot, under the content root, so they survive a rebuild
// of the site files. This serves exactly that folder at the configured request path (default
// /uploads) and nothing else: a request for any other path still falls through to the app.
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
app.UseAuthentication();
app.UseAuthorization();

// After authentication so the limiter can partition on the signed-in user when there is one, and
// only when enabled — the policies stay registered either way, so the controller attributes are
// always satisfiable.
if (rateLimits.Enabled)
{
    app.UseRateLimiter();
}

app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Dashboard}/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// ---------------------------------------------------------------------------
// Database bootstrap: create the store's schema, import the SQLite development
// store once, then seed whatever is still missing.
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

        // Only a SQLite store needs a folder on disk. Parsing a connection string as a file path
        // is meaningless for SQL Server (it would happily create a directory literally named
        // ".;Initial Catalog=MVStores;…"), so this branch is deliberately provider-gated.
        if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(dataSource))
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

        // One-time import of the SQLite store into SQL Server. Self-skips when the target already
        // holds data, so leaving it enabled is harmless after the first successful run.
        if (useSqlServer && app.Configuration.GetValue<bool>("Database:MigrateFromSqlite"))
        {
            var sourcePath = app.Configuration["Database:SqliteSource"] ?? "Data Source=App_Data/store.db";
            sourcePath = Path.IsPathRooted(sourcePath)
                ? sourcePath
                : Path.GetFullPath(sourcePath, app.Environment.ContentRootPath);

            if (File.Exists(sourcePath))
            {
                await SqliteToSqlServerMigrator.MigrateAsync(db, sourcePath, logger);
            }
            else
            {
                logger.LogWarning(
                    "Database:MigrateFromSqlite is enabled but {Path} was not found; no rows were imported.",
                    sourcePath);
            }
        }

        await DbSeeder.SeedAsync(
            db,
            services.GetRequiredService<IPasswordHasher>(),
            app.Configuration,
            logger);

        // Prime the settings cache after the schema exists, so the first storefront render is a
        // dictionary lookup rather than a database hit. Reads fall back to configuration if this
        // step is ever skipped, so warming is an optimisation, not a correctness requirement.
        await services.GetRequiredService<ISiteSettingStore>().EnsureLoadedAsync();

        // The translation catalogue is warmed for the same reason, and it matters a little more:
        // until it is loaded the translator falls back to the shipped text files, so warming it is
        // what makes an administrator's edit the value the storefront actually serves.
        await services.GetRequiredService<ITranslationStore>().EnsureLoadedAsync();

        // The sink writes to the Log table. EnsureCreated builds the whole schema only when the
        // store is empty, so this covers a store that predates the sink — the SQLite development
        // file above all — and logging can never quietly stop working.
        await LogTable.EnsureAsync(db);

        // The transactional outbox postdates the store in both dialects for the same reason, so it
        // gets the same guarded create-on-demand treatment before the dispatcher starts on it.
        await EmailSchema.EnsureAsync(db);

        logger.LogInformation("Store database ready at {Provider}: {ConnectionString}", providerName, dataSource);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database bootstrap failed");
        throw;
    }
}

// Remembers the shopper's language choice. Written as a cookie rather than through a redirect chain so
// switching language does not add a history entry the back button has to walk back through.
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

    // Only local paths, so the switcher cannot be used as an open redirect.
    var target = http.Request.Query["returnUrl"].ToString();
    return Results.LocalRedirect(string.IsNullOrWhiteSpace(target) ? "/" : target);
});

app.Run();
