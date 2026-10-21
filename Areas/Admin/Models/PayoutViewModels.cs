using WebDS_Test.Domain;
using WebDS_Test.Services;

namespace WebDS_Test.Areas.Admin.Models;

/// <summary>The settlement dashboard: what each shop is owed, and what has already been paid.</summary>
public sealed class AdminPayoutIndexViewModel
{
    public IReadOnlyList<ShopOutstanding> Balances { get; init; } = Array.Empty<ShopOutstanding>();
    public IReadOnlyList<VendorPayout> History { get; init; } = Array.Empty<VendorPayout>();
    public string Currency { get; init; } = string.Empty;
}

/// <summary>One settlement and the ledger entries it covered.</summary>
public sealed class AdminPayoutDetailViewModel
{
    public required VendorPayout Payout { get; init; }
    public IReadOnlyList<VendorLedgerEntry> Lines { get; init; } = Array.Empty<VendorLedgerEntry>();
    public string Currency { get; init; } = string.Empty;
}
