using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services;
using WebDS_Test.Services.Email;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// Two-factor authentication: a user can enable TOTP-based 2FA, sign in with a code or backup code,
/// and manage backup codes.
/// </summary>
public class TwoFactorTests
{
    private static ApplicationDbContext NewDb() => TestSupport.NewDb();

    private static async Task<ApplicationDbContext> SeededAsync()
    {
        var db = NewDb();
        await TwoFactorSchema.EnsureAsync(db);

        var hasher = new Pbkdf2PasswordHasher();
        var user = TestSupport.User(email: "user@example.com", displayName: "Test User", passwordHash: hasher.Hash("password"));
        user.TwoFactorEnabled = false;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return db;
    }

    private static AccountService Accounts(ApplicationDbContext db) => new(
        db,
        new Pbkdf2PasswordHasher(),
        NullLogger<AccountService>.Instance);

    private static TwoFactorService TwoFactor() => new();

    [Fact]
    public async Task EnableTwoFactor_creates_secret_and_backup_codes()
    {
        await using var db = await SeededAsync();
        var accounts = Accounts(db);

        var user = await db.Users.FirstAsync();
        var result = await accounts.EnableTwoFactorAsync(user.Id, "password", CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Secret);
        Assert.NotNull(result.BackupCodes);
        Assert.Equal(10, result.BackupCodes.Length);

        var reloaded = await db.Users.FirstAsync();
        Assert.True(reloaded.TwoFactorEnabled);
        Assert.NotNull(reloaded.TwoFactorSecret);
        Assert.NotNull(reloaded.TwoFactorRecoveryCodes);
    }

    [Fact]
    public async Task EnableTwoFactor_fails_with_wrong_password()
    {
        await using var db = await SeededAsync();
        var accounts = Accounts(db);

        var user = await db.Users.FirstAsync();
        var result = await accounts.EnableTwoFactorAsync(user.Id, "wrong", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Your current password is incorrect.", result.Message);
    }

    [Fact]
    public async Task DisableTwoFactor_clears_secret_and_codes()
    {
        await using var db = await SeededAsync();
        var accounts = Accounts(db);

        var user = await db.Users.FirstAsync();
        await accounts.EnableTwoFactorAsync(user.Id, "password", CancellationToken.None);

        var result = await accounts.DisableTwoFactorAsync(user.Id, "password", CancellationToken.None);

        Assert.True(result.Success);

        var reloaded = await db.Users.FirstAsync();
        Assert.False(reloaded.TwoFactorEnabled);
        Assert.Null(reloaded.TwoFactorSecret);
        Assert.Null(reloaded.TwoFactorRecoveryCodes);
    }

    [Fact]
    public async Task DisableTwoFactor_fails_when_not_enabled()
    {
        await using var db = await SeededAsync();
        var accounts = Accounts(db);

        var user = await db.Users.FirstAsync();
        var result = await accounts.DisableTwoFactorAsync(user.Id, "password", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Two-factor authentication is not enabled.", result.Message);
    }

    [Fact]
    public async Task RegenerateBackupCodes_creates_new_codes()
    {
        await using var db = await SeededAsync();
        var accounts = Accounts(db);

        var user = await db.Users.FirstAsync();
        await accounts.EnableTwoFactorAsync(user.Id, "password", CancellationToken.None);
        var originalCodes = (await db.Users.FirstAsync()).TwoFactorRecoveryCodes;

        var result = await accounts.RegenerateBackupCodesAsync(user.Id, "password", CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.BackupCodes);
        Assert.Equal(10, result.BackupCodes.Length);

        var reloaded = await db.Users.FirstAsync();
        Assert.NotEqual(originalCodes, reloaded.TwoFactorRecoveryCodes);
    }

    [Fact]
    public async Task TOTP_code_validation_works()
    {
        var twoFactor = TwoFactor();
        var (secret, _) = twoFactor.GenerateSecret("user@example.com");

        var currentCode = twoFactor.GetCurrentCode(secret);
        Assert.True(twoFactor.ValidateCode(secret, currentCode));

        // Invalid code
        Assert.False(twoFactor.ValidateCode(secret, "000000"));
    }

    [Fact]
    public async Task Backup_code_can_only_be_used_once()
    {
        await using var db = await SeededAsync();
        var accounts = Accounts(db);
        var twoFactor = TwoFactor();

        var user = await db.Users.FirstAsync();
        var result = await accounts.EnableTwoFactorAsync(user.Id, "password", CancellationToken.None);
        var backupCode = result.BackupCodes![0];

        // First use succeeds
        var codesJson = (await db.Users.FirstAsync()).TwoFactorRecoveryCodes;
        Assert.True(twoFactor.ConsumeBackupCode(ref codesJson, backupCode));

        // Second use fails
        Assert.False(twoFactor.ConsumeBackupCode(ref codesJson, backupCode));
    }

    [Fact]
    public async Task Backup_codes_are_independent_of_TOTP()
    {
        var twoFactor = TwoFactor();
        var (secret, _) = twoFactor.GenerateSecret("user@example.com");
        var backupCodes = twoFactor.GenerateBackupCodes();

        var totpCode = twoFactor.GetCurrentCode(secret);
        var backupCode = backupCodes[0];

        Assert.True(twoFactor.ValidateCode(secret, totpCode));
        Assert.False(twoFactor.ValidateCode(secret, backupCode)); // TOTP validator doesn't accept backup codes

        var codesJson = JsonSerializer.Serialize(backupCodes);
        Assert.True(TwoFactor().ConsumeBackupCode(ref codesJson, backupCode));
    }

    [Fact]
    public void Backup_codes_are_alphanumeric_and_correct_length()
    {
        var twoFactor = TwoFactor();
        var codes = twoFactor.GenerateBackupCodes();

        Assert.Equal(10, codes.Length);
        foreach (var code in codes)
        {
            Assert.Equal(8, code.Length);
            Assert.Matches(@"^[A-Z0-9]+$", code);
        }
    }

    [Fact]
    public void TOTP_allows_clock_skew()
    {
        var twoFactor = TwoFactor();
        var (secret, _) = twoFactor.GenerateSecret("user@example.com");

        var currentCode = twoFactor.GetCurrentCode(secret);
        Assert.True(twoFactor.ValidateCode(secret, currentCode));

        // Simulate one step in the past and future
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var pastStep = (now / 30) - 1;
        var futureStep = (now / 30) + 1;

        var pastCode = twoFactor.ComputeTotpForTest(secret, pastStep);
        var futureCode = twoFactor.ComputeTotpForTest(secret, futureStep);

        Assert.True(twoFactor.ValidateCode(secret, pastCode.ToString("D6")));
        Assert.True(twoFactor.ValidateCode(secret, futureCode.ToString("D6")));
    }
}