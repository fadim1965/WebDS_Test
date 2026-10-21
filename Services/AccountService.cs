using Microsoft.EntityFrameworkCore;

namespace WebDS_Test.Services;

/// <summary>Outcome of an account operation, with an optional error key for the view.</summary>
public sealed record AccountResult(bool Success, string Message, ApplicationUser? User = null)
{
    public static AccountResult Ok(ApplicationUser user, string message) => new(true, message, user);
    public static AccountResult Fail(string message) => new(false, message);
}

/// <summary>Registration, sign-in and profile management for customers and administrators.</summary>
public interface IAccountService
{
    /// <summary>Validates credentials, honouring lockout. Returns null when the sign-in fails.</summary>
    Task<ApplicationUser?> ValidateCredentialsAsync(string email, string password, CancellationToken ct = default);

    Task<AccountResult> RegisterAsync(
        string email, string displayName, string password, string? phone = null,
        bool isAdmin = false, CancellationToken ct = default);

    Task<bool> EmailExistsAsync(string email, CancellationToken ct = default);

    Task<ApplicationUser?> GetByIdAsync(string userId, CancellationToken ct = default);

    Task<AccountResult> UpdateProfileAsync(string userId, string displayName, string? phone, CancellationToken ct = default);

    Task<AccountResult> ChangePasswordAsync(string userId, string currentPassword, string newPassword, CancellationToken ct = default);

    Task RecordLoginAsync(string userId, CancellationToken ct = default);

    Task RecordFailedLoginAsync(string email, CancellationToken ct = default);

    Task<IReadOnlyList<ApplicationUser>> GetCustomersAsync(string? search = null, CancellationToken ct = default);

    Task<AccountResult> SetActiveAsync(string userId, bool isActive, CancellationToken ct = default);

    /// <summary>Role names held by a user, used to build cookie claims.</summary>
    Task<IReadOnlyList<string>> GetRolesAsync(string userId, CancellationToken ct = default);
}

public sealed class AccountService : IAccountService
{
    /// <summary>Five failures locks the account for fifteen minutes.</summary>
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public const string AdminRole = "Admin";
    public const string CustomerRole = "Customer";

    private readonly ApplicationDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly ILogger<AccountService> _logger;

    public AccountService(ApplicationDbContext db, IPasswordHasher hasher, ILogger<AccountService> logger)
    {
        _db = db;
        _hasher = hasher;
        _logger = logger;
    }

    private static string Normalize(string email) => email.Trim().ToUpperInvariant();

    public async Task<ApplicationUser?> ValidateCredentialsAsync(string email, string password, CancellationToken ct = default)
    {
        var normalized = Normalize(email);

        var user = await _db.Users
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);

        if (user is null)
        {
            // Burn roughly the same time as a real verification so the response time does not
            // reveal whether the address is registered.
            _hasher.Verify(password, "210000.AAAAAAAAAAAAAAAAAAAAAA=.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
            return null;
        }

        if (!user.IsActive)
        {
            _logger.LogWarning("Sign-in blocked for deactivated account {UserId}", user.Id);
            return null;
        }

        if (user.IsLockedOut)
        {
            _logger.LogWarning("Sign-in blocked for locked-out account {UserId}", user.Id);
            return null;
        }

        if (!_hasher.Verify(password, user.PasswordHash))
        {
            await RecordFailedLoginAsync(email, ct);
            return null;
        }

        return user;
    }

