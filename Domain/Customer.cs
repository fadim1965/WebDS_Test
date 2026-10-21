using System.ComponentModel.DataAnnotations;

namespace WebDS_Test.Domain;

/// <summary>
/// The customer record. This is deliberately a plain entity rather than an
/// <c>IdentityUser</c> derivative: <c>Microsoft.AspNetCore.Identity.EntityFrameworkCore</c> is
/// not available in the local NuGet cache and nuget.org is unreachable from this machine.
/// Cookie authentication is used directly, with hand-rolled PBKDF2 password hashing
/// (see <c>Services/PasswordHasher.cs</c>).
/// </summary>
public class ApplicationUser
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    /// <summary>Normalised (upper-invariant) email, used for the unique index and lookups.</summary>
    [Required, StringLength(256)]
    public string NormalizedEmail { get; set; } = string.Empty;

    [Required, StringLength(120)]
    public string DisplayName { get; set; } = string.Empty;

    [StringLength(40)]
    public string? Phone { get; set; }

    /// <summary>PBKDF2 digest in the form <c>iterations.saltBase64.hashBase64</c>.</summary>
    [Required, StringLength(400)]
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Regenerated whenever credentials or roles change, invalidating existing cookies.</summary>
    [Required, StringLength(64)]
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    public bool IsActive { get; set; } = true;

    public int AccessFailedCount { get; set; }

    /// <summary>Set when lockout is triggered; cleared on a successful sign-in.</summary>
    public DateTime? LockoutEndUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginUtc { get; set; }

    public ICollection<Address> Addresses { get; set; } = new List<Address>();
    public ICollection<Order> Orders { get; set; } = new List<Order>();
    public ICollection<WishlistItem> Wishlist { get; set; } = new List<WishlistItem>();

    /// <summary>Role names granted to this user. Seeded with <c>Customer</c> on registration.</summary>
    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();

    public bool IsLockedOut => LockoutEndUtc is not null && LockoutEndUtc > DateTime.UtcNow;
}

/// <summary>A role assignment. <c>Admin</c> and <c>Customer</c> are the seeded roles.</summary>
public class UserRole
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

    [Required, StringLength(40)]
    public string RoleName { get; set; } = string.Empty;
}

/// <summary>A saved shipping or billing address in the customer's address book.</summary>
public class Address
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

    [Required, StringLength(120)]
    public string FullName { get; set; } = string.Empty;

    [Required, StringLength(200)]
    public string Line1 { get; set; } = string.Empty;

    [StringLength(200)]
    public string? Line2 { get; set; }

    [Required, StringLength(100)]
    public string City { get; set; } = string.Empty;

    [StringLength(100)]
    public string? State { get; set; }

    [Required, StringLength(20)]
    public string PostalCode { get; set; } = string.Empty;

    [Required, StringLength(80)]
    public string Country { get; set; } = string.Empty;

    [StringLength(40)]
    public string? Phone { get; set; }

    /// <summary>At most one default per user; enforced in <c>AddressService</c>.</summary>
    public bool IsDefault { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A product saved to the signed-in customer's wishlist.</summary>
public class WishlistItem
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

    public int ProductId { get; set; }
    public Product? Product { get; set; }

    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}
