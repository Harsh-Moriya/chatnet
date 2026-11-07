using ChatNET.API.Auth.PasswordReset;
using ChatNET.API.Common.Hosting;
using ChatNET.API.Email;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Tests.Auth;

// The settings of password reset are validated when the host starts. The link in the email is built from Frontend:Url, so a wrong
// one is found at the start and not by the first person who asks for a link.
public class PasswordResetOptionsTests
{
    private static PasswordResetOptionsValidator ValidatorFor(bool emailOn, string frontendUrl) => new(
        Options.Create(emailOn ? new EmailOptions { Smtp = new SmtpOptions { Host = "smtp.example.test" } } : new EmailOptions()),
        Options.Create(new FrontendOptions { Url = frontendUrl }));

    private static ValidateOptionsResult Check(PasswordResetOptions options, bool emailOn = true, string frontendUrl = "https://chat.example.test") =>
        ValidatorFor(emailOn, frontendUrl).Validate(null, options);

    [Fact]
    public void The_defaults_are_a_thirty_minute_link_sent_within_ten_minutes_of_the_request()
    {
        var options = new PasswordResetOptions();

        Assert.Equal(TimeSpan.FromMinutes(30), options.TokenLifetime);
        Assert.Equal(TimeSpan.FromMinutes(10), options.SendWithin);
        Assert.True(Check(options).Succeeded);
    }

    [Fact]
    public void A_lifetime_under_a_minute_is_refused()
    {
        var result = Check(new PasswordResetOptions { TokenLifetime = TimeSpan.FromSeconds(59) });

        Assert.True(result.Failed);
        Assert.Contains("PasswordReset:TokenLifetime", result.FailureMessage);
    }

    [Fact]
    public void A_lifetime_of_exactly_a_minute_is_accepted()
    {
        Assert.True(Check(new PasswordResetOptions { TokenLifetime = TimeSpan.FromMinutes(1) }).Succeeded);
    }

    [Fact]
    public void A_send_window_of_nothing_is_refused()
    {
        var result = Check(new PasswordResetOptions { SendWithin = TimeSpan.Zero });

        Assert.True(result.Failed);
        Assert.Contains("PasswordReset:SendWithin", result.FailureMessage);
    }

    [Theory]
    [InlineData("https://chat.example.test")]
    [InlineData("https://chat.example.test/")]
    [InlineData("https://chat.example.test:8443")]
    [InlineData("http://localhost:5173")]
    [InlineData("http://127.0.0.1:5173")]
    [InlineData("http://[::1]:5173")]
    public void An_address_the_web_app_can_be_reached_at_is_accepted(string url)
    {
        Assert.True(Check(new PasswordResetOptions(), frontendUrl: url).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("chat.example.test")]
    [InlineData("/relative")]
    [InlineData("http://chat.example.test")]
    [InlineData("ftp://chat.example.test")]
    [InlineData("https://chat.example.test/app")]
    [InlineData("https://chat.example.test/?next=1")]
    [InlineData("https://chat.example.test/#section")]
    [InlineData("https://user:secret@chat.example.test")]
    public void An_address_that_would_make_a_wrong_or_unsafe_link_is_refused_when_email_is_on(string url)
    {
        var result = Check(new PasswordResetOptions(), frontendUrl: url);

        Assert.True(result.Failed);
        Assert.Contains("Frontend:Url", result.FailureMessage);
        Assert.DoesNotContain("secret", result.FailureMessage);
    }

    // With email off no link is ever made, so the address is not looked at.
    [Fact]
    public void With_email_off_the_frontend_address_is_not_checked()
    {
        Assert.True(Check(new PasswordResetOptions(), emailOn: false, frontendUrl: "not an address").Succeeded);
    }

    [Fact]
    public void The_settings_file_has_the_defaults_and_a_local_frontend_address()
    {
        var settings = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();

        Assert.Equal("http://localhost:5173", settings.GetSection(FrontendOptions.SectionName).Get<FrontendOptions>()!.Url);
        Assert.False(settings.GetSection(PasswordResetOptions.SectionName).Exists());
    }
}

[Collection(IntegrationTestCollection.Name)]
public class PasswordResetStartupTests
{
    private readonly ChatApiFactory _factory;

    public PasswordResetStartupTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("PasswordReset:TokenLifetime", "00:00:10")]
    [InlineData("PasswordReset:SendWithin", "00:00:00")]
    [InlineData("Frontend:Url", "http://chat.example.test")]
    public void The_host_does_not_start_with_a_setting_that_is_wrong(string key, string value)
    {
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(
            (_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));

        var failure = Record.Exception(() => host.CreateClient());

        Assert.NotNull(failure);
        var messages = new List<string>();
        for (var current = failure; current is not null; current = current.InnerException)
            messages.Add(current.Message);
        Assert.Contains(key, string.Join(" | ", messages));
    }
}
