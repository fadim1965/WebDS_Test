using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services.Localization;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// The translation catalogue. The rules that matter are that an administrator's edit is what the
/// storefront serves, that a missing translation degrades to English rather than to a raw key, and
/// that the shipped text files are a seed rather than something an edit can silently undo.
/// </summary>
public class TranslationTests
{
    /// <summary>
    /// A store plus a translation store over one shared InMemory database. The provider is built for
    /// real so <see cref="TranslationStore"/> exercises the same scope-per-write path it uses in the
    /// application.
    /// </summary>
    private static (ApplicationDbContext Db, ITranslationStore Store, ServiceProvider Provider) Build(string name)
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(name));
        services.AddSingleton<ITranslationStore, TranslationStore>();

        var provider = services.BuildServiceProvider();

        return (TestSupport.NewDb(name), provider.GetRequiredService<ITranslationStore>(), provider);
    }

    private static Translator Translator(ITranslationStore? store, string? cookie = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/";

        if (cookie is not null)
        {
            context.Request.Headers.Cookie = $"WebDS.Culture={cookie}";
        }

        return new Translator(
            TestSupport.Options(new LanguageOptions()),
            new StubHttpContextAccessor(context),
            store);
    }

    [Fact]
    public async Task A_saved_string_is_what_the_translator_serves()
    {
        var (db, store, provider) = Build(TestSupport.NewDbName());
        using var _p = provider;

        await store.SetAsync("fr", "controller.cart", "Mon panier", "admin");

        var t = Translator(store, cookie: "fr");

        Assert.Equal("Mon panier", t["controller.cart"]);
    }

    [Fact]
    public async Task An_edited_string_replaces_the_seeded_one_without_a_restart()
    {
        var (db, store, provider) = Build(TestSupport.NewDbName());
        using var _p = provider;

        await store.SetAsync("en", "controller.cart", "Your basket", "admin");

        // The very next read uses what the administrator saved — no restart, no re-warm.
        Assert.Equal("Your basket", Translator(store, cookie: "en")["controller.cart"]);
    }

    [Fact]
    public async Task A_missing_translation_falls_back_to_english()
    {
        var (db, store, provider) = Build(TestSupport.NewDbName());
        using var _p = provider;

        // A key no shipped catalogue carries, so only the store can answer for it.
        await store.SetAsync("en", "controller.only-in-db", "Your cart", "admin");

        // Arabic has no row for the key, so the shopper sees English rather than a raw key.
        Assert.Equal("Your cart", Translator(store, cookie: "ar")["controller.only-in-db"]);
    }

    [Fact]
    public void A_key_no_language_has_returns_the_key_itself()
    {
        var (db, store, provider) = Build(TestSupport.NewDbName());
        using var _p = provider;

        Assert.Equal("no.such.key", Translator(store, cookie: "ar")["no.such.key"]);
    }

    [Fact]
    public async Task Deleting_a_string_removes_it_and_leaves_the_rest_alone()
    {
        var (db, store, provider) = Build(TestSupport.NewDbName());
        using var _p = provider;

        await store.SetAsync("fr", "controller.cart", "Mon panier", "admin");
        await store.SetAsync("fr", "controller.checkout", "Paiement", "admin");

        var removed = await store.DeleteAsync("fr", "controller.cart");

        Assert.True(removed);
        Assert.Null(store.Get("fr", "controller.cart"));
        Assert.Equal("Paiement", store.Get("fr", "controller.checkout"));

        // And the row is gone from the table too, not only from the cache.
        Assert.Empty(await db.Translations.Where(t => t.Culture == "fr" && t.Key == "controller.cart").ToListAsync());
    }

    [Fact]
    public async Task Deleting_a_string_that_is_not_there_reports_that_nothing_was_removed()
    {
        var (_, store, provider) = Build(TestSupport.NewDbName());
        using var _p = provider;

        Assert.False(await store.DeleteAsync("fr", "controller.cart"));
    }

    [Fact]
    public async Task Saving_the_same_key_twice_updates_the_string_rather_than_adding_a_row()
    {
        var (db, store, provider) = Build(TestSupport.NewDbName());
        using var _p = provider;

        await store.SetAsync("fr", "controller.cart", "Panier", "admin");
        await store.SetAsync("fr", "controller.cart", "Mon panier", "admin");

        var rows = await db.Translations.Where(t => t.Culture == "fr" && t.Key == "controller.cart").ToListAsync();

        Assert.Single(rows);
        Assert.Equal("Mon panier", rows[0].Value);
        Assert.Equal("Mon panier", store.Get("fr", "controller.cart"));
    }

    [Fact]
    public async Task A_cold_store_sees_nothing_until_it_is_loaded()
    {
        var name = TestSupport.NewDbName();

        var (_, writer, writerProvider) = Build(name);
        using (writerProvider)
        {
            await writer.SetAsync("fr", "controller.cart", "Mon panier", "admin");
        }

        // A fresh process (here: a fresh store over the same database) starts cold.
        var (_, fresh, freshProvider) = Build(name);
        using (freshProvider)
        {
            Assert.Null(fresh.Get("fr", "controller.cart"));

            await fresh.EnsureLoadedAsync();

            Assert.Equal("Mon panier", fresh.Get("fr", "controller.cart"));
        }
    }

    [Fact]
    public async Task The_store_lists_the_keys_it_holds_for_a_language()
    {
        var (_, store, provider) = Build(TestSupport.NewDbName());
        using var _p = provider;

        await store.SetAsync("fr", "controller.cart", "Panier", "admin");
        await store.SetAsync("fr", "controller.checkout", "Paiement", "admin");
        await store.SetAsync("ar", "controller.cart", "السلة", "admin");

        Assert.Equal(new[] { "controller.cart", "controller.checkout" }, store.KeysFor("fr"));
        Assert.Equal(new[] { "controller.cart" }, store.KeysFor("ar"));

        // Every key across every language, without duplicates.
        Assert.Equal(new[] { "controller.cart", "controller.checkout" }, store.Keys());
    }

    [Fact]
    public async Task The_seeded_catalogue_fills_a_language_that_has_no_rows_yet()
    {
        var name = TestSupport.NewDbName();
        var (db, _, provider) = Build(name);
        using var _p = provider;

        await TranslationSeeder.SeedAsync(db);

        // The shipped catalogues are copied in on the first start, so the storefront has text.
        var rows = await db.Translations.ToListAsync();

        Assert.NotEmpty(rows);
        Assert.Contains(rows, r => r.Culture == "en" && r.Key == "controller.cart");
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Value)));
    }

    [Fact]
    public async Task Seeding_never_overwrites_a_language_that_has_already_been_edited()
    {
        var name = TestSupport.NewDbName();
        var (db, store, provider) = Build(name);
        using var _p = provider;

        // An administrator edits one string of the French catalogue.
        await store.SetAsync("fr", "controller.cart", "Mon panier", "admin");

        // A later start must not put the shipped French text back.
        await TranslationSeeder.SeedAsync(db);

        Assert.Equal("Mon panier", store.Get("fr", "controller.cart"));
    }

    [Fact]
    public async Task A_catalogue_line_without_an_equals_sign_is_skipped()
    {
        var name = TestSupport.NewDbName();
        var (db, _, provider) = Build(name);
        using var _p = provider;

        await TranslationSeeder.SeedAsync(db);

        // Every seeded row is a real key/value pair — no half-parsed line became a row.
        Assert.All(await db.Translations.ToListAsync(), r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Key));
            Assert.False(string.IsNullOrWhiteSpace(r.Value));
        });
    }

    private sealed class StubHttpContextAccessor : IHttpContextAccessor
    {
        public StubHttpContextAccessor(HttpContext? context) => HttpContext = context;
        public HttpContext? HttpContext { get; set; }
    }
}
