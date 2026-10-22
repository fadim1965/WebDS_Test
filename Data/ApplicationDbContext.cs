using Microsoft.EntityFrameworkCore;

namespace WebDS_Test.Data;

/// <summary>
/// Single EF Core context for the store: catalogue, carts, orders, inventory ledger and
/// customers — plus the application's own log trail. SQLite in development, provider-swappable for
/// production — nothing here is SQLite-specific.
/// </summary>
public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();
    public DbSet<ProductImage> ProductImages => Set<ProductImage>();
    public DbSet<Cart> Carts => Set<Cart>();
    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentEvent> PaymentEvents => Set<PaymentEvent>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<ApplicationUser> Users => Set<ApplicationUser>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Address> Addresses => Set<Address>();
    public DbSet<WishlistItem> WishlistItems => Set<WishlistItem>();
    public DbSet<Coupon> Coupons => Set<Coupon>();
    public DbSet<CouponUsage> CouponUsages => Set<CouponUsage>();

    // Marketplace
    public DbSet<Shop> Shops => Set<Shop>();
    public DbSet<ShopUser> ShopUsers => Set<ShopUser>();
    public DbSet<ShopListing> ShopListings => Set<ShopListing>();
    public DbSet<VendorLedgerEntry> VendorLedgerEntries => Set<VendorLedgerEntry>();
    public DbSet<VendorPayout> VendorPayouts => Set<VendorPayout>();

    // Returns
    public DbSet<ReturnRequest> ReturnRequests => Set<ReturnRequest>();
    public DbSet<ReturnLine> ReturnLines => Set<ReturnLine>();

    // Admin audit trail
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    // Shipping
    public DbSet<OrderShipment> OrderShipments => Set<OrderShipment>();
    public DbSet<ShipmentEvent> ShipmentEvents => Set<ShipmentEvent>();

    // Tax
    public DbSet<TaxClass> TaxClasses => Set<TaxClass>();

    /// <summary>
    /// The application's own log trail, written by the database log sink. It maps onto the
    /// pre-existing <c>Log</c> table, so it is named for the table rather than the entity.
    /// </summary>
    public DbSet<LogEntry> Logs => Set<LogEntry>();

    // Reputation
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<ReviewVote> ReviewVotes => Set<ReviewVote>();

    // Messaging
    public DbSet<EmailMessage> EmailMessages => Set<EmailMessage>();

    // Customer <-> vendor conversations
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();

    // Settings administration can change without a deployment (exchange rate, ...)
    public DbSet<SiteSetting> SiteSettings => Set<SiteSetting>();

    // Editable UI strings, seeded from App_Data/i18n and maintained by administration
    public DbSet<Translation> Translations => Set<Translation>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // Money is always decimal(18,2). SQLite stores it as TEXT, which is lossless for
        // decimal; switching the provider to SQL Server maps this to a real decimal column.
        b.Entity<ProductVariant>(e =>
        {
            e.Property(v => v.Price).HasPrecision(18, 2);
            e.Property(v => v.CompareAtPrice).HasPrecision(18, 2);
        });
        b.Entity<Order>(e =>
        {
            e.Property(o => o.Subtotal).HasPrecision(18, 2);
            e.Property(o => o.ShippingTotal).HasPrecision(18, 2);
            e.Property(o => o.TaxTotal).HasPrecision(18, 2);
            e.Property(o => o.DiscountTotal).HasPrecision(18, 2);
            e.Property(o => o.GrandTotal).HasPrecision(18, 2);
            e.Property(o => o.RefundedTotal).HasPrecision(18, 2);
        });
        b.Entity<OrderLine>(e =>
        {
            e.Property(l => l.UnitPrice).HasPrecision(18, 2);
            e.Property(l => l.LineTotal).HasPrecision(18, 2);
        });
        b.Entity<Payment>(e => e.Property(p => p.Amount).HasPrecision(18, 2));

        b.Entity<Category>(e =>
        {
            // Slug is unique per shop, and separately unique across global categories. Two shops
            // may each have a category called "Boxes"; the marketplace may have its own too.
            e.HasIndex(c => new { c.ShopId, c.Slug }).IsUnique();
            e.HasIndex(c => c.ParentId);
            e.HasIndex(c => c.IsGlobal);
            e.HasOne(c => c.Parent)
                .WithMany(c => c.Children)
                .HasForeignKey(c => c.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(c => c.Shop)
                .WithMany(s => s.Categories)
                .HasForeignKey(c => c.ShopId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Product>(e =>
        {
            // Product slugs are unique per shop, so /shop/{shop}/product/{slug} always resolves.
            e.HasIndex(p => new { p.ShopId, p.Slug }).IsUnique();
            e.HasIndex(p => p.CategoryId);
            e.HasIndex(p => p.IsPublished);
            e.HasIndex(p => p.IsDeleted);
            e.HasIndex(p => p.ShopId);
            // NOTE: there is deliberately no global query filter for soft delete here. A filter on
            // Product while ProductImage/ProductVariant hold required relationships to it makes
            // EF emit a "required end of a relationship is filtered" warning — and on a machine
            // where the Event Log is unavailable that warning alone can abort startup. Queries
            // therefore filter `!p.IsDeleted` explicitly, which is also easier to reason about:
            // every read states whether it wants soft-deleted rows.
            e.HasOne(p => p.Category)
                .WithMany(c => c.Products)
                .HasForeignKey(p => p.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(p => p.Shop)
                .WithMany(s => s.Products)
                .HasForeignKey(p => p.ShopId)
                .OnDelete(DeleteBehavior.Cascade);
            // A tax class is optional: null means "use Store:TaxRate", which is how every product
            // behaved before tax classes existed. SetNull rather than Cascade so deleting a class
            // cannot delete the products that referenced it.
            e.HasOne(p => p.TaxClass)
                .WithMany()
                .HasForeignKey(p => p.TaxClassId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(p => p.TaxClassId);
        });

        b.Entity<TaxClass>(e =>
        {
            e.HasIndex(t => t.Name).IsUnique();
            e.Property(t => t.Rate).HasPrecision(5, 4);
        });

        // ---------------------------------------------------------------
        // Marketplace
        // ---------------------------------------------------------------

        b.Entity<Shop>(e =>
        {
            e.HasIndex(s => s.Slug).IsUnique();
            e.HasIndex(s => s.Status);
            e.HasIndex(s => s.OwnerUserId);
            e.Property(s => s.CommissionRate).HasPrecision(5, 4);
            e.Property(s => s.FlatShippingRate).HasPrecision(18, 2);
            e.Property(s => s.FreeShippingThreshold).HasPrecision(18, 2);
            e.HasOne(s => s.Owner)
                .WithMany()
                .HasForeignKey(s => s.OwnerUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ShopUser>(e =>
        {
            // A user appears at most once per shop.
            e.HasIndex(u => new { u.ShopId, u.UserId }).IsUnique();
            e.HasIndex(u => u.UserId);
            e.HasOne(u => u.Shop)
                .WithMany(s => s.Users)
                .HasForeignKey(u => u.ShopId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(u => u.User)
                .WithMany()
                .HasForeignKey(u => u.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ShopListing>(e =>
        {
            // One listing per product per shop: a shop either sells a product or does not.
            e.HasIndex(l => new { l.ShopId, l.ProductId }).IsUnique();
            e.HasIndex(l => l.ProductId);
            e.HasIndex(l => l.IsActive);
            e.Property(l => l.Price).HasPrecision(18, 2);
            e.Property(l => l.CompareAtPrice).HasPrecision(18, 2);
            // Stock is held here rather than on ProductVariant, so the concurrency token lives
            // here too and protects the last unit a *shop* holds.
            e.Property(l => l.RowVersion).IsConcurrencyToken();
            // Neither of the two "owner" foreign keys cascades. Products reach a listing through
            // ProductVariant (see below) as well, and SQL Server rejects a table with more than
            // one cascade path ("may cause cycles or multiple cascade paths"). Products are
            // soft-deleted rather than removed, so nothing in the application relies on the
            // database deleting a listing behind a product.
            e.HasOne(l => l.Shop)
                .WithMany(s => s.Listings)
                .HasForeignKey(l => l.ShopId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.Product)
                .WithMany(p => p.Listings)
                .HasForeignKey(l => l.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.ProductVariant)
                .WithMany()
                .HasForeignKey(l => l.ProductVariantId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<VendorLedgerEntry>(e =>
        {
            e.HasIndex(l => l.ShopId);
            e.HasIndex(l => l.OrderId);
            e.HasIndex(l => l.SettledUtc);
            // Partial reversals are idempotent per return request, so the link is indexed.
            e.HasIndex(l => l.ReturnRequestId);
            e.Property(l => l.GrossAmount).HasPrecision(18, 2);
            e.Property(l => l.CommissionAmount).HasPrecision(18, 2);
            e.Property(l => l.NetAmount).HasPrecision(18, 2);
            e.HasOne(l => l.Shop)
                .WithMany(s => s.Ledger)
                .HasForeignKey(l => l.ShopId)
                .OnDelete(DeleteBehavior.Cascade);
            // A ledger entry must survive the order being archived, so the link is optional.
            e.HasOne(l => l.Order)
                .WithMany()
                .HasForeignKey(l => l.OrderId)
                .OnDelete(DeleteBehavior.SetNull);
            // Same for the return a partial reversal belongs to.
            e.HasOne(l => l.ReturnRequest)
                .WithMany()
                .HasForeignKey(l => l.ReturnRequestId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<VendorPayout>(e =>
        {
            e.HasIndex(p => p.ShopId);
            e.HasIndex(p => p.Reference).IsUnique();
            e.HasIndex(p => p.Status);
            e.Property(p => p.GrossAmount).HasPrecision(18, 2);
            e.Property(p => p.CommissionAmount).HasPrecision(18, 2);
            e.Property(p => p.NetAmount).HasPrecision(18, 2);
            e.HasOne(p => p.Shop)
                .WithMany(s => s.Payouts)
                .HasForeignKey(p => p.ShopId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ---------------------------------------------------------------
        // Returns
        // ---------------------------------------------------------------

        b.Entity<ReturnRequest>(e =>
        {
            e.HasIndex(r => r.Reference).IsUnique();
            e.HasIndex(r => r.OrderId);
            e.HasIndex(r => r.ShopId);
            e.HasIndex(r => r.Status);
            e.HasIndex(r => r.UserId);
            e.Property(r => r.RefundAmount).HasPrecision(18, 2);
            // Both owners are restricted rather than cascading: orders are never hard-deleted
            // while they hold history, and a return is part of that history. The same reasoning
            // the ledger and the reviews use — and it keeps SQL Server's delete-plan rules simple.
            e.HasOne(r => r.Order)
                .WithMany()
                .HasForeignKey(r => r.OrderId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.Shop)
                .WithMany()
                .HasForeignKey(r => r.ShopId)
                .OnDelete(DeleteBehavior.Restrict);
            // A return outlives the account that raised it, the way an order does.
            e.HasOne(r => r.User)
                .WithMany()
                .HasForeignKey(r => r.UserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<ReturnLine>(e =>
        {
            e.HasIndex(l => l.ReturnRequestId);
            e.HasIndex(l => l.OrderLineId);
            e.Property(l => l.UnitPrice).HasPrecision(18, 2);
            // Lines die with their request; they keep the order line alive (which is never deleted
            // anyway), so a return can always be traced back to what was actually bought.
            e.HasOne(l => l.ReturnRequest)
                .WithMany(r => r.Lines)
                .HasForeignKey(l => l.ReturnRequestId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.OrderLine)
                .WithMany()
                .HasForeignKey(l => l.OrderLineId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ---------------------------------------------------------------
        // Admin audit trail
        // ---------------------------------------------------------------

        b.Entity<AuditLog>(e =>
        {
            // The trail is browsed newest-first and filtered by action, actor and the record acted
            // on; there are no foreign keys — see the type's remarks.
            e.HasIndex(l => l.CreatedUtc);
            e.HasIndex(l => l.Action);
            e.HasIndex(l => l.Actor);
            e.HasIndex(l => new { l.EntityType, l.EntityId });
        });

        // ---------------------------------------------------------------
        // Shipping
        // ---------------------------------------------------------------

        b.Entity<OrderShipment>(e =>
        {
            e.HasIndex(s => s.OrderId);
            e.HasIndex(s => s.ShopId);
            e.HasIndex(s => s.TrackingNumber);
            e.Property(s => s.ShippingCost).HasPrecision(18, 2);
            e.Property(s => s.CodAmount).HasPrecision(18, 2);
            e.HasOne(s => s.Order)
                .WithMany(o => o.Shipments)
                .HasForeignKey(s => s.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.Shop)
                .WithMany()
                .HasForeignKey(s => s.ShopId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ShipmentEvent>(e =>
        {
            e.HasIndex(v => v.ShipmentId);
            e.HasOne(v => v.Shipment)
                .WithMany(s => s.Events)
                .HasForeignKey(v => v.ShipmentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ProductVariant>(e =>
        {
            e.HasIndex(v => v.Sku).IsUnique();
            e.HasIndex(v => v.ProductId);
            e.Property(v => v.RowVersion).IsConcurrencyToken();
            e.HasOne(v => v.Product)
                .WithMany(p => p.Variants)
                .HasForeignKey(v => v.ProductId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ProductImage>(e =>
        {
            e.HasIndex(i => i.ProductId);
            e.HasOne(i => i.Product)
                .WithMany(p => p.Images)
                .HasForeignKey(i => i.ProductId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Cart>(e =>
        {
            e.HasIndex(c => c.UserId);
            e.HasIndex(c => c.AnonymousId);
            e.HasOne(c => c.User)
                .WithMany()
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<CartItem>(e =>
        {
            e.HasIndex(i => i.CartId);
            // One row per variant per cart; quantity is incremented instead of duplicated.
            e.HasIndex(i => new { i.CartId, i.ProductVariantId }).IsUnique();
            e.HasOne(i => i.Cart)
                .WithMany(c => c.Items)
                .HasForeignKey(i => i.CartId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(i => i.ProductVariant)
                .WithMany(v => v.CartItems)
                .HasForeignKey(i => i.ProductVariantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Order>(e =>
        {
            e.HasIndex(o => o.OrderNumber).IsUnique();
            e.HasIndex(o => o.IdempotencyKey).IsUnique();
            e.HasIndex(o => o.UserId);
            e.HasIndex(o => o.Status);
            e.HasIndex(o => o.ShopId);
            e.Property(o => o.CodAmount).HasPrecision(18, 2);
            e.HasOne(o => o.User)
                .WithMany(u => u.Orders)
                .HasForeignKey(o => o.UserId)
                .OnDelete(DeleteBehavior.SetNull);
            // An order belongs to exactly one shop; shops are never hard-deleted while they hold
            // order history, so this is restricted rather than cascading.
            e.HasOne(o => o.Shop)
                .WithMany(s => s.Orders)
                .HasForeignKey(o => o.ShopId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(o => o.Coupon)
                .WithMany()
                .HasForeignKey(o => o.CouponId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<OrderLine>(e =>
        {
            e.HasIndex(l => l.OrderId);
            e.HasOne(l => l.Order)
                .WithMany(o => o.Lines)
                .HasForeignKey(l => l.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
            // Sold variants are retained: the product/variant links must not cascade-delete
            // the historical order line.
            e.HasOne(l => l.ProductVariant)
                .WithMany()
                .HasForeignKey(l => l.ProductVariantId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Payment>(e =>
        {
            e.HasIndex(p => p.OrderId);
            e.HasOne(p => p.Order)
                .WithMany(o => o.Payments)
                .HasForeignKey(p => p.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PaymentEvent>(e =>
        {
            // Provider retries a delivery until it gets a 2xx, so (provider, event) must be unique:
            // the index is what makes a replay a no-op rather than a second application.
            e.HasIndex(p => new { p.Provider, p.EventId }).IsUnique();
            e.HasIndex(p => p.OrderId);
            e.HasIndex(p => p.CreatedUtc);
            e.Property(p => p.Amount).HasPrecision(18, 2);
            // An event outlives the order it refers to: the order may be archived, but the record of
            // what the provider said about it must not disappear with it.
            e.HasOne(p => p.Order)
                .WithMany()
                .HasForeignKey(p => p.OrderId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<StockMovement>(e =>
        {
            e.HasIndex(m => m.ProductVariantId);
            e.HasOne(m => m.ProductVariant)
                .WithMany()
                .HasForeignKey(m => m.ProductVariantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ApplicationUser>(e =>
        {
            e.HasIndex(u => u.NormalizedEmail).IsUnique();
        });

        b.Entity<UserRole>(e =>
        {
            e.HasIndex(r => new { r.UserId, r.RoleName }).IsUnique();
            e.HasOne(r => r.User)
                .WithMany(u => u.UserRoles)
                .HasForeignKey(r => r.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Address>(e =>
        {
            e.HasIndex(a => a.UserId);
            e.HasOne(a => a.User)
                .WithMany(u => u.Addresses)
                .HasForeignKey(a => a.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<WishlistItem>(e =>
        {
            e.HasIndex(w => new { w.UserId, w.ProductId }).IsUnique();
            e.HasOne(w => w.User)
                .WithMany(u => u.Wishlist)
                .HasForeignKey(w => w.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(w => w.Product)
                .WithMany()
                .HasForeignKey(w => w.ProductId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ---------------------------------------------------------------
        // Reputation
        // ---------------------------------------------------------------

        b.Entity<Review>(e =>
        {
            e.HasIndex(r => r.ProductId);
            e.HasIndex(r => r.ShopId);
            // Supports "did this shopper already review it?" without scanning the product's reviews.
            e.HasIndex(r => r.UserId);
            // The product page and every rating aggregate filter on (product, published).
            e.HasIndex(r => new { r.ProductId, r.Status });
            e.HasIndex(r => r.Status);
            e.HasIndex(r => r.CreatedUtc);

            // Neither owner cascades. Products and shops are soft-deleted or retained, so a review
            // is never removed behind their back — and two cascading paths onto one table is
            // exactly the shape SQL Server rejects. Reviews are deleted explicitly instead.
            e.HasOne(r => r.Product)
                .WithMany(p => p.Reviews)
                .HasForeignKey(r => r.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.Shop)
                .WithMany(s => s.Reviews)
                .HasForeignKey(r => r.ShopId)
                .OnDelete(DeleteBehavior.Restrict);
            // A review outlives the account that wrote it: the row is kept and simply loses its
            // author, which is what keeps a marketplace's history honest.
            e.HasOne(r => r.User)
                .WithMany()
                .HasForeignKey(r => r.UserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<ReviewVote>(e =>
        {
            e.HasIndex(v => v.ReviewId);
            // One vote per reviewer per review; the unique index makes it an invariant.
            e.HasIndex(v => new { v.ReviewId, v.UserId }).IsUnique();
            e.HasOne(v => v.Review)
                .WithMany(r => r.Votes)
                .HasForeignKey(v => v.ReviewId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(v => v.User)
                .WithMany()
                .HasForeignKey(v => v.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ---------------------------------------------------------------
        // Diagnostics
        // ---------------------------------------------------------------

        b.Entity<LogEntry>(e =>
        {
            // The sink's table is named "Log" (the store's own convention for it) and is written
            // constantly but never joined: the index is on the timestamp alone, which is what date
            // ranges — browsing and retention — actually filter on.
            e.ToTable("Log");
            e.HasIndex(l => l.Logged);
        });

        b.Entity<EmailMessage>(e =>
        {
            // The outbox is drained by status, browsed newest-first, and filtered by kind; the
            // dedupe key is looked up on every notification. Recipients are stored as loose
            // references, not foreign keys — see the type's remarks — so there is nothing to
            // cascade here.
            e.HasIndex(m => m.Status);
            e.HasIndex(m => m.CreatedUtc);
            e.HasIndex(m => m.Kind);
            e.HasIndex(m => m.DedupeKey);
            e.HasIndex(m => m.UserId);
            e.HasIndex(m => m.OrderId);
        });

        // ---------------------------------------------------------------
        // Customer <-> vendor conversations
        // ---------------------------------------------------------------

        b.Entity<Conversation>(e =>
        {
            e.HasIndex(c => c.Reference).IsUnique();
            e.HasIndex(c => c.ShopId);
            e.HasIndex(c => c.UserId);
            e.HasIndex(c => c.Status);
            // The inbox is ordered by activity: "what needs me most recently" is the only sort
            // either side ever asks for.
            e.HasIndex(c => c.LastMessageUtc);
            e.HasOne(c => c.Shop)
                .WithMany()
                .HasForeignKey(c => c.ShopId)
                .OnDelete(DeleteBehavior.Restrict);
            // A conversation outlives the order and the account it mentions, the way a return
            // does: it is history, and history is kept.
            e.HasOne(c => c.Order)
                .WithMany()
                .HasForeignKey(c => c.OrderId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(c => c.User)
                .WithMany()
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<ConversationMessage>(e =>
        {
            e.HasIndex(m => m.ConversationId);
            e.HasIndex(m => m.SentUtc);
            // Messages die with their thread; nothing else points at them.
            e.HasOne(m => m.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SiteSetting>(e =>
        {
            // One row per key: the store reads the whole table into a small dictionary, and the
            // unique index is what makes a concurrent duplicate write fail instead of doubling.
            e.HasIndex(s => s.Key).IsUnique();
        });

        b.Entity<Translation>(e =>
        {
            // A key is unique within a language. The store reads the table into a nested
            // culture -> key dictionary; the unique index is what turns a duplicate save into a
            // failure rather than a second row that silently shadows the first.
            e.HasIndex(t => new { t.Culture, t.Key }).IsUnique();
        });

        b.Entity<Coupon>(e =>
        {
            e.HasIndex(c => c.Code).IsUnique();
            e.HasIndex(c => c.IsActive);
            e.HasIndex(c => c.ShopId);
            e.Property(c => c.Value).HasPrecision(18, 2);
            e.Property(c => c.MinSubtotal).HasPrecision(18, 2);
            e.HasOne(c => c.Shop)
                .WithMany()
                .HasForeignKey(c => c.ShopId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<CouponUsage>(e =>
        {
            e.HasIndex(u => u.CouponId);
            e.HasIndex(u => u.UserId);
            e.HasIndex(u => u.OrderId);
            e.HasOne(u => u.Coupon)
                .WithMany(c => c.Usages)
                .HasForeignKey(u => u.CouponId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(u => u.User)
                .WithMany()
                .HasForeignKey(u => u.UserId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(u => u.Order)
                .WithMany()
                .HasForeignKey(u => u.OrderId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }

    public override int SaveChanges()
    {
        BumpVariantRowVersions();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        BumpVariantRowVersions();
        return base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// SQLite has no server-generated rowversion, so the concurrency token is refreshed
    /// client-side. EF still sends the *original* value in the UPDATE's WHERE clause, so a lost
    /// update surfaces as DbUpdateConcurrencyException.
    /// </summary>
    /// <remarks>
    /// Both token-bearing entities are covered. <see cref="ShopListing"/> is the one that matters
    /// for a marketplace: two shoppers taking the last unit from the same shop must not both win,
    /// even though they may be buying from different shops at the same moment.
    /// </remarks>
    private void BumpVariantRowVersions()
    {
        foreach (var entry in ChangeTracker.Entries<ProductVariant>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Property(v => v.RowVersion).CurrentValue = Guid.NewGuid().ToByteArray();
            }
        }

        foreach (var entry in ChangeTracker.Entries<ShopListing>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Property(l => l.RowVersion).CurrentValue = Guid.NewGuid().ToByteArray();
            }
        }
    }
}
