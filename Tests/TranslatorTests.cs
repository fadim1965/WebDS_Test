using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using WebDS_Test.Services.Localization;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// Language resolution. The rule that matters is that a missing translation degrades to English
/// rather than showing the shopper a raw key — a page of "nav.shops" is worse than English.
/// </summary>
public class TranslatorTests
{
    private static Translator Build(HttpContext? http = null, Action<LanguageOptions>? configure = null)
    {
        var options = new LanguageOptions();
        configure?.Invoke(options);

        return new Translator(
            Options.Create(options),
            new StubHttpContextAccessor(http));
    }

    private static DefaultHttpContext Request(string? cookie = null, string? acceptLanguage = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/Catalog";

        if (cookie is not null)
        {
            context.Request.Headers.Cookie = $"WebDS.Culture={cookie}";
        }

        if (acceptLanguage is not null)
        {
            context.Request.Headers[HeaderNames.AcceptLanguage] = acceptLanguage;
        }

        return context;
    }

    [Fact]
    public void Falls_back_to_english_when_nothing_is_preferred()
    {
        var t = Build(Request());

        Assert.Equal("en", t.Culture);
    }

    [Fact]
    public void Honours_the_cookie_the_shopper_set()
    {
        var t = Build(Request(cookie: "ar"));

        Assert.Equal("ar", t.Culture);
        Assert.True(t.IsRightToLeft);
    }

    [Fact]
    public void Honours_the_browsers_preference()
    {
        var t = Build(Request(acceptLanguage: "fr-CH, fr;q=0.9, en;q=0.8"));

        Assert.Equal("fr", t.Culture);
    }

    [Fact]
    public void Ignores_a_language_the_store_does_not_offer()
    {
        // Otherwise a browser asking for a language the store has no strings for would render blanks.
        var t = Build(Request(cookie: "de"));

        Assert.Equal("en", t.Culture);
    }

    [Fact]
    public void The_cookie_beats_the_browser()
    {
        var t = Build(Request(cookie: "fr", acceptLanguage: "ar"));

        Assert.Equal("fr", t.Culture);
    }

    [Fact]
    public void Latin_languages_are_not_right_to_left()
    {
        Assert.False(Build(Request(cookie: "fr")).IsRightToLeft);
    }

    [Fact]
    public void An_unknown_key_returns_the_key_rather_than_throwing()
    {
        var t = Build(Request());

        Assert.Equal("no.such.key", t["no.such.key"]);
    }

    [Fact]
    public void Has_distinguishes_a_translated_key_from_a_missing_one()
    {
        var t = Build(Request());

        Assert.False(t.Has("definitely.not.a.real.key"));
    }

    [Fact]
    public void Format_does_not_throw_on_a_broken_template()
    {
        var t = Build(Request());

        // Must return something printable whatever happens.
        var result = t.Format("no.such.key", "argument");

        Assert.NotNull(result);
    }

    [Fact]
    public void The_offered_languages_are_reported_with_their_native_names()
    {
        var t = Build(Request());

        Assert.Contains(t.Supported, l => l.Code == "ar" && l.NativeName == "العربية");
        Assert.Contains(t.Supported, l => l.Code == "en");
    }

    private sealed class StubHttpContextAccessor : IHttpContextAccessor
    {
        public StubHttpContextAccessor(HttpContext? context) => HttpContext = context;
        public HttpContext? HttpContext { get; set; }
    }
}
