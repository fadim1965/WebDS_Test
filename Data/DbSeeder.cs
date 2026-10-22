using Microsoft.EntityFrameworkCore;
using WebDS_Test.Domain;

namespace WebDS_Test.Data;

/// <summary>
/// Ensures the schema exists for whichever provider is configured — SQLite by default, SQL Server
/// when <c>Database:Provider</c> selects it — and then populates a demonstrable catalogue, two
/// test accounts and a sample order. Every step is guarded, so a store that already holds rows is
/// left untouched. Schema creation uses <c>EnsureCreated</c> rather than migrations because
/// <c>Microsoft.EntityFrameworkCore.Design</c> is not present in the local NuGet cache on this
/// machine and nuget.org is unreachable — see docs/ECOMMERCE_PLAN.md.
/// </summary>
/// <remarks>
/// The seeded catalogue is the brand's own domain — postal services, mailing supplies and
/// philately — so the storefront content matches the storefront identity. It is replaced
/// wholesale by the Shopify catalogue provider when <c>Shopify:Enabled</c> is true.
/// </remarks>
public static class DbSeeder
{
    public const string DefaultAdminEmail = "admin@libanpost.example";
    public const string DefaultAdminPassword = "Admin#12345";
    public const string DefaultCustomerEmail = "customer@libanpost.example";
    public const string DefaultCustomerPassword = "Customer#123";

    public static async Task SeedAsync(
        ApplicationDbContext db,
        IPasswordHasher hasher,
        IConfiguration config,
        ILogger logger,
        CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);

        // EnsureCreated builds the whole schema only for an empty database, so a store created
        // before the review tables existed needs them added on demand — before anything tries to
        // seed into them. The same guard covers the coupon tables and the columns they add to Orders.
        await ReviewSchema.EnsureAsync(db, ct);
        await CouponSchema.EnsureAsync(db, ct);

        // The payment-webhook trail postdates the store too; the webhook endpoint writes to it from
        // the first delivery, so it must exist before the app starts serving.
        await PaymentSchema.EnsureAsync(db, ct);

        // The vendor money tables — the settlement ledger and its payouts — likewise postdate the
        // store, and a payout query must not fail on a database built before they were modelled.
        await VendorSchema.EnsureAsync(db, ct);

        // Tax classes and the product's optional pointer to one. Same reasoning: the table and the
        // column postdate the store, and checkout reads the column on every order.
        await VendorSchema.EnsureTaxAsync(db, ct);

        // Return requests postdate the store, and with them the ledger column a partial reversal
        // is idempotent on and the order column every refund path adds to.
        await ReturnsSchema.EnsureAsync(db, ct);

        // Payouts gained a lifecycle (failed / reversed) and shops gained payout-destination
        // fields after the payouts table existed; a payout query must not fail on an older store.
        await PayoutStatusSchema.EnsureAsync(db, ct);

        // The admin audit trail postdates the store too. It is written from the first
        // administrative action, so it must exist before the app starts serving.
        await AuditSchema.EnsureAsync(db, ct);

        // Customer <-> vendor conversations postdate the store as well; the inbox and the vendor
        // worklist both query them from the first page load.
        await ConversationSchema.EnsureAsync(db, ct);

        // Runtime settings (the USD/LBP display rate and its labels) postdate the store too; the
        // admin settings screen and the currency formatter read the table on every request.
        await SettingsSchema.EnsureAsync(db, ct);

        // Editable translations postdate the store as well. The table must exist before the shipped
        // catalogues are copied into it, and before the translator queries it on the first request.
        await TranslationSchema.EnsureAsync(db, ct);
        await TranslationSeeder.SeedAsync(db, ct);

        // Listing moderation postdates the store: the review columns sit on Products, which already
        // exists, so they are added on demand before anything reads them.
        await ListingSchema.EnsureAsync(db, ct);

        // One standard-rate class so the feature is usable and visible on a fresh store. Products are
        // left unassigned on purpose: an unassigned product falls back to Store:TaxRate, which is the
        // behaviour every order had before this feature existed.
        if (!await db.TaxClasses.AnyAsync(ct))
        {
            db.TaxClasses.Add(new TaxClass
            {
                Name = "Standard rate",
                Rate = 0.11m,
                IsActive = true,
                CreatedUtc = DateTime.UtcNow
            });

            await db.SaveChangesAsync(ct);
        }

