using WebDS_Test.Services;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// The rate-limit knobs are configuration, and the policy names are a contract between the
/// registration in <c>Program.cs</c> and the <c>[EnableRateLimiting]</c> attributes on controllers.
/// A silent drift there would disable throttling without anyone noticing, so it is pinned here.
/// </summary>
public class RateLimitSettingsTests
{
    [Fact]
    public void The_limiter_is_on_by_default()
    {
        var settings = new RateLimitSettings();

        Assert.True(settings.Enabled);
    }

    [Fact]
    public void Every_policy_ships_with_a_sensible_default()
    {
        var settings = new RateLimitSettings();

        foreach (var rule in new[] { settings.Auth, settings.Checkout, settings.Review })
        {
            Assert.True(rule.PermitLimit > 0);
            Assert.True(rule.WindowSeconds > 0);
        }
    }

    [Fact]
    public void A_rule_defaults_to_thirty_requests_a_minute()
    {
        var rule = new RateLimitRule();

        Assert.Equal(30, rule.PermitLimit);
        Assert.Equal(60, rule.WindowSeconds);
    }

    [Fact]
    public void The_policy_names_are_distinct_and_match_the_attributes()
    {
        var names = new[] { RateLimitPolicies.Auth, RateLimitPolicies.Checkout, RateLimitPolicies.Review };

        Assert.Equal(3, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("auth", names);
        Assert.Contains("checkout", names);
        Assert.Contains("review", names);
    }
}
