using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services;
using WebDS_Test.Services.Email;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// A reset link has to be unguessable, single-use and expiring. These tests drive the real service
/// with a real data-protection provider, a real PBKDF2 hasher and a real outbox, so the token round
/// trip and the "the link stops working once used" rule are exercised end to end.
/// </summary>
public class PasswordResetTests
{
    private const string ResetActionUrl = "https://shop.example/Account/ResetPassword";

    private sealed record Fixture(
        ApplicationDbContext Db,
        PasswordResetService Service,
        RecordingTransport Transport,
        IPasswordHasher Hasher);

    private static Fixture Build(EmailOptions? options = null)
    {
        var db = TestSupport.NewDb();
        var transport = new RecordingTransport();
        var emailOptions = options ?? TestSupport.EmailOptions();
        var hasher = new Pbkdf2PasswordHasher();

        var emails = new EmailService(
            db,
            transport,
            TestSupport.Options(emailOptions),
            TestSupport.Options(TestSupport.Store()),
            TestSupport.Options(TestSupport.Brand()),
            NullLogger<EmailService>.Instance);

        var service = new PasswordResetService(
            db,
            hasher,
            new EphemeralDataProtectionProvider(),
            emails,
            TestSupport.Options(emailOptions),
            NullLogger<PasswordResetService>.Instance);

        return new Fixture(db, service, transport, hasher);
    }

    private static string TokenFrom(string url)
    {
        const string marker = "token=";
        var start = url.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return Uri.UnescapeDataString(url[start..]);
    }

    [Fact]
    public async Task Creates_a_link_for_a_known_account()
    {
        var f = Build();
        f.Db.Users.Add(TestSupport.User(email: "known@example.com"));
        await f.Db.SaveChangesAsync();

        var url = await f.Service.CreateResetUrlAsync("known@example.com", ResetActionUrl);

        Assert.NotNull(url);
        Assert.StartsWith(ResetActionUrl + "?token=", url);
    }

    [Fact]
    public async Task The_lookup_ignores_the_casing_of_the_address()
    {
        var f = Build();
        f.Db.Users.Add(TestSupport.User(email: "known@example.com"));
        await f.Db.SaveChangesAsync();

        var url = await f.Service.CreateResetUrlAsync("  KNOWN@Example.COM  ", ResetActionUrl);

        Assert.NotNull(url);
    }

    [Fact]
    public async Task Returns_nothing_for_an_address_that_is_not_registered()
    {
        var f = Build();

        Assert.Null(await f.Service.CreateResetUrlAsync("nobody@example.com", ResetActionUrl));
    }

    [Fact]
    public async Task Returns_nothing_for_a_deactivated_account()
    {
        var f = Build();
        f.Db.Users.Add(TestSupport.User(email: "gone@example.com", active: false));
        await f.Db.SaveChangesAsync();

        Assert.Null(await f.Service.CreateResetUrlAsync("gone@example.com", ResetActionUrl));
    }

    [Fact]
    public async Task A_fresh_token_is_valid()
    {
        var f = Build();
        f.Db.Users.Add(TestSupport.User());
        await f.Db.SaveChangesAsync();

        var url = await f.Service.CreateResetUrlAsync("shopper@example.com", ResetActionUrl);

        Assert.True(await f.Service.IsTokenValidAsync(TokenFrom(url!)));
    }

    [Fact]
    public async Task A_used_token_stops_being_valid()
    {
        var f = Build();
        f.Db.Users.Add(TestSupport.User());
        await f.Db.SaveChangesAsync();
        var token = TokenFrom((await f.Service.CreateResetUrlAsync("shopper@example.com", ResetActionUrl))!);

        var result = await f.Service.ResetPasswordAsync(token, "BrandNewPass1");
        Assert.True(result.Success);

        // The stamp rotated, so the same link cannot be replayed.
        Assert.False(await f.Service.IsTokenValidAsync(token));
        Assert.False((await f.Service.ResetPasswordAsync(token, "AnotherPass1")).Success);
    }

