using ChatNET.API.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Tests.Email;

// The settings of email are validated when the host starts: a half-set login or a plain-text login would otherwise show up as
// the first message failing, long after the deployment. With no host set the feature is off, which is a valid state.
public class EmailOptionsTests
{
    private static readonly EmailOptionsValidator Validator = new();

    private static EmailOptions Valid() => new()
    {
        Smtp = new SmtpOptions { Host = "smtp.example.test", Port = 587, UserName = "resend", Password = "a-secret-value", Security = SmtpSecurity.StartTls },
        From = new SenderOptions { Address = "noreply@example.test", Name = "ChatNET" },
    };

    private static ValidateOptionsResult Check(EmailOptions options) => Validator.Validate(null, options);

    [Fact]
    public void Settings_that_are_complete_are_valid_and_turn_email_on()
    {
        var options = Valid();

        Assert.True(Check(options).Succeeded);
        Assert.True(options.Enabled);
    }

    [Fact]
    public void With_no_host_email_is_off_and_that_is_valid_whatever_else_is_set()
    {
        var options = Valid();
        options.Smtp.Host = string.Empty;
        options.From.Address = "not an address";

        Assert.True(Check(options).Succeeded);
        Assert.False(options.Enabled);
        Assert.False(new EmailOptions().Enabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("a@")]
    public void A_host_without_a_valid_sender_address_is_refused(string address)
    {
        var options = Valid();
        options.From.Address = address;

        var result = Check(options);

        Assert.True(result.Failed);
        Assert.Contains("Email:From:Address", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void A_port_outside_the_range_is_refused(int port)
    {
        var options = Valid();
        options.Smtp.Port = port;

        Assert.Contains("Email:Smtp:Port", Check(options).FailureMessage);
    }

    [Fact]
    public void A_security_mode_that_does_not_exist_is_refused()
    {
        var options = Valid();
        options.Smtp.Security = (SmtpSecurity)99;

        Assert.Contains("Email:Smtp:Security", Check(options).FailureMessage);
    }

    [Fact]
    public void A_timeout_of_nothing_is_refused()
    {
        var options = Valid();
        options.Smtp.Timeout = TimeSpan.Zero;

        Assert.Contains("Email:Smtp:Timeout", Check(options).FailureMessage);
    }

    [Theory]
    [InlineData("UserName")]
    [InlineData("Password")]
    public void A_login_with_only_a_user_name_or_only_a_password_is_refused(string kept)
    {
        var options = Valid();
        if (kept == "UserName")
            options.Smtp.Password = string.Empty;
        else
            options.Smtp.UserName = string.Empty;

        Assert.Contains("must both be set, or neither", Check(options).FailureMessage);
    }

    [Fact]
    public void A_server_that_takes_no_login_is_valid_without_encryption()
    {
        var options = Valid();
        options.Smtp.UserName = string.Empty;
        options.Smtp.Password = string.Empty;
        options.Smtp.Security = SmtpSecurity.None;

        Assert.True(Check(options).Succeeded);
    }

    [Fact]
    public void A_login_is_never_allowed_without_encryption()
    {
        var options = Valid();
        options.Smtp.Security = SmtpSecurity.None;

        var result = Check(options);

        Assert.True(result.Failed);
        Assert.Contains("cannot be used with a login", result.FailureMessage);
    }

    // The password is a secret, and a failure is logged when the host does not start.
    [Fact]
    public void A_failure_never_repeats_the_password_or_the_user_name()
    {
        var options = Valid();
        options.Smtp.Security = SmtpSecurity.None;
        options.From.Address = "not an address";

        var message = Check(options).FailureMessage;

        Assert.DoesNotContain("a-secret-value", message);
        Assert.DoesNotContain("resend", message);
    }

    [Fact]
    public void The_defaults_are_a_relay_on_the_submission_port_with_starttls_and_a_ten_second_timeout()
    {
        var options = new EmailOptions();

        Assert.Equal(587, options.Smtp.Port);
        Assert.Equal(SmtpSecurity.StartTls, options.Smtp.Security);
        Assert.Equal(TimeSpan.FromSeconds(10), options.Smtp.Timeout);
        Assert.Equal("ChatNET", options.From.Name);
    }

    // Read from the file itself, because the test host overrides the host and port: what a deployment gets when it sets nothing.
    [Fact]
    public void The_settings_file_leaves_email_off()
    {
        var settings = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();

        var options = settings.GetSection(EmailOptions.SectionName).Get<EmailOptions>() ?? new EmailOptions();

        Assert.False(options.Enabled);
    }
}
