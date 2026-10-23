using System.ComponentModel.DataAnnotations;

namespace WebDS_Test.Domain;

/// <summary>
/// A vendor's shop — the tenant boundary of the marketplace.
/// </summary>
/// <remarks>
/// <para>
/// Everything a vendor can see is scoped by <see cref="Id"/>. Products belong to exactly one shop,
/// a shop's listings belong to that shop, and an order always belongs to a single shop because
/// checkout splits a mixed cart. That last rule is what keeps fulfilment tractable: one order, one
/// shop, one consignment for the carrier to collect.
/// </para>
/// <para>
/// Shop status is deliberately a workflow rather than a boolean so a vendor can be suspended
/// without deleting their catalogue.
/// </para>
/// </remarks>
public class Shop
{
    public int Id { get; set; }

    /// <summary>The signed-in user who registered the shop.</summary>
    public string OwnerUserId { get; set; } = string.Empty;
    public ApplicationUser? Owner { get; set; }

    [Required, StringLength(120)]
    public string Name { get; set; } = string.Empty;

    /// <summary>URL segment: <c>/shop/{slug}</c>. Unique.</summary>
    [Required, StringLength(120)]
    public string Slug { get; set; } = string.Empty;

    [StringLength(600)]
    public string? Description { get; set; }

    /// <summary>Short tagline shown on cards in the marketplace directory.</summary>
    [StringLength(160)]
    public string? Tagline { get; set; }

    public ShopStatus Status { get; set; } = ShopStatus.PendingApproval;

    /// <summary>Why a shop was rejected or suspended. Shown to the vendor.</summary>
    [StringLength(500)]
    public string? StatusReason { get; set; }

    // ---- Contact and origin -------------------------------------------
    [StringLength(120)] public string? ContactName { get; set; }
    [StringLength(40)] public string? Phone { get; set; }
    [EmailAddress, StringLength(256)] public string? Email { get; set; }
    [StringLength(200)] public string? Website { get; set; }

    [StringLength(200)] public string? AddressLine1 { get; set; }
    [StringLength(100)] public string? City { get; set; }
    [StringLength(40)] public string? PostalCode { get; set; }
    [StringLength(80)] public string? Country { get; set; }

    /// <summary>
    /// Branch or counter the carrier collects from. Used as the pickup reference on a consignment.
    /// </summary>
    [StringLength(120)] public string? PickupLocation { get; set; }

    // ---- Commerce ------------------------------------------------------
    /// <summary>Marketplace commission for this shop, as a fraction (0.10 = 10%).</summary>
    public decimal CommissionRate { get; set; } = 0.10m;

    /// <summary>Flat per-order shipping the shop charges. Overrides the marketplace default when set.</summary>
    public decimal? FlatShippingRate { get; set; }

    /// <summary>Subtotal at or above which this shop ships free. Null uses the marketplace setting.</summary>
    public decimal? FreeShippingThreshold { get; set; }

    /// <summary>Currency the shop prices in. Single-currency today; recorded for clarity.</summary>
    [StringLength(8)] public string Currency { get; set; } = "USD";

    // ---- Payout destination --------------------------------------------
    // Where a settled balance would be sent. Recorded, not verified: the demonstration store moves
    // no money, so these are the fields a real settlement run would need rather than a working
    // banking integration.
    /// <summary>Name on the bank account the payout is sent to.</summary>
    [StringLength(120)] public string? BankAccountName { get; set; }

    /// <summary>The bank or payment provider that holds the account.</summary>
    [StringLength(120)] public string? BankName { get; set; }

    /// <summary>IBAN or account number, as the shop entered it. Shown only to staff.</summary>
    [StringLength(40)] public string? BankIban { get; set; }

    // ---- Presentation --------------------------------------------------
    /// <summary>Key into the theme catalogue. See <c>IThemeCatalog</c>.</summary>
    [StringLength(40)]
    public string ThemeKey { get; set; } = "postal-classic";

    /// <summary>Optional logo path under wwwroot. Falls back to the marketplace logo.</summary>
    [StringLength(500)]
    public string? LogoUrl { get; set; }

