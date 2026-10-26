using WebDS_Test.Domain;

namespace WebDS_Test.Areas.Vendor.Models;

/// <summary>One colleague with access to the shop.</summary>
public sealed class TeamMemberRow
{
    public string UserId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public ShopRole Role { get; init; }
    public DateTime GrantedUtc { get; init; }
    public DateTime? AcceptedTermsUtc { get; init; }

    public bool IsOwner => Role == ShopRole.Owner;

    public string RoleLabel => Role switch
    {
        ShopRole.Owner => "Owner",
        ShopRole.Manager => "Manager",
        ShopRole.Fulfilment => "Fulfilment",
        _ => Role.ToString()
    };
}

/// <summary>The team screen: who can operate the shop, and the form that adds someone.</summary>
public sealed class TeamIndexViewModel
{
    public Shop Shop { get; init; } = null!;

    public IReadOnlyList<TeamMemberRow> Members { get; init; } = Array.Empty<TeamMemberRow>();

    /// <summary>Roles a new colleague may be given. Ownership is not transferable here.</summary>
    public IReadOnlyList<ShopRole> InvitableRoles { get; } = new[] { ShopRole.Manager, ShopRole.Fulfilment };
}
