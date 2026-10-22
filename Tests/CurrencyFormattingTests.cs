using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// The dual-currency display: the base amount is the money of record and the alternate figure is
/// arithmetic the storefront does for the shopper, so the tests here pin the two things that
/// matter — the conversion/rounding and the fallback behaviour when no rate is configured.
/// </summary>
public class CurrencyFormattingTests
{
    /// <summary>
    /// A store plus a formatter over one shared InMemory database and one shared settings store.
    /// The service provider is built for real so <see cref="SiteSettingStore"/> exercises the same
    /// scope-per-write path it uses in the application.
    /// </summary>
    private static (ApplicationDbContext Db, ISiteSettingStore Settings, ICurrencyFormatter Money, ServiceProvider Provider)
        Build(string name, Action<StoreSettings>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(name));
        services.AddSingleton<ISiteSettingStore, SiteSettingStore>();

        var provider = services.BuildServiceProvider();
        var settings = TestSupport.Store(configure);
        var store = provider.GetRequiredService<ISiteSettingStore>();
        var money = new CurrencyFormatter(store, TestSupport.Options(settings));

        return (TestSupport.NewDb(name), store, money, provider);
    }

    [Fact]
    public void Format_pairs_the_base_amount_with_an_indicative_alternate_figure()
    {
        var (_, _, money, provider) = Build(TestSupport.NewDbName());
        using var _p = provider;

        Assert.True(money.IsDual);
        Assert.Equal("LBP", money.AlternateCode);
        Assert.Equal("$24.50 · ≈2,192,750 LL", money.Format(24.50m));
    }

    [Fact]
    public void Alternates_round_to_whole_units_away_from_zero()
    {
        var (_, _, money, provider) = Build(TestSupport.NewDbName(), s => s.AlternateRate = 3m);
        using var _p = provider;

        // 0.50 * 3 = 1.50 -> 2 with the store's away-from-zero rounding, never banker's 2.
        Assert.Equal(2m, money.ToAlternate(0.50m));
        Assert.Equal("$0.50 · ≈2 LL", money.Format(0.50m));
    }

    [Fact]
    public void Format_degenerates_to_the_base_amount_when_the_alternate_is_off()
    {
        var (_, _, money, provider) = Build(TestSupport.NewDbName(), s => s.AlternateRate = 0m);
        using var _p = provider;

        Assert.False(money.IsDual);
        Assert.Equal(string.Empty, money.FormatAlternate(24.50m));
        Assert.Equal("$24.50", money.Format(24.50m));
    }

    [Fact]
    public void An_empty_alternate_code_switches_the_figure_off_as_well()
    {
        var (_, _, money, provider) = Build(TestSupport.NewDbName(), s => s.AlternateCurrency = "");
        using var _p = provider;

        Assert.False(money.IsDual);
        Assert.Equal("$24.50", money.Format(24.50m));
    }

    [Fact]
    public async Task A_saved_rate_overrides_the_configuration_immediately()
    {
        var (db, settings, money, provider) = Build(TestSupport.NewDbName(), s => s.AlternateRate = 89_500m);
        using var _p = provider;

        // Before anyone saves anything the shipped configuration stands.
        Assert.Equal("$10.00 · ≈895,000 LL", money.Format(10.00m));

        await settings.SetAsync(SiteSettingKeys.AlternateRate, "100000", "admin");
        await settings.SetAsync(SiteSettingKeys.AlternateCurrencySymbol, "L.L.", "admin");

        // No restart, no re-warm: the very next render uses what the admin saved.
        Assert.Equal("$10.00 · ≈1,000,000 L.L.", money.Format(10.00m));

        // And it is durable in the table, not only in the cache.
        var row = await db.SiteSettings.SingleAsync(s => s.Key == SiteSettingKeys.AlternateRate);
        Assert.Equal("100000", row.Value);
        Assert.Equal("admin", row.UpdatedBy);
    }

    [Fact]
    public async Task EnsureLoadedAsync_picks_up_rows_written_by_another_process()
    {
        var name = TestSupport.NewDbName();

        // A row written through one store (a different process, in production).
        var (_, writer, _, writerProvider) = Build(name);
        using (writerProvider)
        {
            await writer.SetAsync(SiteSettingKeys.AlternateRate, "50000", "admin");
        }

        // A cold store over the same database sees nothing until it loads, then sees the row —
        // which is exactly the cold-at-startup path the program warms after seeding.
        var (_, fresh, money, freshProvider) = Build(name);
        using (freshProvider)
        {
            Assert.Null(fresh.Get(SiteSettingKeys.AlternateRate));

            await fresh.EnsureLoadedAsync();

            Assert.Equal(50_000m, money.AlternateRate);
            Assert.Equal("$2.00 · ≈100,000 LL", money.Format(2.00m));
        }
    }

    [Fact]
    public async Task A_row_survives_into_a_fresh_store_over_the_same_database()
    {
        var name = TestSupport.NewDbName();

        var (_, first, _, firstProvider) = Build(name);
        using (firstProvider)
        {
            await first.SetAsync(SiteSettingKeys.AlternateRate, "120000", "admin");
        }

        // A new process (here: a new store over the same database) warms from the rows.
        var (_, second, money, secondProvider) = Build(name);
        using (secondProvider)
        {
            await second.EnsureLoadedAsync();

            Assert.Equal(120_000m, money.AlternateRate);
            Assert.Equal("$1.00 · ≈120,000 LL", money.Format(1.00m));
        }
    }

    [Fact]
    public async Task A_corrupt_rate_falls_back_to_base_only_rather_than_throwing()
    {
        var (_, settings, money, provider) = Build(TestSupport.NewDbName());
        using var _p = provider;

        await settings.SetAsync(SiteSettingKeys.AlternateRate, "not-a-number", "admin");
        await settings.EnsureLoadedAsync();

        Assert.False(money.IsDual);
        Assert.Equal("$9.99", money.Format(9.99m));
    }

    [Fact]
    public void FormatBase_never_includes_the_alternate_figure()
    {
        var (_, _, money, provider) = Build(TestSupport.NewDbName());
        using var _p = provider;

        Assert.Equal("$24.50", money.FormatBase(24.50m));
    }
}