    /// <summary>Banner path under wwwroot, shown at the top of the shop storefront.</summary>
    [StringLength(500)]
    public string? BannerUrl { get; set; }

    /// <summary>Whether the shop appears in the marketplace directory.</summary>
    public bool IsFeatured { get; set; }

    // ---- Audit ---------------------------------------------------------
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedUtc { get; set; }
    public DateTime? SuspendedUtc { get; set; }

    public ICollection<ShopUser> Users { get; set; } = new List<ShopUser>();
    public ICollection<Product> Products { get; set; } = new List<Product>();
    public ICollection<ShopListing> Listings { get; set; } = new List<ShopListing>();
    public ICollection<Order> Orders { get; set; } = new List<Order>();
    public ICollection<Category> Categories { get; set; } = new List<Category>();
    public ICollection<VendorLedgerEntry> Ledger { get; set; } = new List<VendorLedgerEntry>();
    public ICollection<VendorPayout> Payouts { get; set; } = new List<VendorPayout>();

    /// <summary>Reviews of this shop's products, the basis of its seller rating and badge.</summary>
    public ICollection<Review> Reviews { get; set; } = new List<Review>();

    /// <summary>True when the shop is visible and tradeable on the storefront.</summary>
    public bool IsTrading => Status == ShopStatus.Approved;

    /// <summary>Where the carrier collects. Falls back to the registered address.</summary>
    public string PickupSummary => string.Join(", ", new[]
    {
        PickupLocation, AddressLine1, City, PostalCode, Country
    }.Where(part => !string.IsNullOrWhiteSpace(part)));
}

public enum ShopStatus
{
    /// <summary>Registered but not yet reviewed. Cannot sell.</summary>
    PendingApproval = 0,
    Approved = 1,
    Rejected = 2,
    Suspended = 3,
    /// <summary>Closed by the vendor. Catalogue retained, nothing for sale.</summary>
    Closed = 4
}

public static class ShopStatusExtensions
{
    public static string ToDisplay(this ShopStatus status) => status switch
    {
        ShopStatus.PendingApproval => "Awaiting approval",
        ShopStatus.Approved => "Approved",
        ShopStatus.Rejected => "Rejected",
        ShopStatus.Suspended => "Suspended",
        ShopStatus.Closed => "Closed",
        _ => status.ToString()
    };

    public static string ToCssClass(this ShopStatus status) => status switch
    {
        ShopStatus.PendingApproval => "text-bg-warning",
        ShopStatus.Approved => "text-bg-success",
        ShopStatus.Rejected => "text-bg-danger",
        ShopStatus.Suspended => "text-bg-secondary",
        ShopStatus.Closed => "text-bg-dark",
        _ => "text-bg-secondary"
    };
}

/// <summary>Roles within a shop. The owner can do everything; staff roles are narrower.</summary>
public enum ShopRole
{
    Owner = 0,
    Manager = 1,
    Fulfilment = 2
}

/// <summary>Links a user to a shop they can operate.</summary>
public class ShopUser
{
    public int Id { get; set; }

    public int ShopId { get; set; }
    public Shop? Shop { get; set; }

    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

    public ShopRole Role { get; set; } = ShopRole.Owner;

    /// <summary>Set when the vendor accepts the marketplace's terms.</summary>
    public DateTime? AcceptedTermsUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A shop's offer of a product: the shop's own price against a shared product definition.
/// </summary>
/// <remarks>
/// The separation matters for two reasons. A shop controls its own price and stock without
/// touching another shop's, and the same catalogue product (say a standard EMS envelope) can be
/// offered by several shops at different prices — which is what makes a marketplace useful.
/// Inventory lives here, not on <see cref="ProductVariant"/>, because stock is a property of who is
/// selling, not of what is being sold.
/// </remarks>
public class ShopListing
{
    public int Id { get; set; }

    public int ShopId { get; set; }
    public Shop? Shop { get; set; }

    /// <summary>The shared product definition. One product per shop, so this is effectively unique per shop.</summary>
    public int ProductId { get; set; }
    public Product? Product { get; set; }

