using System.Net;
using ChatNET.API.Auth.PasswordReset;
using ChatNET.API.Common.Time;
using ChatNET.API.Email;
using ChatNET.API.Outbox.Models;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.Auth;

// Asking for a reset link. The answer is 202 for every address and costs the same work for every address, because a different
// answer, or a faster one, would tell a stranger which addresses have an account. The email itself is sent by the outbox relay.
[Collection(IntegrationTestCollection.Name)]
public class ForgotPasswordTests : IAsyncLifetime
{
    private readonly ChatApiFactory _factory;

    public ForgotPasswordTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // A relay pass takes every waiting row, whoever wrote it, so these tests leave none behind and start with none.
    public Task InitializeAsync() => _factory.RemoveWaitingOutboxRowsAsync();

    public Task DisposeAsync() => _factory.RemoveWaitingOutboxRowsAsync();

    private static string UnknownAddress() => $"nobody_{Guid.NewGuid():N}@example.test";

    private WebApplicationFactory<Program> HostWith(Dictionary<string, string?> settings) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings)));

    // A host for the relay: a clock the test moves and an email sender that records and fails when told. The clock is started half
    // a minute ahead of now, so a row the test writes a moment later is already due when the relay looks at it.
    private WebApplicationFactory<Program> RelayHost(RecordingEmailSender sender, ManualTimeProvider clock, Dictionary<string, string?>? settings = null)
    {
        clock.Advance(TimeSpan.FromSeconds(30));

        return _factory.WithClock(clock).WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings ?? []));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(sender);
            });
        });
    }

    private static async Task<OutboxRowView> RowOfAsync(ChatApiFactory factory, Guid userId)
    {
        var rows = await factory.OutboxRowsAboutAsync("userId", userId);
        var row = rows.Last(r => r.Kind == PasswordResetRequested.Name);
        return new OutboxRowView(row.Id, row.Payload, row.Attempts, row.DispatchedAt, row.LastError);
    }

    private sealed record OutboxRowView(Guid Id, string Payload, int Attempts, DateTime? DispatchedAt, string? LastError);

    // ##### What the person gets #####

    [Fact]
    public async Task A_known_address_gets_one_email_with_both_parts_and_a_link_that_works_for_thirty_minutes()
    {
        var user = await _factory.RegisterUserAsync("forgot");
        using var client = _factory.CreateClient();

        var response = await client.ForgotAsync(user.Email);
        await _factory.RunRelayAsync();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var mail = Assert.Single(await _factory.Mail.WaitForAsync(user.Email));
        Assert.Equal("Reset your ChatNET password", mail.Subject);
        Assert.Equal(user.Email, Assert.Single(mail.To).Address);
        Assert.Equal(ChatApiFactory.MailFromAddress, mail.From.Address);
        Assert.Contains("within 30 minutes", mail.Text);
        Assert.Contains("within 30 minutes", mail.Html);

        var token = PasswordResetApi.TokenIn(mail);
        Assert.Equal(43, token.Length);
        Assert.Contains($"http://localhost:5173/reset-password#token={token}", mail.Text);
        Assert.Contains($"href=\"http://localhost:5173/reset-password#token={token}\"", mail.Html);

        var stored = await _factory.ResetTokenAsync(token);
        Assert.Equal(user.Id, stored!.UserId);
        Assert.Null(stored.UsedAt);
        Assert.Equal(TimeSpan.FromMinutes(30), stored.ExpiresAt - stored.CreatedAt);
    }

    // The display name is typed by whoever registered, and nobody has proved the address is theirs.
    [Fact]
    public async Task The_email_never_contains_the_display_name()
    {
        var user = await _factory.RegisterUserAsync("namefree", "Click here to win a prize");
        using var client = _factory.CreateClient();

        await client.ForgotAsync(user.Email);
        await _factory.RunRelayAsync();

        var mail = Assert.Single(await _factory.Mail.WaitForAsync(user.Email));
        Assert.DoesNotContain("prize", mail.Text);
        Assert.DoesNotContain("prize", mail.Html);
        Assert.DoesNotContain("prize", mail.Subject);
    }

    [Fact]
    public async Task The_link_lifetime_follows_the_setting()
    {
        var user = await _factory.RegisterUserAsync("lifetime");
        using var host = HostWith(new() { ["PasswordReset:TokenLifetime"] = "00:45:00" });
        using var client = host.CreateClient();

        await client.ForgotAsync(user.Email);
        await host.RunRelayAsync();

        var mail = Assert.Single(await _factory.Mail.WaitForAsync(user.Email));
        Assert.Contains("within 45 minutes", mail.Text);
        var stored = await _factory.ResetTokenAsync(PasswordResetApi.TokenIn(mail));
        Assert.Equal(TimeSpan.FromMinutes(45), stored!.ExpiresAt - stored.CreatedAt);
    }

    // Identity finds an account by upper-casing the address, which can match an address that is not spelled the same. The mail
    // goes to the address that is stored.
    [Fact]
    public async Task The_email_goes_to_the_stored_address_whatever_case_was_typed()
    {
        var user = await _factory.RegisterUserAsync("casing");
        using var client = _factory.CreateClient();

        var response = await client.ForgotAsync(user.Email.ToUpperInvariant());
        await _factory.RunRelayAsync();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var mail = Assert.Single(await _factory.Mail.WaitForAsync(user.Email));
        Assert.Equal(user.Email, Assert.Single(mail.To).Address);
    }

    // ##### Nothing that tells addresses apart #####

    [Fact]
    public async Task Known_unknown_and_deactivated_addresses_get_the_same_answer_and_each_saves_one_row()
    {
        var known = await _factory.RegisterUserAsync("sameknown");
        var deactivated = await _factory.RegisterUserAsync("samedeactivated");
        await _factory.DeactivateAsync(deactivated.Id);
        using var client = _factory.CreateClient();
        var nobodyBefore = await _factory.ResetRowsForNobodyAsync();

        var forKnown = await client.ForgotAsync(known.Email);
        var forUnknown = await client.ForgotAsync(UnknownAddress());
        var forDeactivated = await client.ForgotAsync(deactivated.Email);

        Assert.All(new[] { forKnown, forUnknown, forDeactivated }, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        Assert.Equal(await forKnown.Content.ReadAsStringAsync(), await forUnknown.Content.ReadAsStringAsync());
        Assert.Equal(await forKnown.Content.ReadAsStringAsync(), await forDeactivated.Content.ReadAsStringAsync());
        Assert.Equal(ShapeOf(forKnown), ShapeOf(forUnknown));
        Assert.Equal(ShapeOf(forKnown), ShapeOf(forDeactivated));

        // The same work: one row each. The row of the real account names it, the other two name nobody.
        Assert.Single(await _factory.OutboxRowsAboutAsync("userId", known.Id));
        Assert.Equal(nobodyBefore + 2, await _factory.ResetRowsForNobodyAsync());
    }

    // The header names and values that are not the time of the request or the id of its trace.
    private static string ShapeOf(HttpResponseMessage response) => string.Join(
        ";",
        response.Headers.Concat(response.Content.Headers)
            .Where(h => h.Key is not ("Date" or "traceparent"))
            .OrderBy(h => h.Key)
            .Select(h => $"{h.Key}={string.Join(",", h.Value)}"));

    [Fact]
    public async Task A_deactivated_account_gets_no_email()
    {
        var user = await _factory.RegisterUserAsync("deact");
        await _factory.DeactivateAsync(user.Id);
        using var client = _factory.CreateClient();

        await client.ForgotAsync(user.Email);
        await _factory.RunRelayAsync();

        Assert.Empty(await _factory.Mail.SentToAfterAsync(user.Email, TimeSpan.FromMilliseconds(500)));
        Assert.Empty(await _factory.ResetTokensOfAsync(user.Id));
    }

    [Fact]
    public async Task An_unknown_address_gets_no_email_and_no_token_is_made()
    {
        var address = UnknownAddress();
        using var client = _factory.CreateClient();

        await client.ForgotAsync(address);
        await _factory.RunRelayAsync();

        Assert.Empty(await _factory.Mail.SentToAfterAsync(address, TimeSpan.FromMilliseconds(500)));
    }

    // ##### What is stored #####

    [Fact]
    public async Task The_outbox_row_names_the_person_and_holds_no_token_or_address()
    {
        var user = await _factory.RegisterUserAsync("payload");
        using var client = _factory.CreateClient();

        await client.ForgotAsync(user.Email);
        var beforeRelay = await RowOfAsync(_factory, user.Id);
        await _factory.RunRelayAsync();
        var afterRelay = await RowOfAsync(_factory, user.Id);

        // Postgres stores the payload as jsonb, which spaces it its own way, so it is compared as JSON.
        using var payload = System.Text.Json.JsonDocument.Parse(beforeRelay.Payload);
        Assert.Equal(user.Id.ToString(), payload.RootElement.GetProperty("userId").GetString());
        Assert.Single(payload.RootElement.EnumerateObject());
        Assert.NotNull(afterRelay.DispatchedAt);
        Assert.Null(afterRelay.LastError);

        var mail = Assert.Single(await _factory.Mail.WaitForAsync(user.Email));
        var token = PasswordResetApi.TokenIn(mail);
        Assert.DoesNotContain(token, afterRelay.Payload);

        // Only the hash is stored, and it is not the token.
        var stored = await _factory.ResetTokenAsync(token);
        Assert.NotEqual(token, stored!.TokenHash);
    }

    // ##### Limits #####

    [Fact]
    public async Task Each_address_is_sent_at_most_three_emails_an_hour_and_the_answer_does_not_change()
    {
        var user = await _factory.RegisterUserAsync("perthree");
        var other = await _factory.RegisterUserAsync("perthreeother");
        using var host = HostWith(new() { ["RateLimiting:PasswordResetEmail:PermitLimit"] = "3" });
        using var client = host.CreateClient();

        // Spelled in two ways: the count is of the address, not of how it was typed.
        var answers = new[]
        {
            await client.ForgotAsync(user.Email),
            await client.ForgotAsync(user.Email.ToUpperInvariant()),
            await client.ForgotAsync(user.Email),
            await client.ForgotAsync(user.Email.ToUpperInvariant()),
        };
        var anotherAnswer = await client.ForgotAsync(other.Email);
        await host.RunRelayAsync();

        Assert.All(answers.Append(anotherAnswer), answer => Assert.Equal(HttpStatusCode.Accepted, answer.StatusCode));
        Assert.Equal(3, (await _factory.Mail.WaitForAsync(user.Email, 3)).Count);
        Assert.Equal(3, (await _factory.Mail.SentToAfterAsync(user.Email, TimeSpan.FromMilliseconds(500))).Count);
        Assert.Single(await _factory.Mail.WaitForAsync(other.Email));
    }

    [Fact]
    public async Task Each_client_address_may_ask_only_so_often_and_a_refused_request_saves_nothing()
    {
        using var host = HostWith(new() { ["RateLimiting:ForgotPassword:PermitLimit"] = "2" });
        using var client = host.CreateClient();
        var nobodyBefore = await _factory.ResetRowsForNobodyAsync();

        Assert.Equal(HttpStatusCode.Accepted, (await client.ForgotAsync(UnknownAddress())).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.ForgotAsync(UnknownAddress())).StatusCode);
        var refused = await client.ForgotAsync(UnknownAddress());

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotEmpty(refused.Headers.GetValues("Retry-After"));
        Assert.Equal(nobodyBefore + 2, await _factory.ResetRowsForNobodyAsync());
    }

    // ##### Email off #####

    [Fact]
    public async Task With_email_off_asking_answers_503_and_saves_nothing()
    {
        using var host = HostWith(new() { ["Email:Smtp:Host"] = string.Empty });
        using var client = host.CreateClient();
        var nobodyBefore = await _factory.ResetRowsForNobodyAsync();

        var response = await client.ForgotAsync(UnknownAddress());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal(503, problem.Status);
        Assert.Equal("Service Unavailable", problem.Title);
        Assert.Equal("Password reset is not available.", problem.Detail);
        Assert.Equal(nobodyBefore, await _factory.ResetRowsForNobodyAsync());
    }

    // A request that cannot be served must not use up anyone's allowance of emails.
    [Fact]
    public async Task A_request_answered_503_does_not_use_up_the_allowance_of_the_address()
    {
        var address = UnknownAddress();
        using var host = HostWith(new()
        {
            ["Email:Smtp:Host"] = string.Empty,
            ["RateLimiting:PasswordResetEmail:PermitLimit"] = "1",
        });
        using var client = host.CreateClient();

        await client.ForgotAsync(address);
        await client.ForgotAsync(address);

        using var scope = host.Services.CreateScope();
        var limiter = host.Services.GetRequiredService<PasswordResetEmailLimiter>();
        Assert.True(await limiter.MaySendAsync(address.ToUpperInvariant(), CancellationToken.None));
    }

    // ##### Validation #####

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("a@")]
    public async Task A_missing_or_malformed_address_is_a_400_on_the_email_field_and_saves_nothing(string email)
    {
        using var client = _factory.CreateClient();
        var nobodyBefore = await _factory.ResetRowsForNobodyAsync();

        var response = await client.ForgotAsync(email);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.True(problem.Errors!.Value.TryGetProperty("Email", out _));
        Assert.Equal(nobodyBefore, await _factory.ResetRowsForNobodyAsync());
    }

    [Fact]
    public async Task An_address_longer_than_254_characters_is_refused()
    {
        using var client = _factory.CreateClient();

        var response = await client.ForgotAsync($"{new string('a', 250)}@example.test");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ##### The relay #####

    [Fact]
    public async Task A_mail_server_that_asks_to_try_later_leaves_the_row_for_a_retry_with_no_address_in_its_error()
    {
        var user = await _factory.RegisterUserAsync("retry");
        var sender = new RecordingEmailSender();
        var clock = new ManualTimeProvider();
        using var relay = RelayHost(sender, clock);
        using var client = _factory.CreateClient();
        sender.FailNext(new EmailDeliveryException("SMTP 451", isPermanent: false));

        await client.ForgotAsync(user.Email);
        await relay.RunRelayAsync();

        var failed = await RowOfAsync(_factory, user.Id);
        Assert.Null(failed.DispatchedAt);
        Assert.Equal(1, failed.Attempts);
        Assert.Equal("EmailDeliveryException: SMTP 451", failed.LastError);
        Assert.Empty(sender.Sent);

        clock.Advance(TimeSpan.FromSeconds(30));
        await relay.RunRelayAsync();

        var retried = await RowOfAsync(_factory, user.Id);
        Assert.NotNull(retried.DispatchedAt);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal(user.Email, sent.To);
    }

    [Fact]
    public async Task A_mail_server_that_refuses_for_good_ends_the_row_and_is_not_asked_again()
    {
        var user = await _factory.RegisterUserAsync("refused");
        var sender = new RecordingEmailSender();
        var clock = new ManualTimeProvider();
        var logs = new CapturingLoggerProvider();
        using var relay = RelayHost(sender, clock).WithWebHostBuilder(builder => builder.ConfigureLogging(l => l.AddProvider(logs)));
        using var client = _factory.CreateClient();
        sender.FailNext(new EmailDeliveryException("SMTP 550", isPermanent: true));

        await client.ForgotAsync(user.Email);
        await relay.RunRelayAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        await relay.RunRelayAsync();

        var row = await RowOfAsync(_factory, user.Id);
        Assert.NotNull(row.DispatchedAt);
        Assert.Equal(1, row.Attempts);
        Assert.Empty(sender.Sent);
        Assert.Contains(logs.Entries, entry => entry.Contains("refused the password reset email") && entry.Contains("SMTP 550"));
        Assert.DoesNotContain(logs.Entries, entry => entry.Contains(user.Email));
    }

    [Fact]
    public async Task A_request_that_waited_longer_than_the_send_window_is_dropped_without_an_email()
    {
        var user = await _factory.RegisterUserAsync("late");
        var sender = new RecordingEmailSender();
        var clock = new ManualTimeProvider();
        using var relay = RelayHost(sender, clock);
        using var client = _factory.CreateClient();

        await client.ForgotAsync(user.Email);
        clock.Advance(TimeSpan.FromMinutes(11));
        await relay.RunRelayAsync();

        Assert.NotNull((await RowOfAsync(_factory, user.Id)).DispatchedAt);
        Assert.Empty(sender.Sent);
        Assert.Empty(await _factory.ResetTokensOfAsync(user.Id));
    }

    // The window is checked at the mailer, where the age can be set exactly: at the limit it is still sent, a moment later not.
    [Fact]
    public async Task A_request_exactly_as_old_as_the_send_window_is_still_sent_and_one_tick_older_is_not()
    {
        var user = await _factory.RegisterUserAsync("window");
        var sender = new RecordingEmailSender();
        var clock = new ManualTimeProvider();
        using var host = RelayHost(sender, clock);
        // Truncated as the mailer truncates its own, so the age below is exactly the window and not a few ticks short of it.
        var now = clock.GetUtcNow().UtcDateTime.TruncateToMicroseconds();
        var window = TimeSpan.FromMinutes(10);

        using (var scope = host.Services.CreateScope())
        {
            var mailer = scope.ServiceProvider.GetRequiredService<PasswordResetMailer>();
            await mailer.SendAsync(new OutboxMessage { Id = Guid.NewGuid(), CreatedAt = now - window }, user.Id, CancellationToken.None);
            Assert.Single(sender.Sent);

            await mailer.SendAsync(new OutboxMessage { Id = Guid.NewGuid(), CreatedAt = now - window - TimeSpan.FromTicks(10) }, user.Id, CancellationToken.None);
            Assert.Single(sender.Sent);
        }
    }

    [Fact]
    public async Task Email_switched_off_after_the_request_ends_the_row_without_sending()
    {
        var user = await _factory.RegisterUserAsync("switchedoff");
        var sender = new RecordingEmailSender();
        using var relay = RelayHost(sender, new ManualTimeProvider(), new() { ["Email:Smtp:Host"] = string.Empty });
        using var client = _factory.CreateClient();

        await client.ForgotAsync(user.Email);
        await relay.RunRelayAsync();

        Assert.NotNull((await RowOfAsync(_factory, user.Id)).DispatchedAt);
        Assert.Empty(sender.Sent);
        Assert.Empty(await _factory.ResetTokensOfAsync(user.Id));
    }

    [Fact]
    public async Task An_account_deactivated_after_the_request_is_not_sent_an_email()
    {
        var user = await _factory.RegisterUserAsync("deactlater");
        var sender = new RecordingEmailSender();
        using var relay = RelayHost(sender, new ManualTimeProvider());
        using var client = _factory.CreateClient();

        await client.ForgotAsync(user.Email);
        await _factory.DeactivateAsync(user.Id);
        await relay.RunRelayAsync();

        Assert.NotNull((await RowOfAsync(_factory, user.Id)).DispatchedAt);
        Assert.Empty(sender.Sent);
        Assert.Empty(await _factory.ResetTokensOfAsync(user.Id));
    }

    [Fact]
    public async Task A_person_whose_account_was_deleted_after_the_request_is_not_sent_an_email()
    {
        var user = await _factory.RegisterUserAsync("deleted");
        var sender = new RecordingEmailSender();
        using var relay = RelayHost(sender, new ManualTimeProvider());
        using var client = _factory.CreateClient();

        await client.ForgotAsync(user.Email);
        await _factory.ExecuteDbAsync(db => db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync());
        await relay.RunRelayAsync();

        Assert.Empty(sender.Sent);
    }

    // ##### Logs #####

    [Fact]
    public async Task Nothing_in_the_logs_holds_the_address_the_token_or_the_link()
    {
        var user = await _factory.RegisterUserAsync("logfree");
        var logs = new CapturingLoggerProvider();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureLogging(l => l.AddProvider(logs)));
        using var client = host.CreateClient();

        await client.ForgotAsync(user.Email);
        await client.ForgotAsync(UnknownAddress());
        await host.RunRelayAsync();
        var mail = Assert.Single(await _factory.Mail.WaitForAsync(user.Email));
        var token = PasswordResetApi.TokenIn(mail);

        Assert.NotEmpty(logs.Entries);
        Assert.DoesNotContain(logs.Entries, entry => entry.Contains(user.Email, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logs.Entries, entry => entry.Contains(token));
        Assert.DoesNotContain(logs.Entries, entry => entry.Contains("reset-password"));
        Assert.Contains(logs.Entries, entry => entry.Contains(user.Id.ToString()));
    }
}
