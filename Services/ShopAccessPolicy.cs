using WebDS_Test.Domain;

namespace WebDS_Test.Services;

/// <summary>
/// What a shop role may do, expressed once so every vendor controller answers the question the same
/// way.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ShopAccess"/> already carries the three long-standing decisions (catalogue, settings,
/// fulfilment); this type adds the ones the after-sales and money screens needed. Keeping them here
/// rather than in each action means a new controller cannot quietly invent a fourth interpretation
/// of "manager".
/// </para>
/// <para>
/// The matrix is deliberately small:
/// </para>
/// <list type="bullet">
/// <item><description><b>Owner</b> — everything, including the bank destination and the team.</description></item>
/// <item><description><b>Manager</b> — day-to-day trading: catalogue, fulfilment, after-sales
/// decisions, customer replies and the financial reports. Not the bank destination, not the team.</description></item>
/// <item><description><b>Fulfilment</b> — packing, labels, dispatch and tracking, plus reading the
/// order and after-sales queues. Nothing that changes money, prices or who works in the shop.</description></item>
/// </list>
/// </remarks>
public static class ShopAccessPolicy
{
    /// <summary>
    /// Approving, rejecting and receiving returns, and answering after-sales threads that commit the
    /// shop to a remedy. Fulfilment staff can read the queue but not decide it.
    /// </summary>
    public static bool CanDecideAfterSales(this ShopAccess access) =>
        access.Role is ShopRole.Owner or ShopRole.Manager;

    /// <summary>
    /// Seeing payouts, the ledger and the financial reports. Fulfilment staff have no business
    /// knowing what the shop earns or where the money is sent.
    /// </summary>
    public static bool CanViewFinancials(this ShopAccess access) =>
        access.Role is ShopRole.Owner or ShopRole.Manager;

    /// <summary>
    /// Changing the bank account a settlement is paid into. Owner-only: redirecting the money is the
    /// one change that cannot be undone by the marketplace after the transfer leaves.
    /// </summary>
    public static bool CanManagePayoutDetails(this ShopAccess access) =>
        access.Role == ShopRole.Owner;

    /// <summary>Adding or removing colleagues, and choosing their role.</summary>
    public static bool CanManageTeam(this ShopAccess access) =>
        access.Role == ShopRole.Owner;

    /// <summary>
    /// Replying to a shopper or answering a public review. The shop speaks with one voice, and that
    /// voice is the owner's or a manager's.
    /// </summary>
    public static bool CanRespondToCustomers(this ShopAccess access) =>
        access.Role is ShopRole.Owner or ShopRole.Manager;
}
