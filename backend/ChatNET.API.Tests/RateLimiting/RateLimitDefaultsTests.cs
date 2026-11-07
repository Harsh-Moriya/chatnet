using System.ComponentModel.DataAnnotations;
using ChatNET.API.RateLimiting;
using Microsoft.Extensions.Configuration;

namespace ChatNET.API.Tests.RateLimiting;

// The numbers a deployment starts with. The test host overrides them so that
// no ordinary test is limited, which is why they are read from the file itself.
public class RateLimitDefaultsTests
{
    private static IConfigurationRoot Settings() =>
        new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();

    [Theory]
    [InlineData("auth", 10, 60)]
    [InlineData("refresh", 60, 60)]
    [InlineData("send", 30, 10)]
    [InlineData("uploads", 60, 3600)]
    [InlineData("edit", 20, 60)]
    [InlineData("search", 30, 10)]
    [InlineData("avatars", 600, 60)]
    [InlineData("push", 20, 60)]
    public void A_policy_starts_at_the_agreed_limit(string policy, int permits, int windowSeconds)
    {
        var section = RateLimitPolicies.All.Single(p => p.Name == policy).Section;

        var options = Settings().GetSection(section).Get<RateLimitPolicyOptions>()!;

        Assert.Equal(permits, options.PermitLimit);
        Assert.Equal(windowSeconds, options.WindowSeconds);
        Validator.ValidateObject(options, new ValidationContext(options), validateAllProperties: true);
    }

    [Fact]
    public void Every_policy_has_a_section_in_the_settings()
    {
        var settings = Settings();

        Assert.All(RateLimitPolicies.All, p =>
            Assert.True(settings.GetSection(p.Section).Exists(), $"{p.Section} is not in appsettings.json."));
    }

    [Fact]
    public void The_store_starts_with_a_short_wait_and_a_short_pause()
    {
        var options = Settings().GetSection(RateLimitStoreOptions.SectionName).Get<RateLimitStoreOptions>()!;

        Assert.Equal("ratelimit", options.KeyPrefix);
        Assert.Equal(200, options.TimeoutMilliseconds);
        Assert.Equal(5, options.PauseSeconds);
        Validator.ValidateObject(options, new ValidationContext(options), validateAllProperties: true);
    }
}
