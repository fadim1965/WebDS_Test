namespace WebDS_Test.Services;

/// <summary>
/// Rules for settling a vendor's ledger. Kept in configuration because they are policy, not code:
/// the minimum is the marketplace's answer to "is this balance worth a bank transfer?".
/// </summary>
public sealed class PayoutOptions
{
    public const string SectionName = "Payouts";

    /// <summary>
    /// Smallest outstanding balance worth a settlement. A balance below it is refused so a shop a few
    /// cents in credit is not paid a transfer fee to receive it. Zero allows any positive balance.
    /// </summary>
    public decimal MinimumAmount { get; set; }

    /// <summary>Method pre-filled on the settlement form.</summary>
    public string DefaultMethod { get; set; } = "Bank transfer";
}