        // The marketplace needs at least one trading shop to own the seeded catalogue, and that
        // shop needs an owner account first.
        await SeedUsersAsync(db, hasher, config, logger, ct);
        await SeedShopAsync(db, logger, ct);
        await SeedCategoriesAsync(db, ct);
        foreach (var category in await db.Categories.ToListAsync(ct))
        {
            category.IsGlobal = true;
        }
        await db.SaveChangesAsync(ct);
        await SeedProductsAsync(db, ct);
        await SeedListingsAsync(db, ct);
        await SeedSampleOrderAsync(db, logger, ct);
        await SeedReviewsAsync(db, logger, ct);
        await SeedCouponsAsync(db, logger, ct);
    }

    // ------------------------------------------------------------------
    // Catalogue
    // ------------------------------------------------------------------

    private static async Task SeedCategoriesAsync(ApplicationDbContext db, CancellationToken ct)
    {
        if (await db.Categories.AnyAsync(ct))
        {
            return;
        }

        var mailing = new Category { Name = "Mailing & Parcels", Slug = "mailing-and-parcels", DisplayOrder = 1, Description = "Letters, parcels and registered mail." };
        var express = new Category { Name = "Express Mail", Slug = "express-mail", DisplayOrder = 2, Description = "EMS and PostXpress time-critical delivery." };
        var philately = new Category { Name = "Philately & Stamps", Slug = "philately", DisplayOrder = 3, Description = "Commemorative issues, first day covers and collector albums." };
        var packaging = new Category { Name = "Packaging", Slug = "packaging", DisplayOrder = 4, Description = "Boxes, mailers and protective wrapping." };
        var pobox = new Category { Name = "P.O. Box Services", Slug = "p-o-box", DisplayOrder = 5, Description = "Annual and quarterly post office box rental." };
        var supplies = new Category { Name = "Office Supplies", Slug = "office-supplies", DisplayOrder = 6, Description = "Counter consumables, labels and stationery." };

        db.Categories.AddRange(mailing, express, philately, packaging, pobox, supplies);
        await db.SaveChangesAsync(ct);

        db.Categories.AddRange(
            new Category { Name = "Envelopes & Mailers", Slug = "envelopes-mailers", DisplayOrder = 1, ParentId = mailing.Id, Description = "Pre-paid and plain envelopes." },
            new Category { Name = "Registered Mail", Slug = "registered-mail", DisplayOrder = 2, ParentId = mailing.Id, Description = "Tamper-evident and tracked mail." },
            new Category { Name = "EMS", Slug = "ems", DisplayOrder = 1, ParentId = express.Id, Description = "International express, next-day where available." },
            new Category { Name = "PostXpress", Slug = "postxpress", DisplayOrder = 2, ParentId = express.Id, Description = "Same-day domestic consignments." },
            new Category { Name = "Commemorative Issues", Slug = "commemorative-issues", DisplayOrder = 1, ParentId = philately.Id, Description = "Limited and themed stamp issues." },
            new Category { Name = "Albums & Accessories", Slug = "albums-accessories", DisplayOrder = 2, ParentId = philately.Id, Description = "Binders, pages and handling tools." },
            new Category { Name = "Boxes", Slug = "boxes", DisplayOrder = 1, ParentId = packaging.Id, Description = "Single and double-wall shipping boxes." },
            new Category { Name = "Protective Wrapping", Slug = "protective-wrapping", DisplayOrder = 2, ParentId = packaging.Id, Description = "Bubble wrap, void fill and tape." },
            new Category { Name = "Counter Consumables", Slug = "counter-consumables", DisplayOrder = 1, ParentId = supplies.Id, Description = "Labels, rolls and dispensers." });

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedProductsAsync(ApplicationDbContext db, CancellationToken ct)
    {
        if (await db.Products.AnyAsync(ct))
        {
            return;
        }

        var categories = await db.Categories.ToDictionaryAsync(c => c.Slug, ct);

        int Cat(string slug) => categories[slug].Id;

        var products = new List<Product>
        {
            Build("EMS Priority Envelope — Pack of 10", "ems", Cat("ems"), "ems-priority-envelope-pack-of-10",
                "Pre-paid express envelope with tracking included, sized for A4 documents.",
                24.50m, 29.00m, featured: true, brand: "LibanPost", description: "Ten pre-paid EMS envelopes with tracking included. Sized for A4 documents and ready for counter drop-off. The adhesive flap is tamper-evident, and the tracking barcode is pre-printed so the envelope can be lodged without a queue ticket.",
                variants: new[] { ("LP-EMS-ENV-10", "Pack of 10", 24.50m, 148), ("LP-EMS-ENV-25", "Pack of 25", 54.00m, 62) }),

            Build("EMS Priority Envelope — Pack of 25", "ems", Cat("ems"), "ems-priority-envelope-pack-of-25",
                "Bulk pack of tracked express envelopes for regular senders.",
                54.00m, 62.00m, brand: "LibanPost", description: "The same pre-paid tracked envelope in a bulk pack, for businesses sending express mail weekly.",
                variants: new[] { ("LP-EMS-ENV-25B", "Pack of 25", 54.00m, 62) }),

            Build("Registered Mail Kit", "registered-mail", Cat("registered-mail"), "registered-mail-kit",
                "Tamper-evident wallet, seals and a tracking log for registered mail.",
                18.75m, null, featured: true, brand: "LibanPost", description: "Everything needed to lodge registered mail: a tamper-evident wallet, numbered seals, a duplicate tracking log and a pen. The wallet takes A4 folded once.",
                variants: new[] { ("LP-REG-KIT", "Complete kit", 18.75m, 210) }),

            Build("PostXpress Same-Day Label", "postxpress", Cat("postxpress"), "postxpress-same-day-label",
                "Same-day domestic consignment label with SMS tracking.",
                9.75m, null, brand: "LibanPost", description: "Purchased per consignment at the counter. The label carries a barcode linked to SMS tracking, so the recipient is notified at dispatch and on delivery.",
                variants: new[] { ("LP-PX-SD-1", "Single consignment", 9.75m, 400), ("LP-PX-SD-10", "Sheet of 10", 89.00m, 74) }),

            Build("Cedar Tree Commemorative Sheet", "commemorative-issues", Cat("commemorative-issues"), "cedar-tree-commemorative-sheet",
                "Limited commemorative sheet in a numbered presentation folder.",
                12.00m, null, featured: true, brand: "LibanPost Philately", description: "A limited commemorative sheet celebrating the Lebanese cedar, issued in a numbered presentation folder with a certificate of authenticity. Printed in six colours with a metallic overprint.",
                variants: new[] { ("LP-STMP-CEDAR-M", "Mint", 12.00m, 540), ("LP-STMP-CEDAR-FDC", "First day cover", 19.50m, 96) }),

            Build("Byblos Heritage Issue", "commemorative-issues", Cat("commemorative-issues"), "byblos-heritage-issue",
                "Four-stamp issue depicting the archaeological site of Byblos.",
                9.00m, 11.00m, brand: "LibanPost Philately", description: "A four-stamp issue depicting the archaeological site of Byblos, available as a mint set or on a first day cover cancelled at the Byblos post office.",
                variants: new[] { ("LP-STMP-BYBLOS-M", "Mint set", 9.00m, 380), ("LP-STMP-BYBLOS-FDC", "First day cover", 14.50m, 4) }),

            Build("Philatelic Album — Starter", "albums-accessories", Cat("albums-accessories"), "philatelic-album-starter",
                "Ring binder with 40 acid-free pages, interleaves and a slipcase.",
                44.00m, null, brand: "LibanPost Philately", description: "A ring binder with forty acid-free pages, interleaves and a slipcase. The pages take standard mounting strips and the spine is wide enough to hold around eight hundred stamps.",
                variants: new[] { ("LP-STMP-ALB-01", "Starter album", 44.00m, 27) }),

            Build("Stamp Tongs & Magnifier Set", "albums-accessories", Cat("albums-accessories"), "stamp-tongs-magnifier-set",
                "Nickel tongs and a 10× loupe for handling mint stamps.",
                16.50m, null, brand: "LibanPost Philately", description: "Nickel-plated tongs with a smooth grip and a 10× loupe, so mint stamps can be handled and inspected without touching gum.",
                variants: new[] { ("LP-STMP-TONG-01", "Tongs + loupe", 16.50m, 63) }),

            Build("Double-Wall Shipping Box — Medium", "boxes", Cat("boxes"), "double-wall-shipping-box-medium",
                "Rigid double-wall board rated for 15 kg, flat-packed with a self-seal strip.",
                32.00m, null, featured: true, brand: "LibanPost", description: "Rigid double-wall corrugated board rated for 15 kg. Flat-packed in bundles with a self-seal strip and a tear strip for the recipient. Internal dimensions 400 × 300 × 200 mm.",
                variants: new[] { ("LP-BOX-MD-10", "Bundle of 10", 32.00m, 84), ("LP-BOX-MD-25", "Bundle of 25", 71.50m, 24), ("LP-BOX-MD-200", "Pallet of 200", 520.00m, 0) }),

            Build("Single-Wall Shipping Box — Small", "boxes", Cat("boxes"), "single-wall-shipping-box-small",
                "Lightweight box for documents and low-value goods.",
                19.75m, 23.00m, brand: "LibanPost", description: "Single-wall board for documents and light goods. Internal dimensions 300 × 220 × 120 mm, supplied flat in bundles of ten.",
                variants: new[] { ("LP-BOX-SM-10", "Bundle of 10", 19.75m, 130) }),

            Build("Padded Mailer — A4", "envelopes-mailers", Cat("envelopes-mailers"), "padded-mailer-a4",
                "Bubble-lined mailer with a peel-and-seal closure.",
                15.25m, null, brand: "LibanPost", description: "A lightweight bubble-lined mailer for small fragile items, with a peel-and-seal closure and a tear-open strip. Takes A4 without folding.",
                variants: new[] { ("LP-MLR-A4-50", "Pack of 50", 15.25m, 320), ("LP-MLR-A5-50", "A5, pack of 50", 11.40m, 245) }),

            Build("Bubble Wrap Roll — 500 mm × 50 m", "protective-wrapping", Cat("protective-wrapping"), "bubble-wrap-roll-500mm",
                "Standard-duty bubble wrap for parcels that need cushioning.",
                28.60m, null, brand: "LibanPost Supplies", description: "Standard-duty bubble wrap on a 50 m roll, 500 mm wide. Perforated every 300 mm so it can be torn by hand at the packing bench.",
                variants: new[] { ("LP-SUP-BW-500", "Single roll", 28.60m, 41) }),

            Build("Kraft Paper Tape — 50 mm", "protective-wrapping", Cat("protective-wrapping"), "kraft-paper-tape-50mm",
                "Water-activated paper tape that bonds into the board.",
                12.90m, 15.00m, brand: "LibanPost Supplies", description: "Water-activated kraft tape reinforced with glass filaments. Bonds into the board itself, so a sealed parcel cannot be opened and resealed without evidence.",
                variants: new[] { ("LP-SUP-KT-50", "Single roll", 12.90m, 88), ("LP-SUP-KT-50C", "Case of 8", 92.00m, 2) }),

            Build("Thermal Label Rolls — 4×6 in", "counter-consumables", Cat("counter-consumables"), "thermal-label-rolls-4x6",
                "Direct-thermal labels for parcel and courier printers.",
                21.40m, null, featured: true, brand: "LibanPost Supplies", description: "Direct-thermal labels for parcel and courier printers, BPA-free and usable without a ribbon. Each roll holds 250 labels on a 25 mm core.",
                variants: new[] { ("LP-SUP-TL-250", "Roll of 250", 21.40m, 175), ("LP-SUP-TL-12", "Case of 12 rolls", 232.00m, 9) }),

            Build("Counter Tape Dispenser", "counter-consumables", Cat("counter-consumables"), "counter-tape-dispenser",
                "Weighted desktop dispenser that takes 50 mm tape.",
                27.90m, 34.00m, brand: "LibanPost Supplies", description: "A weighted desktop dispenser that takes 50 mm tape and stays put, leaving one hand free to hold the parcel. The blade is shielded.",
                variants: new[] { ("LP-SUP-TD-01", "Standard", 27.90m, 58) }),

            Build("Fragile Handling Labels", "counter-consumables", Cat("counter-consumables"), "fragile-handling-labels",
                "High-contrast handling labels that survive sorting machinery.",
                6.50m, null, brand: "LibanPost Supplies", description: "High-contrast handling labels printed on stock that survives sorting machinery and humidity. Supplied as sheets of twenty-four.",
                variants: new[] { ("LP-SUP-FR-24", "Sheet of 24", 6.50m, 610) }),

            Build("P.O. Box — Annual, Standard", "p-o-box", Cat("p-o-box"), "po-box-annual-standard",
                "Twelve months of standard-size P.O. Box rental.",
                120.00m, null, brand: "LibanPost", description: "Twelve months of standard-size P.O. Box rental at a participating branch, activated at the counter. Includes a key set and a change-of-address notification for the first month.",
                variants: new[] { ("LP-POB-STD-YR", "Standard", 120.00m, 45), ("LP-POB-LRG-YR", "Large", 210.00m, 12) }),

            Build("P.O. Box — Quarterly, Standard", "p-o-box", Cat("p-o-box"), "po-box-quarterly-standard",
                "A three-month P.O. Box rental for short-term use.",
                38.00m, 45.00m, brand: "LibanPost", description: "A three-month rental of a standard P.O. Box, useful for a temporary address or a short project. Renewable at the counter.",
                variants: new[] { ("LP-POB-STD-Q", "Quarterly", 38.00m, 61) })
        };

        var shop = await db.Shops.FirstOrDefaultAsync(ct);
        if (shop is not null)
        {
            foreach (var product in products)
            {
                product.ShopId = shop.Id;
            }
        }

        db.Products.AddRange(products);
        await db.SaveChangesAsync(ct);
    }
    /// <summary>
    /// Assembles a seeded product with its image gallery. Images are inline SVG data URIs so the
    /// storefront renders identically with no network access and no bundled binary assets.
    /// </summary>
    private static Product Build(
        string name,
        string categorySlug,
        int categoryId,
        string slug,
        string shortDescription,
        decimal price,
        decimal? compareAtPrice,
        bool featured = false,
        string? brand = null,
        string? description = null,
        (string Sku, string VariantName, decimal Price, int Stock)[]? variants = null)
    {
        var product = new Product
        {
            Name = name,
            Slug = slug,
            ShortDescription = shortDescription,
            Description = description,
            Brand = brand,
            CategoryId = categoryId,
            IsPublished = true,
            IsFeatured = featured,
            CreatedUtc = DateTime.UtcNow.AddDays(-System.Random.Shared.Next(20, 400)),
            UpdatedUtc = DateTime.UtcNow.AddDays(-System.Random.Shared.Next(1, 19))
        };

        var specs = variants is { Length: > 0 }
            ? variants
            : new[] { ($"SKU-{slug.ToUpperInvariant().Replace("-", string.Empty)[..Math.Min(12, slug.Replace("-", string.Empty).Length)]}", string.Empty, price, 25) };

        for (var i = 0; i < specs.Length; i++)
        {
            var (sku, variantName, variantPrice, stock) = specs[i];
            product.Variants.Add(new ProductVariant
            {
                Sku = sku,
                Name = variantName,
                Price = variantPrice,
                // The first variant carries the promotional comparison price and the section
                // colour band is derived from the category slug.
                CompareAtPrice = i == 0 ? compareAtPrice : null,
                StockQuantity = stock,
                IsActive = true,
                LowStockThreshold = 5
            });
        }

        var primary = PlaceholderImage(name, categorySlug, 0);
        product.Images.Add(new ProductImage { Url = primary, AltText = name, DisplayOrder = 0, IsPrimary = true });
        product.Images.Add(new ProductImage { Url = PlaceholderImage(name, categorySlug, 1), AltText = $"{name} — alternate view", DisplayOrder = 1 });
        product.Images.Add(new ProductImage { Url = PlaceholderImage(name, categorySlug, 2), AltText = $"{name} — detail", DisplayOrder = 2 });

        return product;
    }

    /// <summary>
    /// Colour bands for the generated placeholder images, keyed by catalogue section. The pairs
    /// are drawn from the brand palette so seeded imagery sits inside the visual identity.
    /// </summary>
    private static readonly Dictionary<string, (string From, string To)> CategoryPalette = new()
    {
        ["mailing-and-parcels"] = ("#015a97", "#2683c2"),
        ["envelopes-mailers"] = ("#0b5f98", "#4695cb"),
        ["registered-mail"] = ("#01538b", "#2683c2"),
        ["express-mail"] = ("#006db7", "#3297fd"),
        ["ems"] = ("#006db7", "#3297fd"),
        ["postxpress"] = ("#0d7dca", "#2683c2"),
        ["philately"] = ("#8a6d00", "#fed300"),
        ["commemorative-issues"] = ("#8a6d00", "#fed300"),
        ["albums-accessories"] = ("#2a4c62", "#6b8fa3"),
        ["packaging"] = ("#015a97", "#0776c1"),
        ["boxes"] = ("#015a97", "#0776c1"),
        ["protective-wrapping"] = ("#2581c0", "#6b8fa3"),
        ["p-o-box"] = ("#2a4c62", "#006db7"),
        ["office-supplies"] = ("#006bba", "#4695cb"),
        ["counter-consumables"] = ("#006bba", "#4695cb")
    };
    internal static string PlaceholderImage(string name, string categorySlug, int variantIndex)
    {
        var (from, to) = CategoryPalette.TryGetValue(categorySlug, out var palette)
            ? palette
            : ("#334155", "#64748b");

        if (variantIndex == 1)
        {
            (from, to) = (to, from);
        }
        else if (variantIndex >= 2)
        {
            (from, to) = (Darken(from), Darken(to));
        }

        var initials = string.Concat(name
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 0 && char.IsLetterOrDigit(w[0]))
            .Take(2)
            .Select(w => char.ToUpperInvariant(w[0])));

        var label = Escape(name.Length > 26 ? name[..26] + "…" : name);
        var angle = 20 + (variantIndex * 35);

        var svg =
            $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 600 600" width="600" height="600" role="img" aria-label="{label}">
              <defs>
                <linearGradient id="g" gradientTransform="rotate({angle})">
                  <stop offset="0%" stop-color="{from}"/>
                  <stop offset="100%" stop-color="{to}"/>
                </linearGradient>
              </defs>
              <rect width="600" height="600" fill="url(#g)"/>
              <circle cx="470" cy="130" r="150" fill="#ffffff" opacity="0.08"/>
              <circle cx="120" cy="500" r="190" fill="#000000" opacity="0.08"/>
              <text x="300" y="330" text-anchor="middle" font-family="Segoe UI, Helvetica, Arial, sans-serif" font-size="180" font-weight="700" fill="#ffffff" opacity="0.92">{initials}</text>
              <text x="300" y="420" text-anchor="middle" font-family="Segoe UI, Helvetica, Arial, sans-serif" font-size="26" fill="#ffffff" opacity="0.75">{label}</text>
            </svg>
            """;

        return "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
    }

    private static string Darken(string hex)
    {
        if (hex.Length != 7 || hex[0] != '#')
        {
            return hex;
        }

        var r = (int)(Convert.ToInt32(hex.Substring(1, 2), 16) * 0.72);
        var g = (int)(Convert.ToInt32(hex.Substring(3, 2), 16) * 0.72);
        var bl = (int)(Convert.ToInt32(hex.Substring(5, 2), 16) * 0.72);
        return $"#{r:X2}{g:X2}{bl:X2}";
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // ------------------------------------------------------------------
    // Accounts
    // ------------------------------------------------------------------

    private static async Task SeedUsersAsync(
        ApplicationDbContext db, IPasswordHasher hasher, IConfiguration config,
        ILogger logger, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(ct))
        {
            return;
        }

        var adminEmail = config["Seed:AdminEmail"] ?? DefaultAdminEmail;
        var adminPassword = config["Seed:AdminPassword"] ?? DefaultAdminPassword;

        var admin = new ApplicationUser
        {
            Email = adminEmail,
            NormalizedEmail = adminEmail.ToUpperInvariant(),
            DisplayName = "Store Administrator",
            PasswordHash = hasher.Hash(adminPassword),
            IsActive = true,
            CreatedUtc = DateTime.UtcNow
        };
        admin.UserRoles.Add(new UserRole { UserId = admin.Id, RoleName = AccountService.AdminRole });
        admin.UserRoles.Add(new UserRole { UserId = admin.Id, RoleName = AccountService.CustomerRole });

        var customer = new ApplicationUser
        {
            Email = DefaultCustomerEmail,
            NormalizedEmail = DefaultCustomerEmail.ToUpperInvariant(),
            DisplayName = "Dana Whitfield",
            Phone = "+1 555 0140",
            PasswordHash = hasher.Hash(DefaultCustomerPassword),
            IsActive = true,
            CreatedUtc = DateTime.UtcNow.AddDays(-64),
            LastLoginUtc = DateTime.UtcNow.AddDays(-2)
        };
        customer.UserRoles.Add(new UserRole { UserId = customer.Id, RoleName = AccountService.CustomerRole });
        customer.Addresses.Add(new Address
        {
            UserId = customer.Id,
            FullName = "Dana Whitfield",
            Line1 = "18 Fennel Row",
            Line2 = "Flat 3",
            City = "Beirut",
            State = "Beirut",
            PostalCode = "1107 2020",
            Country = "Lebanon",
            Phone = "+961 1 555 014",
            IsDefault = true
        });

        db.Users.AddRange(admin, customer);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Seeded accounts: {AdminEmail} (Admin) and {CustomerEmail} (Customer)",
            adminEmail, DefaultCustomerEmail);
    }

    // ------------------------------------------------------------------
    // A marketplace shop that owns the seeded catalogue
    // ------------------------------------------------------------------
    private static async Task SeedShopAsync(ApplicationDbContext db, ILogger logger, CancellationToken ct)
    {
        if (await db.Shops.AnyAsync(ct))
        {
            return;
        }

        var admin = await db.Users.FirstOrDefaultAsync(u => u.UserRoles.Any(r => r.RoleName == "Admin"), ct)
                   ?? await db.Users.FirstOrDefaultAsync(ct);

        var shop = new Shop
        {
            OwnerUserId = admin?.Id ?? string.Empty,
            Name = "LibanPost eShop",
            Slug = "libanpost-eshop",
            Description = "The marketplace's own demonstration vendor.",
            Tagline = "Lebanon's postal services and supplies.",
            Status = ShopStatus.Approved,
            ApprovedUtc = DateTime.UtcNow,
            CommissionRate = 0.05m,
            FlatShippingRate = 4.50m,
            FreeShippingThreshold = 60.00m,
            Currency = "USD",
            IsFeatured = true,
            CreatedUtc = DateTime.UtcNow
        };

        if (admin is not null)
        {
            shop.Users.Add(new ShopUser
            {
                UserId = admin.Id,
                Role = ShopRole.Owner,
                AcceptedTermsUtc = DateTime.UtcNow,
                CreatedUtc = DateTime.UtcNow
            });
        }

        db.Shops.Add(shop);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Seeded marketplace shop '{ShopName}' ({ShopId})", shop.Name, shop.Id);
    }

    // ------------------------------------------------------------------
    // Listings: a shop's commercial offer for each seeded product
    // ------------------------------------------------------------------
    private static async Task SeedListingsAsync(ApplicationDbContext db, CancellationToken ct)
    {
        if (await db.ShopListings.AnyAsync(ct))
        {
            return;
        }

        var shop = await db.Shops.FirstOrDefaultAsync(ct);
        if (shop is null)
        {
            return;
        }

        var products = await db.Products.Include(p => p.Variants).ToListAsync(ct);
        foreach (var product in products)
        {
            product.ShopId = shop.Id;
            var stock = product.Variants.Where(v => v.IsActive).Sum(v => v.StockQuantity);
            db.ShopListings.Add(new ShopListing
            {
                ShopId = shop.Id,
                ProductId = product.Id,
                Price = product.Variants.Where(v => v.IsActive).Select(v => (decimal?)v.Price).Min() ?? product.Variants.FirstOrDefault()?.Price,
                StockQuantity = stock > 0 ? stock : product.Variants.Sum(v => v.StockQuantity),
                IsActive = true,
                UpdatedUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------
    // A sample order so the admin fulfilment queue is not empty on first run
    // ------------------------------------------------------------------

    private static async Task SeedSampleOrderAsync(ApplicationDbContext db, ILogger logger, CancellationToken ct)
    {
        if (await db.Orders.AnyAsync(ct))
        {
            return;
        }

        var customer = await db.Users.FirstOrDefaultAsync(u => u.Email == DefaultCustomerEmail, ct);
        if (customer is null)
        {
            return;
        }

        // Pick two in-stock variants from different products.
        var variants = await db.ProductVariants
            .Include(v => v.Product).ThenInclude(p => p!.Images)
            .Where(v => v.IsActive && v.StockQuantity > 3 && v.Product!.IsFeatured)
            .OrderBy(v => v.Id)
            .Take(2)
            .ToListAsync(ct);

        if (variants.Count == 0)
        {
            return;
        }

        var seedShop = await db.Shops.FirstOrDefaultAsync(ct);
        var order = new Order
        {
            ShopId = seedShop?.Id ?? 0,
            OrderNumber = $"ORD-{DateTime.UtcNow.Year}-0001",
            UserId = customer.Id,
            Email = customer.Email,
            CustomerName = customer.DisplayName,
            Phone = customer.Phone,
            Status = OrderStatus.Shipped,
            ShippingName = "Dana Whitfield",
            ShippingLine1 = "18 Fennel Row",
            ShippingLine2 = "Flat 3",
            ShippingCity = "Beirut",
            ShippingState = "Beirut",
            ShippingPostalCode = "1107 2020",
            ShippingCountry = "Lebanon",
            TrackingNumber = "LBP0000000001",
            PlacedUtc = DateTime.UtcNow.AddDays(-6),
            PaidUtc = DateTime.UtcNow.AddDays(-6),
            ShippedUtc = DateTime.UtcNow.AddDays(-4)
        };

        foreach (var variant in variants)
        {
            var quantity = variant.Id == variants[0].Id ? 1 : 2;
            order.Lines.Add(new OrderLine
            {
                ProductId = variant.ProductId,
                ProductVariantId = variant.Id,
                ProductName = variant.Product!.Name,
                VariantName = variant.Name,
                Sku = variant.Sku,
                ImageUrl = variant.Product.Images.OrderByDescending(i => i.IsPrimary).FirstOrDefault()?.Url,
                Quantity = quantity,
                UnitPrice = variant.Price,
                LineTotal = Math.Round(variant.Price * quantity, 2)
            });
        }

        order.Subtotal = order.Lines.Sum(l => l.LineTotal);
        order.ShippingTotal = order.Subtotal >= 75m ? 0m : 5.99m;
        order.TaxTotal = Math.Round(order.Subtotal * 0.0825m, 2);
        order.GrandTotal = Math.Round(order.Subtotal + order.ShippingTotal + order.TaxTotal, 2);

        db.Orders.Add(order);

        db.Payments.Add(new Payment
        {
            Order = order,
            Provider = "sandbox",
            TransactionId = "sbx_seeded000000000000000",
            Amount = order.GrandTotal,
            Currency = "USD",
            Status = PaymentStatus.Succeeded,
            IsTest = true,
            CreatedUtc = order.PlacedUtc,
            CompletedUtc = order.PlacedUtc
        });

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded sample order {OrderNumber}", order.OrderNumber);
    }

    // ------------------------------------------------------------------
    // Coupons: a couple of working codes so the feature is demonstrable
    // ------------------------------------------------------------------

    /// <summary>
    /// Seeds two promotional codes — a store-wide 10% and a fixed-amount code with a minimum — so
    /// the cart and admin coupon surfaces have something to exercise on a fresh store.
    /// </summary>
    private static async Task SeedCouponsAsync(ApplicationDbContext db, ILogger logger, CancellationToken ct)
    {
        if (await db.Coupons.AnyAsync(ct))
        {
            return;
        }

        db.Coupons.AddRange(
            new Coupon
            {
                Code = "WELCOME10",
                Description = "10% off any order — welcome offer.",
                Type = DiscountType.Percentage,
                Value = 10m,
                MinSubtotal = 0m,
                MaxUsesPerUser = 1,
                IsActive = true,
                AppliesToAll = true
            },
            new Coupon
            {
                Code = "POST5",
                Description = "$5 off orders over $30.",
                Type = DiscountType.FixedAmount,
                Value = 5.00m,
                MinSubtotal = 30.00m,
                MaxUses = 100,
                IsActive = true,
                AppliesToAll = true
            },
            new Coupon
            {
                Code = "EXPIRED",
                Description = "Sample of an out-of-date code, kept so the rejection path is visible.",
                Type = DiscountType.Percentage,
                Value = 15m,
                ExpiresUtc = DateTime.UtcNow.AddDays(-1),
                IsActive = true,
                AppliesToAll = true
            });

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded 3 discount coupons");
    }

    // ------------------------------------------------------------------
    // Reviews: reputation that is not blank on first run
    // ------------------------------------------------------------------

    /// <summary>
    /// Seeds a plausible spread of reviews so the rating, histogram and seller-badge surfaces have
    /// something to show. Generated from a fixed seed, so every fresh install looks identical.
    /// </summary>
    /// <remarks>
    /// Seeded reviews carry no <see cref="Review.UserId"/>: they stand in for historical shoppers
    /// whose accounts are not part of this demonstration store. The demo customer's own review is
    /// written through the storefront against the seeded order, which is the path a reviewer would
    /// actually take.
    /// </remarks>
    private static async Task SeedReviewsAsync(ApplicationDbContext db, ILogger logger, CancellationToken ct)
    {
        if (await db.Reviews.AnyAsync(ct))
        {
            return;
        }

        var products = await db.Products.OrderBy(p => p.Id).ToListAsync(ct);
        if (products.Count == 0)
        {
            return;
        }

        var rng = new Random(20260415);
        var now = DateTime.UtcNow;
        var reviews = new List<Review>();

        foreach (var product in products)
        {
            // A product that no shop has listed cannot collect reviews.
            if (product.ShopId == 0)
            {
                continue;
            }

            // The number of reviews varies per product so the "most reviewed" sort has a signal.
            var count = rng.Next(3, 22);
            for (var i = 0; i < count; i++)
            {
                var rating = WeightedRating(rng);
                var (title, body) = CommentFor(rating, rng);
                var created = now.AddDays(-rng.Next(1, 300)).AddHours(-rng.Next(0, 24));

                var review = new Review
                {
                    ProductId = product.Id,
                    ShopId = product.ShopId,
                    UserId = null,
                    ReviewerName = ReviewerNames[rng.Next(ReviewerNames.Length)],
                    Rating = rating,
                    Title = title,
                    Body = body,
                    // Most reviewers are verified buyers; a minority are not, as on any marketplace.
                    IsVerifiedPurchase = rng.Next(100) < 78,
                    Status = ReviewStatus.Published,
                    CreatedUtc = created,
                    PublishedUtc = created,
                    // Satisfied reviewers attract more agreement than critical ones.
                    HelpfulCount = rating >= 4 ? rng.Next(0, 30) : rng.Next(0, 6),
                    NotHelpfulCount = rating >= 4 ? rng.Next(0, 3) : rng.Next(0, 8)
                };

                // Sellers are nudged to answer criticism, so about half the lower ratings get a
                // public reply — the behaviour the vendor area is built to support.
                if (rating <= 3 && rng.Next(100) < 55)
                {
                    review.VendorReply =
                        "Thank you for taking the time to report this. Our counter team has reviewed the batch and changed how this item is packed and checked before dispatch.";
                    review.VendorReplyUtc = created.AddDays(rng.Next(1, 6));
                }

                reviews.Add(review);
            }
        }

        db.Reviews.AddRange(reviews);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Seeded {Count} product reviews across {Products} products", reviews.Count, products.Count);
    }

    /// <summary>Sketches the star distribution of a real storefront: mostly 5s, a long critical tail.</summary>
    private static int WeightedRating(Random rng)
    {
        var roll = rng.Next(100);
        return roll switch
        {
            < 58 => 5,
            < 84 => 4,
            < 94 => 3,
            < 98 => 2,
            _ => 1
        };
    }

    private static (string Title, string Body) CommentFor(int rating, Random rng)
    {
        var pool = rating >= 4 ? PositiveReviews : rating == 3 ? NeutralReviews : CriticalReviews;
        return pool[rng.Next(pool.Length)];
    }

    private static readonly string[] ReviewerNames =
    {
        "Rania H.", "Marc D.", "Youssef K.", "Layla N.", "Karim S.", "Nadia F.", "Georges A.",
        "Maya T.", "Hassan R.", "Christelle M.", "Omar B.", "Dalia S.", "Elie Z.", "Nour H.",
        "Tarek J.", "Rita C.", "Fadi G.", "Sara W.", "Antoine L.", "Hala M."
    };

    private static readonly (string Title, string Body)[] PositiveReviews =
    {
        ("Exactly as described", "Arrived quickly and matched the description. The quality is better than I expected for the price and I would order again."),
        ("Great value", "Well made and does the job. Delivery through LibanPost was fast and the tracking worked the whole way."),
        ("Would buy again", "Second time ordering this. Consistent quality and the counter staff were helpful when I collected it."),
        ("Solid quality", "Sturdy and well finished. No complaints at all after a couple of weeks of daily use."),
        ("Fast delivery", "Ordered on Monday and it was here by Wednesday. Packed properly, nothing damaged."),
        ("Just what I needed", "Fits the description perfectly and the price is fair. Recommended for anyone sending regularly.")
    };

    private static readonly (string Title, string Body)[] NeutralReviews =
    {
        ("Does the job", "It works as advertised but the finish is a little basic. Fine for the price, nothing special."),
        ("Good but slow", "The item itself is fine. Delivery took longer than I expected, though that may be the season."),
        ("Average", "Nothing wrong with it, but nothing that stands out either. Perfectly usable."),
        ("Okay overall", "Decent product. The packaging arrived slightly dented, but the contents were unaffected.")
    };

    private static readonly (string Title, string Body)[] CriticalReviews =
    {
        ("Not as sturdy as it looks", "The board is thinner than I expected and one corner arrived crushed. It still works but I would look elsewhere for heavy items."),
        ("Arrived late", "Took nearly two weeks even though it was marked as express. The product is fine, the delay was the problem."),
        ("Missing pieces", "One seal was missing from the pack. Support replaced it quickly, but it should not have shipped short."),
        ("Expected better", "The description oversells it. Functional, but the finish feels cheap compared to the photo.")
    };
}
