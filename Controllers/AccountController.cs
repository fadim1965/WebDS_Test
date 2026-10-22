using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Models;
using WebDS_Test.Services.Email;

namespace WebDS_Test.Controllers;

/// <summary>Registration, sign-in, profile, password and address book.</summary>
public class AccountController : StoreControllerBase
{
    private readonly IAccountService _accounts;
    private readonly IOrderService _orders;
    private readonly IEmailService _emails;
    private readonly IPasswordResetService _passwordReset;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<AccountController> _logger;

    public AccountController(
        ICatalogService catalog,
        IOptions<StoreSettings> settings,
        ICartService cart,
        IAccountService accounts,
        IOrderService orders,
        IEmailService emails,
        IPasswordResetService passwordReset,
        ApplicationDbContext db,
        ILogger<AccountController> logger)
        : base(catalog, settings, cart)
    {
        _accounts = accounts;
        _orders = orders;
        _emails = emails;
        _passwordReset = passwordReset;
        _db = db;
        _logger = logger;
    }

    private string? CurrentUserId =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    // ------------------------------------------------------------------
    // Sign in / out
    // ------------------------------------------------------------------

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction(nameof(Profile));
        }

        ViewData["Title"] = "Sign in";
        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            ViewData["Title"] = "Sign in";
            return View(model);
        }

        var user = await _accounts.ValidateCredentialsAsync(model.Email, model.Password, ct);

        if (user is null)
        {
            // Deliberately generic: do not reveal whether the address is registered or locked.
            ModelState.AddModelError(string.Empty, "Those credentials did not match an account, or the account is temporarily locked.");
            ViewData["Title"] = "Sign in";
            return View(model);
        }

        // If 2FA is enabled, redirect to the challenge page instead of signing in directly.
        if (user.TwoFactorEnabled)
        {
            // Store the user ID and rememberMe in temp data for the 2FA challenge step.
            TempData["TwoFactorUserId"] = user.Id;
            TempData["TwoFactorRememberMe"] = model.RememberMe;
            TempData["TwoFactorReturnUrl"] = model.ReturnUrl;

            return RedirectToAction(nameof(TwoFactorChallenge), new { returnUrl = model.ReturnUrl });
        }

        await SignInAsync(user, model.RememberMe, ct);
        await _accounts.RecordLoginAsync(user.Id, ct);

        // Anything the shopper collected while anonymous becomes theirs.
        await CartService.MergeAnonymousCartAsync(user.Id, ct);

        Flash($"Welcome back, {user.DisplayName}.");

        var roles = await _accounts.GetRolesAsync(user.Id, ct);
        if (!string.IsNullOrWhiteSpace(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
        {
            return Redirect(model.ReturnUrl);
        }

        return roles.Contains(AccountService.AdminRole)
            ? RedirectToAction("Index", "Dashboard", new { area = "Admin" })
            : RedirectToAction(nameof(Profile));
    }

    // ------------------------------------------------------------------
    // Two-factor authentication challenge
    // ------------------------------------------------------------------

    [HttpGet]
    [AllowAnonymous]
    public IActionResult TwoFactorChallenge(string? returnUrl = null)
    {
        var userId = TempData.Peek("TwoFactorUserId") as string;
        if (string.IsNullOrEmpty(userId))
        {
            return RedirectToAction(nameof(Login));
        }

        TempData.Keep("TwoFactorUserId");
        TempData.Keep("TwoFactorRememberMe");
        TempData.Keep("TwoFactorReturnUrl");

        ViewData["Title"] = "Two-factor authentication";
        return View(new TwoFactorChallengeViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> TwoFactorChallenge(TwoFactorChallengeViewModel model, CancellationToken ct)
    {
        var userId = TempData["TwoFactorUserId"] as string;
        var rememberMe = TempData["TwoFactorRememberMe"] as bool? ?? false;
        var returnUrl = TempData["TwoFactorReturnUrl"] as string ?? model.ReturnUrl;

        if (string.IsNullOrEmpty(userId))
        {
            return RedirectToAction(nameof(Login));
        }

        if (!ModelState.IsValid)
        {
            ViewData["Title"] = "Two-factor authentication";
            return View(model);
        }

        var user = await _accounts.GetByIdAsync(userId, ct);
        if (user is null || !user.TwoFactorEnabled)
        {
            TempData.Clear();
            return RedirectToAction(nameof(Login));
        }

        var twoFactor = new TwoFactorService();
        var code = model.Code?.Trim() ?? string.Empty;

        // Try TOTP code first
        var valid = twoFactor.ValidateCode(user.TwoFactorSecret!, code);

        // If not valid, try backup code
        var backupUsed = false;
        if (!valid && !string.IsNullOrWhiteSpace(user.TwoFactorRecoveryCodes))
        {
            var codesJson = user.TwoFactorRecoveryCodes;
            backupUsed = twoFactor.ConsumeBackupCode(ref codesJson, code);
            if (backupUsed)
            {
                user.TwoFactorRecoveryCodes = codesJson;
                await _db.SaveChangesAsync(ct);
            }
        }

        if (!valid && !backupUsed)
        {
            ModelState.AddModelError(string.Empty, "The code is invalid or has already been used.");
            ViewData["Title"] = "Two-factor authentication";
            return View(model);
        }

        TempData.Clear();

        await SignInAsync(user, rememberMe, ct);
        await _accounts.RecordLoginAsync(user.Id, ct);

        // Anything the shopper collected while anonymous becomes theirs.
        await CartService.MergeAnonymousCartAsync(user.Id, ct);

        Flash($"Welcome back, {user.DisplayName}.");

        var roles = await _accounts.GetRolesAsync(user.Id, ct);
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        var rolesList = await _accounts.GetRolesAsync(user.Id, ct);
        return rolesList.Contains(AccountService.AdminRole)
            ? RedirectToAction("Index", "Dashboard", new { area = "Admin" })
            : RedirectToAction(nameof(Profile));
    }

    // ------------------------------------------------------------------
    // Two-factor authentication setup (authenticated users only)
    // ------------------------------------------------------------------

    [HttpGet]
    public async Task<IActionResult> TwoFactor(CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (string.IsNullOrEmpty(userId))
        {
            return Challenge();
        }

        var user = await _accounts.GetByIdAsync(userId, ct);
        if (user is null)
        {
            return NotFound();
        }

        ViewData["Title"] = "Two-factor authentication";
        return View(new TwoFactorViewModel
        {
            IsEnabled = user.TwoFactorEnabled,
            BackupCodesCount = user.TwoFactorRecoveryCodes != null
                ? JsonSerializer.Deserialize<string[]>(user.TwoFactorRecoveryCodes)?.Length ?? 0
                : 0
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnableTwoFactor(string password, CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (string.IsNullOrEmpty(userId))
        {
            return Challenge();
        }

        var result = await _accounts.EnableTwoFactorAsync(userId, password, ct);

        if (!result.Success)
        {
            Flash(result.Message, false);
            return RedirectToAction(nameof(TwoFactor));
        }

        // The first time 2FA is enabled, we show the secret and backup codes.
        // We pass them via TempData so they appear on the confirmation screen.
        TempData["TwoFactorSecret"] = result.Secret;
        TempData["TwoFactorQrUri"] = result.QrCodeUri;
        TempData["TwoFactorBackupCodes"] = result.BackupCodes;

        Flash("Two-factor authentication enabled. Scan the QR code with your authenticator app and save the backup codes.");
        return RedirectToAction(nameof(TwoFactorConfirmed));
    }

    [HttpGet]
    public IActionResult TwoFactorConfirmed()
    {
        var secret = TempData["TwoFactorSecret"] as string;
        var qrUri = TempData["TwoFactorQrUri"] as string;
        var backupCodes = TempData["TwoFactorBackupCodes"] as string[];

        if (string.IsNullOrEmpty(secret))
        {
            return RedirectToAction(nameof(TwoFactor));
        }

        ViewData["Title"] = "Two-factor authentication enabled";
        return View(new TwoFactorConfirmedViewModel
        {
            Secret = secret,
            QrCodeUri = qrUri,
            BackupCodes = backupCodes ?? Array.Empty<string>()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DisableTwoFactor(string password, CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (string.IsNullOrEmpty(userId))
        {
            return Challenge();
        }

        var result = await _accounts.DisableTwoFactorAsync(userId, password, ct);
        Flash(result.Message, result.Success);

        return RedirectToAction(nameof(TwoFactor));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegenerateBackupCodes(string password, CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (string.IsNullOrEmpty(userId))
        {
            return Challenge();
        }

        var result = await _accounts.RegenerateBackupCodesAsync(userId, password, ct);

        if (!result.Success)
        {
            Flash(result.Message, false);
            return RedirectToAction(nameof(TwoFactor));
        }

        TempData["TwoFactorBackupCodes"] = result.BackupCodes;
        Flash("New backup codes generated. Save them in a safe place.");
        return RedirectToAction(nameof(BackupCodesConfirmed));
    }

    [HttpGet]
    public IActionResult BackupCodesConfirmed()
    {
        var backupCodes = TempData["TwoFactorBackupCodes"] as string[];

        if (backupCodes is null || backupCodes.Length == 0)
        {
            return RedirectToAction(nameof(TwoFactor));
        }

        ViewData["Title"] = "New backup codes";
        return View(new BackupCodesConfirmedViewModel { BackupCodes = backupCodes });
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Register()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction(nameof(Profile));
        }

        ViewData["Title"] = "Create an account";
        return View(new RegisterViewModel());
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> Register(RegisterViewModel model, CancellationToken ct)
    {
        if (!model.AcceptTerms)
        {
            ModelState.AddModelError(nameof(model.AcceptTerms), "Please accept the terms to continue.");
        }

        if (!ModelState.IsValid)
        {
            ViewData["Title"] = "Create an account";
            return View(model);
        }

        var result = await _accounts.RegisterAsync(model.Email, model.DisplayName, model.Password, model.Phone, ct: ct);

        if (!result.Success || result.User is null)
        {
            ModelState.AddModelError(string.Empty, result.Message);
            ViewData["Title"] = "Create an account";
            return View(model);
        }

        await SignInAsync(result.User, rememberMe: true, ct);
        await CartService.MergeAnonymousCartAsync(result.User.Id, ct);

        // A welcome is a courtesy, not part of creating the account: if mail is down the shopper
        // still has an account, so a failure here is logged rather than surfaced.
        try
        {
            await _emails.NotifyWelcomeAsync(result.User, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not queue the welcome email for {Email}", result.User.Email);
        }

        Flash("Your account is ready. Happy shopping!");
        return RedirectToAction(nameof(Profile));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        HttpContext.Session.Remove(OrdersController.ViewableOrdersSessionKey);
        Flash("You have been signed out.");
        return RedirectToAction("Index", "Home");
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult AccessDenied()
    {
        ViewData["Title"] = "Access denied";
        return View();
    }

    // ------------------------------------------------------------------
    // Password reset
    //
    // Two steps: ask for a link, then use it. The "ask" step always reports success, whether or not
    // the address is registered, so it cannot be used to enumerate accounts. The "use" step carries
    // a signed, expiring token rather than a database row.
    // ------------------------------------------------------------------

    [HttpGet]
    [AllowAnonymous]
    public IActionResult ForgotPassword()
    {
        ViewData["Title"] = "Reset your password";
        return View(new ForgotPasswordViewModel());
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            ViewData["Title"] = "Reset your password";
            return View(model);
        }

        // The link points back at this same action, as an absolute URL the mail can carry.
        var resetUrl = Url.Action(nameof(ResetPassword), "Account", null, Request.Scheme)!;

        try
        {
            await _passwordReset.SendResetLinkAsync(model.Email, resetUrl, ct);
        }
        catch (Exception ex)
        {
            // Never let a mail failure change the response: that would leak whether the address
            // exists. The failure is in the log and in the Emails outbox.
            _logger.LogError(ex, "Could not queue a password-reset email for {Email}", model.Email);
        }

        // Deliberately the same message for a known and an unknown address.
        TempData["Message"] = "If that email address is registered, a reset link is on its way. Check your inbox.";
        TempData["MessageType"] = "success";
        return RedirectToAction(nameof(ForgotPassword));
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword(string? token, CancellationToken ct)
    {
        ViewData["Title"] = "Choose a new password";

        // Refuse a stale link now rather than after somebody has typed a new password.
        if (!await _passwordReset.IsTokenValidAsync(token ?? string.Empty, ct))
        {
            TempData["Message"] = "That password reset link is not valid or has expired. Please request a new one.";
            TempData["MessageType"] = "danger";
            return RedirectToAction(nameof(ForgotPassword));
        }

        return View(new ResetPasswordViewModel { Token = token! });
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            ViewData["Title"] = "Choose a new password";
            return View(model);
        }

        var result = await _passwordReset.ResetPasswordAsync(model.Token, model.Password, ct);

        if (!result.Success)
        {
            ModelState.AddModelError(string.Empty, result.Message);
            ViewData["Title"] = "Choose a new password";
            return View(model);
        }

        _logger.LogInformation("Password reset redeemed for {Email}", result.User?.Email);
        Flash(result.Message);
        return RedirectToAction(nameof(Login));
    }

    // ------------------------------------------------------------------
    // Profile
    // ------------------------------------------------------------------

    [HttpGet]
    [Authorize]
    public async Task<IActionResult> Profile(CancellationToken ct)
    {
        await LoadCartBadgeAsync(ct);

        var userId = CurrentUserId;
        if (userId is null)
        {
            return Challenge();
        }

        var user = await _accounts.GetByIdAsync(userId, ct);
        if (user is null)
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction(nameof(Login));
        }

        var orders = await _orders.GetOrdersForUserAsync(userId, ct);

        var model = new ProfileViewModel
        {
            User = user,
            Roles = await _accounts.GetRolesAsync(userId, ct),
            OrderCount = orders.Count,
            LifetimeSpend = orders
                .Where(o => o.Status is OrderStatus.Paid or OrderStatus.Shipped or OrderStatus.Delivered)
                .Sum(o => o.GrandTotal),
            Addresses = await _db.Addresses.AsNoTracking()
                .Where(a => a.UserId == userId)
                .OrderByDescending(a => a.IsDefault)
                .ThenBy(a => a.Id)
                .ToListAsync(ct),
            DisplayName = user.DisplayName,
            Phone = user.Phone
        };

        ViewData["Title"] = "Your account";
        return View(model);
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateProfile(ProfileViewModel model, CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (userId is null)
        {
            return Challenge();
        }

        if (!ModelState.IsValid)
        {
            Flash("Please check the highlighted fields.", success: false);
            return RedirectToAction(nameof(Profile));
        }

        var result = await _accounts.UpdateProfileAsync(userId, model.DisplayName, model.Phone, ct);
        Flash(result.Message, result.Success);

        // Keep the cookie's display name in step with the profile.
        if (result.Success && result.User is not null)
        {
            await SignInAsync(result.User, rememberMe: true, ct);
        }

        return RedirectToAction(nameof(Profile));
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model, CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (userId is null)
        {
            return Challenge();
        }

        if (!ModelState.IsValid)
        {
            Flash("Please check the password fields.", success: false);
            return RedirectToAction(nameof(Profile));
        }

        var result = await _accounts.ChangePasswordAsync(userId, model.CurrentPassword, model.NewPassword, ct);

        if (result.Success)
        {
            // Re-issue the cookie against the new security stamp so the shopper stays signed in.
            var user = await _accounts.GetByIdAsync(userId, ct);
            if (user is not null)
            {
                await SignInAsync(user, rememberMe: true, ct);
            }
        }

        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Profile));
    }

    // ------------------------------------------------------------------
    // Wishlist
    // ------------------------------------------------------------------

    [HttpGet]
    [Authorize]
    public async Task<IActionResult> Wishlist(CancellationToken ct)
    {
        await LoadCartBadgeAsync(ct);

        var userId = CurrentUserId;
        if (userId is null)
        {
            return Challenge();
        }

        var items = await _db.WishlistItems.AsNoTracking()
            .Include(w => w.Product).ThenInclude(p => p!.Category)
            .Include(w => w.Product).ThenInclude(p => p!.Variants)
            .Include(w => w.Product).ThenInclude(p => p!.Images)
            .Where(w => w.UserId == userId)
            .OrderByDescending(w => w.AddedUtc)
            .ToListAsync(ct);

        ViewData["Title"] = "Your wishlist";
        return View(items);
    }

    // ------------------------------------------------------------------
    // Address book
    // ------------------------------------------------------------------

    [HttpGet]
    [Authorize]
    public async Task<IActionResult> Addresses(CancellationToken ct)
    {
        await LoadCartBadgeAsync(ct);

        var userId = CurrentUserId;
        if (userId is null)
        {
            return Challenge();
        }

        ViewData["Title"] = "Address book";
        return View(await LoadAddressesAsync(userId, ct));
    }

    [HttpGet]
    [Authorize]
    public async Task<IActionResult> EditAddress(int? id, CancellationToken ct)
    {
        await LoadCartBadgeAsync(ct);

        var userId = CurrentUserId;
        if (userId is null)
        {
            return Challenge();
        }

        if (id is null)
        {
            ViewData["Title"] = "Add an address";
            // Default to the store's home country rather than a hard-coded one.
            return View(new AddressFormModel { Country = Settings.DefaultCountry });
        }

        var address = await _db.Addresses.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct);

        if (address is null)
        {
            return NotFound();
        }

        ViewData["Title"] = "Edit address";
        return View(new AddressFormModel
        {
            Id = address.Id,
            FullName = address.FullName,
            Line1 = address.Line1,
            Line2 = address.Line2,
            City = address.City,
            State = address.State,
            PostalCode = address.PostalCode,
            Country = address.Country,
            Phone = address.Phone,
            IsDefault = address.IsDefault
        });
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditAddress(AddressFormModel model, CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (userId is null)
        {
            return Challenge();
        }

        if (!ModelState.IsValid)
        {
            await LoadCartBadgeAsync(ct);
            ViewData["Title"] = model.Id == 0 ? "Add an address" : "Edit address";
            return View(model);
        }

        Address address;
        if (model.Id == 0)
        {
            address = new Address { UserId = userId };
            _db.Addresses.Add(address);
        }
        else
        {
            var existing = await _db.Addresses.FirstOrDefaultAsync(a => a.Id == model.Id && a.UserId == userId, ct);
            if (existing is null)
            {
                return NotFound();
            }

            address = existing;
        }

        address.FullName = model.FullName.Trim();
        address.Line1 = model.Line1.Trim();
        address.Line2 = string.IsNullOrWhiteSpace(model.Line2) ? null : model.Line2.Trim();
        address.City = model.City.Trim();
        address.State = string.IsNullOrWhiteSpace(model.State) ? null : model.State.Trim();
        address.PostalCode = model.PostalCode.Trim();
        address.Country = model.Country.Trim();
        address.Phone = string.IsNullOrWhiteSpace(model.Phone) ? null : model.Phone.Trim();

        if (model.IsDefault)
        {
            // Only one default at a time.
            var others = await _db.Addresses.Where(a => a.UserId == userId && a.Id != address.Id).ToListAsync(ct);
            foreach (var other in others)
            {
                other.IsDefault = false;
            }

            address.IsDefault = true;
        }
        else if (address.IsDefault && model.Id != 0)
        {
            address.IsDefault = false;
        }

        await _db.SaveChangesAsync(ct);

        // Guarantee the customer always has one default address.
        if (!await _db.Addresses.AnyAsync(a => a.UserId == userId && a.IsDefault, ct))
        {
            var first = await _db.Addresses.Where(a => a.UserId == userId).OrderBy(a => a.Id).FirstOrDefaultAsync(ct);
            if (first is not null)
            {
                first.IsDefault = true;
                await _db.SaveChangesAsync(ct);
            }
        }

        Flash(model.Id == 0 ? "Address added." : "Address updated.");
        return RedirectToAction(nameof(Addresses));
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAddress(int id, CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (userId is null)
        {
            return Challenge();
        }

        var address = await _db.Addresses.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct);
        if (address is null)
        {
            return NotFound();
        }

        var wasDefault = address.IsDefault;
        _db.Addresses.Remove(address);
        await _db.SaveChangesAsync(ct);

        if (wasDefault)
        {
            var next = await _db.Addresses.Where(a => a.UserId == userId).OrderBy(a => a.Id).FirstOrDefaultAsync(ct);
            if (next is not null)
            {
                next.IsDefault = true;
                await _db.SaveChangesAsync(ct);
            }
        }

        Flash("Address removed.");
        return RedirectToAction(nameof(Addresses));
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDefaultAddress(int id, CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (userId is null)
        {
            return Challenge();
        }

        var addresses = await _db.Addresses.Where(a => a.UserId == userId).ToListAsync(ct);
        if (addresses.All(a => a.Id != id))
        {
            return NotFound();
        }

        foreach (var address in addresses)
        {
            address.IsDefault = address.Id == id;
        }

        await _db.SaveChangesAsync(ct);

        Flash("Default address updated.");
        return RedirectToAction(nameof(Addresses));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<IReadOnlyList<Address>> LoadAddressesAsync(string userId, CancellationToken ct) =>
        await _db.Addresses.AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.IsDefault)
            .ThenBy(a => a.Id)
            .ToListAsync(ct);

    /// <summary>Issues the authentication cookie with the claims the app authorises against.</summary>
    private async Task SignInAsync(ApplicationUser user, bool rememberMe, CancellationToken ct)
    {
        var roles = await _accounts.GetRolesAsync(user.Id, ct);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Email, user.Email),
            new("security_stamp", user.SecurityStamp)
        };

        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = rememberMe,
                ExpiresUtc = DateTimeOffset.UtcNow.Add(rememberMe ? TimeSpan.FromDays(14) : TimeSpan.FromHours(8))
            });

        _logger.LogInformation("Signed in user {UserId} with roles {Roles}", user.Id, string.Join(", ", roles));
    }
}