    /// <summary>Optional shop-specific variant when a shop only sells one option of a product.</summary>
    public int? ProductVariantId { get; set; }
    public ProductVariant? ProductVariant { get; set; }

    /// <summary>The shop's selling price. Null means "use the product's own price".</summary>
    public decimal? Price { get; set; }

    /// <summary>Optional promotional "was" price for this shop.</summary>
    public decimal? CompareAtPrice { get; set; }

    /// <summary>Stock this shop holds. Independent of every other shop's stock.</summary>
    public int StockQuantity { get; set; }

    /// <summary>Below this level the vendor dashboard flags the listing.</summary>
    public int LowStockThreshold { get; set; } = 5;

    /// <summary>When false the listing is hidden from the storefront without being deleted.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Shop-specific SKU, printed on the carrier label when present.</summary>
    [StringLength(64)]
    public string? Sku { get; set; }

    [StringLength(500)]
    public string? Note { get; set; }

    /// <summary>
    /// Concurrency token for the listing's stock. Refreshed on save by <c>ApplicationDbContext</c>,
    /// exactly as <see cref="ProductVariant.RowVersion"/> is, so two shoppers cannot take the last
    /// unit from the same shop.
    /// </summary>
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Effective selling price: the shop's price, else the product's minimum variant price.</summary>
    public decimal EffectivePrice =>
        Price ?? (Product is null || Product.Variants.Count == 0 ? 0m : Product.Variants.Min(v => v.Price));

    public bool IsLowStock => StockQuantity > 0 && StockQuantity <= LowStockThreshold;

    public bool IsOutOfStock => StockQuantity <= 0;
}

/// <summary>
/// Vendor settlement record. One entry per order line, crediting the vendor and debiting the
/// marketplace commission.
/// </summary>
/// <remarks>
/// The marketplace collects from the shopper (cash on delivery today), so the vendor is owed money
/// rather than the reverse. Keeping a ledger rather than a running total means every payout can be
/// reconciled back to the order that generated it.
/// </remarks>
public class VendorLedgerEntry
{
    public int Id { get; set; }

    public int ShopId { get; set; }
    public Shop? Shop { get; set; }

    public int? OrderId { get; set; }
    public Order? Order { get; set; }

    public VendorLedgerEntryType Type { get; set; }

    /// <summary>Gross amount the shopper paid for the lines this entry covers.</summary>
    public decimal GrossAmount { get; set; }

    /// <summary>Marketplace commission withheld.</summary>
    public decimal CommissionAmount { get; set; }

    /// <summary>Net owed to (positive) or recoverable from (negative) the vendor.</summary>
    public decimal NetAmount { get; set; }

    [StringLength(200)]
    public string? Note { get; set; }

    /// <summary>Set when the entry has been included in a payout.</summary>
    public DateTime? SettledUtc { get; set; }

    [StringLength(120)]
    public string? PayoutReference { get; set; }

    /// <summary>
    /// The return request a partial reversal belongs to, when it is one. Full reversals (a whole
    /// cancellation or refund) leave it null — the idempotency key for a full reversal is the bare
    /// (order, type) pair, while a partial reversal is keyed per return.
    /// </summary>
    public int? ReturnRequestId { get; set; }
    public ReturnRequest? ReturnRequest { get; set; }

