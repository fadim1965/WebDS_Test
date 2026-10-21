namespace WebDS_Test.Models;

/// <summary>Backing model for Account/Login.</summary>
public sealed class LoginViewModel
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter your email address.")]
    [System.ComponentModel.DataAnnotations.EmailAddress]
    public string Email { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter your password.")]
    [System.ComponentModel.DataAnnotations.DataType(System.ComponentModel.DataAnnotations.DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public bool RememberMe { get; set; } = true;

    /// <summary>Where to send the shopper after a successful sign-in.</summary>
    public string? ReturnUrl { get; set; }
}

/// <summary>Backing model for Account/Register.</summary>
public sealed class RegisterViewModel
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter your name.")]
    [System.ComponentModel.DataAnnotations.StringLength(120)]
    [System.ComponentModel.DataAnnotations.Display(Name = "Full name")]
    public string DisplayName { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter your email address.")]
    [System.ComponentModel.DataAnnotations.EmailAddress(ErrorMessage = "That does not look like a valid email address.")]
    [System.ComponentModel.DataAnnotations.StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Phone]
    [System.ComponentModel.DataAnnotations.StringLength(40)]
    public string? Phone { get; set; }

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Choose a password.")]
    [System.ComponentModel.DataAnnotations.StringLength(100, MinimumLength = 8, ErrorMessage = "Use at least 8 characters.")]
    [System.ComponentModel.DataAnnotations.DataType(System.ComponentModel.DataAnnotations.DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.DataType(System.ComponentModel.DataAnnotations.DataType.Password)]
    [System.ComponentModel.DataAnnotations.Display(Name = "Confirm password")]
    [System.ComponentModel.DataAnnotations.Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;

    public bool AcceptTerms { get; set; }
}

/// <summary>Backing model for the profile page. Also carries the change-password form.</summary>
public sealed class ProfileViewModel
{
    public ApplicationUser User { get; init; } = null!;
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
    public int OrderCount { get; init; }
    public decimal LifetimeSpend { get; init; }
    public IReadOnlyList<Address> Addresses { get; init; } = Array.Empty<Address>();

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.StringLength(120)]
    [System.ComponentModel.DataAnnotations.Display(Name = "Full name")]
    public string DisplayName { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Phone]
    [System.ComponentModel.DataAnnotations.StringLength(40)]
    public string? Phone { get; set; }
}

/// <summary>Backing model for the change-password form.</summary>
public sealed class ChangePasswordViewModel
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter your current password.")]
    [System.ComponentModel.DataAnnotations.DataType(System.ComponentModel.DataAnnotations.DataType.Password)]
    [System.ComponentModel.DataAnnotations.Display(Name = "Current password")]
    public string CurrentPassword { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Choose a new password.")]
    [System.ComponentModel.DataAnnotations.StringLength(100, MinimumLength = 8, ErrorMessage = "Use at least 8 characters.")]
    [System.ComponentModel.DataAnnotations.DataType(System.ComponentModel.DataAnnotations.DataType.Password)]
    [System.ComponentModel.DataAnnotations.Display(Name = "New password")]
    public string NewPassword { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.DataType(System.ComponentModel.DataAnnotations.DataType.Password)]
    [System.ComponentModel.DataAnnotations.Display(Name = "Confirm new password")]
    [System.ComponentModel.DataAnnotations.Compare(nameof(NewPassword), ErrorMessage = "The passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

/// <summary>Backing model for the "email me a reset link" form.</summary>
public sealed class ForgotPasswordViewModel
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter your email address.")]
    [System.ComponentModel.DataAnnotations.EmailAddress(ErrorMessage = "That does not look like a valid email address.")]
    [System.ComponentModel.DataAnnotations.StringLength(256)]
    [System.ComponentModel.DataAnnotations.Display(Name = "Email address")]
    public string Email { get; set; } = string.Empty;
}

/// <summary>Backing model for the "choose a new password" form, carrying the reset token.</summary>
public sealed class ResetPasswordViewModel
{
    /// <summary>The signed, expiring token from the reset link. Posted back unchanged.</summary>
    public string Token { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Choose a new password.")]
    [System.ComponentModel.DataAnnotations.StringLength(100, MinimumLength = 8, ErrorMessage = "Use at least 8 characters.")]
    [System.ComponentModel.DataAnnotations.DataType(System.ComponentModel.DataAnnotations.DataType.Password)]
    [System.ComponentModel.DataAnnotations.Display(Name = "New password")]
    public string Password { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.DataType(System.ComponentModel.DataAnnotations.DataType.Password)]
    [System.ComponentModel.DataAnnotations.Display(Name = "Confirm new password")]
    [System.ComponentModel.DataAnnotations.Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

/// <summary>Backing model for creating or editing an address book entry.</summary>
public sealed class AddressFormModel
{
    public int Id { get; set; }

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter a recipient name.")]
    [System.ComponentModel.DataAnnotations.StringLength(120)]
    [System.ComponentModel.DataAnnotations.Display(Name = "Full name")]
    public string FullName { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter a street address.")]
    [System.ComponentModel.DataAnnotations.StringLength(200)]
    [System.ComponentModel.DataAnnotations.Display(Name = "Address line 1")]
    public string Line1 { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.StringLength(200)]
    [System.ComponentModel.DataAnnotations.Display(Name = "Address line 2")]
    public string? Line2 { get; set; }

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter a city.")]
    [System.ComponentModel.DataAnnotations.StringLength(100)]
    public string City { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.StringLength(100)]
    [System.ComponentModel.DataAnnotations.Display(Name = "State / region")]
    public string? State { get; set; }

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter a postal code.")]
    [System.ComponentModel.DataAnnotations.StringLength(20)]
    [System.ComponentModel.DataAnnotations.Display(Name = "Postal code")]
    public string PostalCode { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter a country.")]
    [System.ComponentModel.DataAnnotations.StringLength(80)]
    public string Country { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Phone]
    [System.ComponentModel.DataAnnotations.StringLength(40)]
    public string? Phone { get; set; }

    [System.ComponentModel.DataAnnotations.Display(Name = "Use as my default address")]
    public bool IsDefault { get; set; }
}
