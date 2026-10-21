using WebDS_Test.Domain;
using WebDS_Test.Services;

namespace WebDS_Test.Areas.Vendor.Models;

/// <summary>A vendor's own money page: what they are owed, what has been paid, and why.</summary>
public sealed class VendorPayoutViewModel
{
    public required Shop Shop { get; init; }

    /// <summary>The unsettled balance, or null if the shop has vanished (it cannot, here, but the type allows it).</summary>
    public ShopOutstanding? Outstanding { get; init; }

    public IReadOnlyList<VendorPayout> Payouts { get; init; } = Array.Empty<VendorPayout>();

    /// <summary>The most recent ledger entries, settled or not, so the balance is explainable.</summary>
    public IReadOnlyList<VendorLedgerEntry> Ledger { get; init; } = Array.Empty<VendorLedgerEntry>();
}

/// <summary>One payout as the vendor sees it, with the entries it settled.</summary>
public sealed class VendorPayoutDetailViewModel
{
    public required Shop Shop { get; init; }
    public required VendorPayout Payout { get; init; }
    public IReadOnlyList<VendorLedgerEntry> Lines { get; init; } = Array.Empty<VendorLedgerEntry>();
}