    /// <summary>The post-sale request that caused this reversal, if applicable.</summary>
    public int? PostSaleRequestId { get; set; }
    public PostSaleRequest? PostSaleRequest { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public enum VendorLedgerEntryType
{
    /// <summary>Vendor is owed for a completed sale.</summary>
    Sale = 0,
    /// <summary>Reversal after a cancellation or refund.</summary>
    Reversal = 1,
    /// <summary>Manual correction by an administrator.</summary>
    Adjustment = 2,
    /// <summary>Money paid out to the vendor.</summary>
    Payout = 3
}

/// <summary>
/// One settlement of a vendor's outstanding balance — the record that money was actually paid out.
/// </summary>
/// <remarks>
/// <para>
/// The ledger records what the marketplace owes; a payout records that it was paid. A payout never
/// writes an earning of its own — it only marks the entries it covers as settled through
/// <see cref="VendorLedgerEntry.SettledUtc"/> and <see cref="VendorLedgerEntry.PayoutReference"/>, so
/// "what is still owed" is always simply "the unsettled entries", and any past statement can be
/// reconstructed by following the reference back.
/// </para>
/// <para>
/// The totals are stored on the payout as well as derivable from its entries. That is deliberate:
/// the statement is a financial document and must not change if an old entry is later corrected — a
/// correction becomes a new, separately settled entry instead.
/// </para>
/// <para>
/// A payout can be marked <em>failed</em> (the bank refused the transfer) or <em>reversed</em>
/// (recorded in error). Either way the ledger entries it covered are released back to "unsettled",
/// so the shop's payable is rebuilt from the ledger rather than from a correction on the payout —
/// the payout row keeps its totals for the statement, and the money trail stays explainable.
/// </para>
/// </remarks>
public class VendorPayout
{
    public int Id { get; set; }

    public int ShopId { get; set; }
    public Shop? Shop { get; set; }

    /// <summary>Human-facing settlement reference, e.g. <c>PAY-2026-0001</c>. Unique.</summary>
    [Required, StringLength(40)]
    public string Reference { get; set; } = string.Empty;

    /// <summary>Timestamp of the earliest earning this payout settled.</summary>
    public DateTime PeriodStartUtc { get; set; }

    /// <summary>Timestamp of the latest earning this payout settled.</summary>
    public DateTime PeriodEndUtc { get; set; }

    /// <summary>How many ledger entries were settled by this payout.</summary>
    public int EntryCount { get; set; }

    /// <summary>Sum of the settled entries' gross, before commission.</summary>
    public decimal GrossAmount { get; set; }

    /// <summary>Marketplace commission withheld across the settled entries.</summary>
    public decimal CommissionAmount { get; set; }

    /// <summary>What was actually paid to the vendor.</summary>
    public decimal NetAmount { get; set; }

    /// <summary>How the money left, e.g. <c>Bank transfer</c>. Free text — the store moves no money.</summary>
    [StringLength(40)]
    public string Method { get; set; } = "Bank transfer";

    [StringLength(200)]
    public string? Note { get; set; }

    /// <summary>Administrator who ran the settlement.</summary>
    [StringLength(256)]
    public string? CreatedBy { get; set; }

    /// <summary>Completed, refused by the bank, or recorded in error.</summary>
    public PayoutStatus Status { get; set; } = PayoutStatus.Paid;

    /// <summary>Why the bank transfer failed, when it did.</summary>
    [StringLength(500)]
    public string? FailureReason { get; set; }

    /// <summary>Who marked the payout failed or reversed.</summary>
    [StringLength(256)]
    public string? HandledBy { get; set; }

    public DateTime? FailedUtc { get; set; }
    public DateTime? ReversedUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Lifecycle of a recorded payout. Transitions are validated in <c>PayoutService</c>.</summary>
public enum PayoutStatus
{
    /// <summary>The settlement was recorded; in a live store the transfer left.</summary>
    Paid = 0,
    /// <summary>The bank or payment provider refused the transfer; the balance re-opened.</summary>
    Failed = 1,
    /// <summary>The settlement was recorded in error and undone; the balance re-opened.</summary>
    Reversed = 2
}

public static class PayoutStatusExtensions
{
    /// <summary>Human label used by badges on the vendor and admin payouts pages.</summary>
    public static string ToDisplay(this PayoutStatus status) => status switch
    {
        PayoutStatus.Paid => "Paid",
        PayoutStatus.Failed => "Failed",
        PayoutStatus.Reversed => "Reversed",
        _ => status.ToString()
    };

    /// <summary>Bootstrap contextual colour for the badge.</summary>
    public static string ToCssClass(this PayoutStatus status) => status switch
    {
        PayoutStatus.Paid => "text-bg-success",
        PayoutStatus.Failed => "text-bg-danger",
        PayoutStatus.Reversed => "text-bg-secondary",
        _ => "text-bg-secondary"
    };
}