    [Fact]
    public async Task Resetting_sets_the_new_password_and_rotates_the_stamp()
    {
        var f = Build();
        var user = TestSupport.User(passwordHash: new Pbkdf2PasswordHasher().Hash("OriginalPass1"));
        f.Db.Users.Add(user);
        await f.Db.SaveChangesAsync();
        var originalStamp = user.SecurityStamp;
        var token = TokenFrom((await f.Service.CreateResetUrlAsync("shopper@example.com", ResetActionUrl))!);

        var result = await f.Service.ResetPasswordAsync(token, "Replacement1");

        Assert.True(result.Success);

        var stored = await f.Db.Users.SingleAsync();
        Assert.True(f.Hasher.Verify("Replacement1", stored.PasswordHash));
        Assert.False(f.Hasher.Verify("OriginalPass1", stored.PasswordHash));
        Assert.NotEqual(originalStamp, stored.SecurityStamp);
        Assert.Equal(0, stored.AccessFailedCount);
        Assert.Null(stored.LockoutEndUtc);
    }

    [Fact]
    public async Task A_tampered_token_is_refused()
    {
        var f = Build();
        f.Db.Users.Add(TestSupport.User());
        await f.Db.SaveChangesAsync();

        Assert.False(await f.Service.IsTokenValidAsync("not-a-real-token"));
        var result = await f.Service.ResetPasswordAsync("not-a-real-token", "WhateverPass1");
        Assert.False(result.Success);
        Assert.Contains("not valid or has expired", result.Message);
    }

    [Fact]
    public async Task An_empty_token_is_refused_without_throwing()
    {
        var f = Build();

        Assert.False(await f.Service.IsTokenValidAsync(string.Empty));
        Assert.False((await f.Service.ResetPasswordAsync(string.Empty, "WhateverPass1")).Success);
    }

    [Fact]
    public async Task A_short_password_is_refused_and_changes_nothing()
    {
        var f = Build();
        var user = TestSupport.User(passwordHash: new Pbkdf2PasswordHasher().Hash("OriginalPass1"));
        f.Db.Users.Add(user);
        await f.Db.SaveChangesAsync();
        var token = TokenFrom((await f.Service.CreateResetUrlAsync("shopper@example.com", ResetActionUrl))!);

        var result = await f.Service.ResetPasswordAsync(token, "short");

        Assert.False(result.Success);
        Assert.Contains("at least 8", result.Message);

        var stored = await f.Db.Users.SingleAsync();
        Assert.True(f.Hasher.Verify("OriginalPass1", stored.PasswordHash));
        Assert.True(await f.Service.IsTokenValidAsync(token));   // the link is still good
    }

    [Fact]
    public async Task Sending_a_link_queues_the_reset_email()
    {
        var f = Build();
        f.Db.Users.Add(TestSupport.User(email: "shopper@example.com"));
        await f.Db.SaveChangesAsync();

        var sent = await f.Service.SendResetLinkAsync("shopper@example.com", ResetActionUrl);

        Assert.True(sent);
        var message = await f.Db.EmailMessages.SingleAsync();
        Assert.Equal(EmailKind.PasswordReset, message.Kind);
        Assert.Equal("shopper@example.com", message.ToAddress);
        Assert.Contains("token=", message.BodyText);
    }

    [Fact]
    public async Task Sending_to_an_unknown_address_reports_false_and_queues_nothing()
    {
        var f = Build();

        var sent = await f.Service.SendResetLinkAsync("nobody@example.com", ResetActionUrl);

        Assert.False(sent);
        Assert.Equal(0, await f.Db.EmailMessages.CountAsync());
    }

    [Fact]
    public async Task A_token_sealed_by_a_different_key_ring_is_refused()
    {
        var f = Build();
        f.Db.Users.Add(TestSupport.User());
        await f.Db.SaveChangesAsync();
        var token = TokenFrom((await f.Service.CreateResetUrlAsync("shopper@example.com", ResetActionUrl))!);

        // A second service built on its own ephemeral key ring stands in for "the keys were
        // rotated, or the link came from another deployment": it cannot unseal the token, which is
        // exactly the path a genuinely expired token takes (both surface as a CryptographicException).
        var stranger = Build();

        Assert.False(await stranger.Service.IsTokenValidAsync(token));
        Assert.False((await stranger.Service.ResetPasswordAsync(token, "WhateverPass1")).Success);
    }
}
