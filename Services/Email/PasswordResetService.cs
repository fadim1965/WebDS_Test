using Microsoft.AspNetCore.DataProtection;

namespace WebDS_Test.Services.Email;

/// <summary>
/// Mints and redeems password-reset tokens.
/// </summary>
/// <remarks>
/// <para>
/// A reset link has to be unguessable and short-lived, but it also has to survive a round trip
/// through an email client — so it is a self-describing token rather than a row in the database.
/// The token is the user id and their <c>SecurityStamp</c>, sealed with ASP.NET Core Data
/// Protection and given a lifetime. Nothing has to be stored, and the token stops working on its
/// own the moment it expires.
/// </para>
/// <para>
/// Binding the token to the security stamp means the link is invalidated by the very act it
/// performs: resetting the password rotates the stamp, so an old link — or a second copy of the
/// same one — cannot be replayed.
/// </para>
/// </remarks>
public interface IPasswordResetService
{
    /// <summary>
    /// Builds a reset URL for the account with this email address, or returns null when there is no
    /// usable account. Callers must show the same message either way, so the endpoint cannot be used
    /// to discover which addresses are registered.
    /// </summary>
    Task<string?> CreateResetUrlAsync(string email, string resetActionUrl, CancellationToken ct = default);

    /// <summary>
    /// Builds a reset link for the address and queues it for delivery. Returns true when a link was
    /// sent. Like <see cref="CreateResetUrlAsync"/> it reveals nothing: a missing account simply
    /// returns false, and the caller shows the same confirmation either way.
    /// </summary>
    Task<bool> SendResetLinkAsync(string email, string resetActionUrl, CancellationToken ct = default);

    /// <summary>
    /// Whether a token is currently usable, without consuming it. Lets the reset form refuse a stale
    /// link up front instead of letting somebody type a new password only to be told it has expired.
    /// </summary>
    Task<bool> IsTokenValidAsync(string token, CancellationToken ct = default);

    /// <summary>Validates a token and, when it is good, sets the new password.</summary>
    Task<AccountResult> ResetPasswordAsync(string token, string newPassword, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class PasswordResetService : IPasswordResetService
{
    /// <summary>Versioned so the token format can change without invalidating older keys.</summary>
    private const string ProtectorPurpose = "WebDS_Test.PasswordReset.v1";

    private const int MinimumPasswordLength = 8;

    private readonly ApplicationDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly ITimeLimitedDataProtector _protector;
    private readonly IEmailService _emails;
    private readonly EmailOptions _options;
    private readonly ILogger<PasswordResetService> _logger;

    public PasswordResetService(
        ApplicationDbContext db,
        IPasswordHasher hasher,
        IDataProtectionProvider protection,
        IEmailService emails,
        IOptions<EmailOptions> options,
        ILogger<PasswordResetService> logger)
    {
        _db = db;
        _hasher = hasher;
        _protector = protection.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();
        _emails = emails;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string?> CreateResetUrlAsync(string email, string resetActionUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var normalized = email.Trim().ToUpperInvariant();

        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);

        // Inactive accounts get no link. Returning null here (rather than throwing) keeps the
        // caller's response identical to the "no such account" case.
        if (user is null || !user.IsActive)
        {
            return null;
        }

        return BuildUrl(user.Id, user.SecurityStamp, resetActionUrl);
    }

    public async Task<bool> SendResetLinkAsync(string email, string resetActionUrl, CancellationToken ct = default)
    {
        var url = await CreateResetUrlAsync(email, resetActionUrl, ct);
        if (url is null)
        {
            return false;
        }

        var normalized = email.Trim().ToUpperInvariant();
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);
        if (user is null)
        {
            return false;
        }

        await _emails.NotifyPasswordResetAsync(user, url, ct);
        _logger.LogInformation("Queued a password-reset email for {Email}", user.Email);
        return true;
    }

    private string BuildUrl(string userId, string securityStamp, string resetActionUrl)
    {
        var token = _protector.Protect(
            $"{userId}|{securityStamp}",
            TimeSpan.FromMinutes(Math.Clamp(_options.PasswordResetMinutes, 5, 1440)));

        var separator = resetActionUrl.Contains('?') ? '&' : '?';
        return $"{resetActionUrl}{separator}token={Uri.EscapeDataString(token)}";
    }

    public async Task<bool> IsTokenValidAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var parts = _protector.Unprotect(token).Split('|');
            if (parts.Length != 2)
            {
                return false;
            }

            var (userId, stamp) = (parts[0], parts[1]);

            return await _db.Users
                .AsNoTracking()
                .AnyAsync(u => u.Id == userId && u.IsActive && u.SecurityStamp == stamp, ct);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    public async Task<AccountResult> ResetPasswordAsync(string token, string newPassword, CancellationToken ct = default)
    {
        const string genericFailure = "That password reset link is not valid or has expired. Please request a new one.";

        if (string.IsNullOrWhiteSpace(token))
        {
            return AccountResult.Fail(genericFailure);
        }

        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < MinimumPasswordLength)
        {
            return AccountResult.Fail($"Choose a password of at least {MinimumPasswordLength} characters.");
        }

        string payload;
        try
        {
            payload = _protector.Unprotect(token);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Tampered with, or past its lifetime. Both are "ask for a new link".
            return AccountResult.Fail(genericFailure);
        }

        var parts = payload.Split('|');
        if (parts.Length != 2)
        {
            return AccountResult.Fail(genericFailure);
        }

        var (userId, stamp) = (parts[0], parts[1]);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive || !string.Equals(user.SecurityStamp, stamp, StringComparison.Ordinal))
        {
            return AccountResult.Fail(genericFailure);
        }

        user.PasswordHash = _hasher.Hash(newPassword);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.AccessFailedCount = 0;
        user.LockoutEndUtc = null;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Password reset completed for {Email}", user.Email);

        return AccountResult.Ok(user, "Your password has been changed. You can sign in with it now.");
    }
}