    public async Task<AccountResult> RegisterAsync(
        string email, string displayName, string password, string? phone = null,
        bool isAdmin = false, CancellationToken ct = default)
    {
        var normalized = Normalize(email);

        if (await _db.Users.AnyAsync(u => u.NormalizedEmail == normalized, ct))
        {
            return AccountResult.Fail("An account with that email address already exists.");
        }

        var user = new ApplicationUser
        {
            Email = email.Trim(),
            NormalizedEmail = normalized,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? email.Split('@')[0] : displayName.Trim(),
            Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(),
            PasswordHash = _hasher.Hash(password),
            IsActive = true,
            CreatedUtc = DateTime.UtcNow
        };

        user.UserRoles.Add(new UserRole { UserId = user.Id, RoleName = CustomerRole });
        if (isAdmin)
        {
            user.UserRoles.Add(new UserRole { UserId = user.Id, RoleName = AdminRole });
        }

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Registered user {UserId} ({Email})", user.Id, user.Email);
        return AccountResult.Ok(user, "Your account has been created.");
    }

    public Task<bool> EmailExistsAsync(string email, CancellationToken ct = default)
    {
        var normalized = Normalize(email);
        return _db.Users.AnyAsync(u => u.NormalizedEmail == normalized, ct);
    }

    public Task<ApplicationUser?> GetByIdAsync(string userId, CancellationToken ct = default) =>
        _db.Users.Include(u => u.UserRoles).FirstOrDefaultAsync(u => u.Id == userId, ct);

    public async Task<AccountResult> UpdateProfileAsync(
        string userId, string displayName, string? phone, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return AccountResult.Fail("Account not found.");
        }

        user.DisplayName = displayName.Trim();
        user.Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        await _db.SaveChangesAsync(ct);

        return AccountResult.Ok(user, "Profile updated.");
    }

    public async Task<AccountResult> ChangePasswordAsync(
        string userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return AccountResult.Fail("Account not found.");
        }

        if (!_hasher.Verify(currentPassword, user.PasswordHash))
        {
            return AccountResult.Fail("Your current password is incorrect.");
        }

        user.PasswordHash = _hasher.Hash(newPassword);
        // A new security stamp invalidates cookies issued against the old password.
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Password changed for user {UserId}", userId);
        return AccountResult.Ok(user, "Your password has been changed.");
    }

    public async Task RecordLoginAsync(string userId, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return;
        }

        user.LastLoginUtc = DateTime.UtcNow;
        user.AccessFailedCount = 0;
        user.LockoutEndUtc = null;
        await _db.SaveChangesAsync(ct);
    }

    public async Task RecordFailedLoginAsync(string email, CancellationToken ct = default)
    {
        var normalized = Normalize(email);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);
        if (user is null)
        {
            return;
        }

        user.AccessFailedCount++;

        if (user.AccessFailedCount >= MaxFailedAttempts)
        {
            user.LockoutEndUtc = DateTime.UtcNow.Add(LockoutDuration);
            user.AccessFailedCount = 0;
            _logger.LogWarning("Account {UserId} locked until {LockoutEnd} after repeated failures",
                user.Id, user.LockoutEndUtc);
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ApplicationUser>> GetCustomersAsync(string? search = null, CancellationToken ct = default)
    {
        IQueryable<ApplicationUser> q = _db.Users.AsNoTracking().Include(u => u.UserRoles);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim().Replace("%", "\\%").Replace("_", "\\_")}%";
            q = q.Where(u =>
                EF.Functions.Like(u.Email, term, "\\") ||
                EF.Functions.Like(u.DisplayName, term, "\\"));
        }

        return await q.OrderByDescending(u => u.CreatedUtc).ToListAsync(ct);
    }

    public async Task<AccountResult> SetActiveAsync(string userId, bool isActive, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return AccountResult.Fail("Account not found.");
        }

        user.IsActive = isActive;
        if (!isActive)
        {
            user.SecurityStamp = Guid.NewGuid().ToString("N");
        }

        await _db.SaveChangesAsync(ct);

        return AccountResult.Ok(user, isActive ? "Account reactivated." : "Account deactivated.");
    }

    public async Task<IReadOnlyList<string>> GetRolesAsync(string userId, CancellationToken ct = default) =>
        await _db.UserRoles.AsNoTracking()
            .Where(r => r.UserId == userId)
            .Select(r => r.RoleName)
            .ToListAsync(ct);
}
