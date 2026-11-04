using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ChatNET.API.Push;
using ChatNET.API.Push.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Tests.Push;

// The sender is the one place that talks to a push service, so these tests play the push service (FakePushService) and the browser
// (the keys it holds, and a decryption that follows the RFC) and look at what is sent, and at what each answer is taken to mean.
public class WebPushSenderTests
{
    private static readonly Guid ConversationId = Guid.NewGuid();
    private static readonly Guid MessageId = Guid.NewGuid();

    private static PushAlert Alert() => new(
        "Alice in Weekend plans", "see you at 5", $"conversation-{ConversationId}", PushAlertTypes.Message, ConversationId, MessageId);

    private static PushTarget TargetOf(Browser browser) =>
        new(Guid.NewGuid(), Guid.NewGuid(), browser.Endpoint, browser.P256dh, browser.Auth);

    private sealed record Rig(WebPushSender Sender, FakePushService Service, CapturingLoggerProvider Log);

    private static Rig Build(Action<PushOptions>? change = null, bool withKeys = true, TimeProvider? time = null)
    {
        var options = withKeys
            ? new PushOptions
            {
                PublicKey = PushApi.Vapid.PublicKey,
                PrivateKey = PushApi.Vapid.PrivateKey,
                Subject = PushApi.Vapid.Subject,
                AllowedHosts = ["fcm.googleapis.com", "updates.push.services.mozilla.com"],
            }
            : new PushOptions();
        change?.Invoke(options);

        var service = new FakePushService();
        var log = new CapturingLoggerProvider();
        var sender = new WebPushSender(
            new HttpClient(service),
            Options.Create(options),
            new PushEndpointPolicy(Options.Create(options)),
            time ?? TimeProvider.System,
            new LoggerFactory([log]).CreateLogger<WebPushSender>());

        return new Rig(sender, service, log);
    }

    // ##### What is sent #####

    [Fact]
    public async Task A_push_is_posted_once_to_the_address_of_the_browser()
    {
        var rig = Build();
        var browser = Browser.Subscribe();

        var outcome = await rig.Sender.SendAsync(TargetOf(browser), Alert(), default);

        Assert.Equal(PushOutcome.Sent, outcome);
        var push = Assert.Single(rig.Service.Received);
        Assert.Equal(browser.Endpoint, push.Uri.ToString());
        Assert.Equal(HttpMethod.Post, push.Method);
    }

    [Fact]
    public async Task The_browser_can_read_the_alert_with_its_own_keys()
    {
        var rig = Build();
        var browser = Browser.Subscribe();

        await rig.Sender.SendAsync(TargetOf(browser), Alert(), default);

        var alert = Assert.Single(rig.Service.Received).AlertFor(browser);
        Assert.Equal("Alice in Weekend plans", alert.GetProperty("title").GetString());
        Assert.Equal("see you at 5", alert.GetProperty("body").GetString());
        Assert.Equal($"conversation-{ConversationId}", alert.GetProperty("tag").GetString());
        Assert.Equal("message", alert.GetProperty("type").GetString());
        Assert.Equal(ConversationId, alert.GetProperty("conversationId").GetGuid());
        Assert.Equal(MessageId, alert.GetProperty("messageId").GetGuid());
    }

    [Fact]
    public async Task Another_browser_cannot_read_it()
    {
        var rig = Build();
        var browser = Browser.Subscribe();
        var other = Browser.Subscribe();

        await rig.Sender.SendAsync(TargetOf(browser), Alert(), default);

        var push = Assert.Single(rig.Service.Received);
        Assert.ThrowsAny<CryptographicException>(() => push.AlertFor(other));
    }

    // The push service holds what it relays but does not read it: neither the words of the alert nor the tag are in the body it
    // is given.
    [Fact]
    public async Task The_text_is_not_in_what_the_push_service_receives()
    {
        var rig = Build();
        var browser = Browser.Subscribe();

        await rig.Sender.SendAsync(TargetOf(browser), Alert(), default);

        var push = Assert.Single(rig.Service.Received);
        var visible = System.Text.Encoding.UTF8.GetString(push.Body) + string.Join(" ", push.Headers.Values) + push.Uri;
        Assert.DoesNotContain("see you at 5", visible);
        Assert.DoesNotContain("Alice", visible);
        Assert.DoesNotContain(ConversationId.ToString(), visible.Replace(push.Uri.ToString(), string.Empty));
        Assert.Equal("aes128gcm", push.Header("Content-Encoding"));
    }

    [Fact]
    public async Task The_request_is_signed_by_this_server_for_the_push_service_it_goes_to()
    {
        var rig = Build();
        var browser = Browser.Subscribe();

        await rig.Sender.SendAsync(TargetOf(browser), Alert(), default);

        var token = VapidToken.Read(Assert.Single(rig.Service.Received).Header("Authorization"));
        Assert.True(token.SignatureIsValid, "The token is not signed by the private key of the public key it carries.");
        Assert.Equal(PushApi.Vapid.PublicKey, token.PublicKey);
        Assert.Equal("https://fcm.googleapis.com", token.Claims.GetProperty("aud").GetString());
        Assert.Equal(PushApi.Vapid.Subject, token.Claims.GetProperty("sub").GetString());

    }

    // The RFC allows a token to last a day; twelve hours is what is asked for, counted from the clock the application reads.
    [Fact]
    public async Task The_token_lasts_twelve_hours_from_now()
    {
        var clock = new ManualTimeProvider();
        var rig = Build(time: clock);

        await rig.Sender.SendAsync(TargetOf(Browser.Subscribe()), Alert(), default);

        var token = VapidToken.Read(Assert.Single(rig.Service.Received).Header("Authorization"));
        Assert.Equal(clock.GetUtcNow().AddHours(12).ToUnixTimeSeconds(), token.Claims.GetProperty("exp").GetInt64());
    }

    [Fact]
    public async Task The_request_is_an_octet_stream_in_the_content_coding_of_the_standard()
    {
        var rig = Build();

        await rig.Sender.SendAsync(TargetOf(Browser.Subscribe()), Alert(), default);

        var push = Assert.Single(rig.Service.Received);
        Assert.Equal("aes128gcm", push.Header("Content-Encoding"));
        Assert.Equal("application/octet-stream", push.Header("Content-Type"));
        Assert.StartsWith("vapid t=", push.Header("Authorization"));
    }

    [Fact]
    public async Task The_audience_of_the_signature_follows_the_push_service_of_the_browser()
    {
        var rig = Build();
        var firefox = Browser.Subscribe(host: "updates.push.services.mozilla.com");

        await rig.Sender.SendAsync(TargetOf(firefox), Alert(), default);

        var token = VapidToken.Read(Assert.Single(rig.Service.Received).Header("Authorization"));
        Assert.Equal("https://updates.push.services.mozilla.com", token.Claims.GetProperty("aud").GetString());
    }

    [Fact]
    public async Task The_push_service_is_asked_to_hold_the_push_for_the_configured_time_and_to_deliver_it_at_once()
    {
        var rig = Build(options => options.TimeToLive = TimeSpan.FromMinutes(2));

        await rig.Sender.SendAsync(TargetOf(Browser.Subscribe()), Alert(), default);

        var push = Assert.Single(rig.Service.Received);
        Assert.Equal("120", push.Header("TTL"));
        Assert.Equal("high", push.Header("Urgency"));
    }

    [Fact]
    public async Task The_default_is_a_day()
    {
        var rig = Build();

        await rig.Sender.SendAsync(TargetOf(Browser.Subscribe()), Alert(), default);

        Assert.Equal("86400", Assert.Single(rig.Service.Received).Header("TTL"));
    }

    // ##### What each answer means #####

    [Theory]
    [InlineData(HttpStatusCode.Created, PushOutcome.Sent)]
    [InlineData(HttpStatusCode.OK, PushOutcome.Sent)]
    [InlineData(HttpStatusCode.Accepted, PushOutcome.Sent)]
    [InlineData(HttpStatusCode.NotFound, PushOutcome.Gone)]
    [InlineData(HttpStatusCode.Gone, PushOutcome.Gone)]
    [InlineData(HttpStatusCode.TooManyRequests, PushOutcome.Unavailable)]
    [InlineData(HttpStatusCode.RequestTimeout, PushOutcome.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, PushOutcome.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, PushOutcome.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, PushOutcome.Unavailable)]
    [InlineData(HttpStatusCode.BadRequest, PushOutcome.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, PushOutcome.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, PushOutcome.Rejected)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, PushOutcome.Rejected)]
    public async Task The_answer_of_the_push_service_decides_what_becomes_of_the_registration(HttpStatusCode status, PushOutcome expected)
    {
        var rig = Build();
        rig.Service.Respond = FakePushService.Answer(status);

        var outcome = await rig.Sender.SendAsync(TargetOf(Browser.Subscribe()), Alert(), default);

        Assert.Equal(expected, outcome);
    }

    [Fact]
    public async Task A_service_that_cannot_be_reached_is_unavailable_and_not_a_reason_to_remove_anything()
    {
        var rig = Build();
        rig.Service.Respond = _ => throw new HttpRequestException("No such host is known.");

        var outcome = await rig.Sender.SendAsync(TargetOf(Browser.Subscribe()), Alert(), default);

        Assert.Equal(PushOutcome.Unavailable, outcome);
    }

    // The client's own timeout surfaces as a cancellation that nobody asked for.
    [Fact]
    public async Task A_service_that_does_not_answer_in_time_is_unavailable()
    {
        var rig = Build();
        rig.Service.Respond = _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");

        var outcome = await rig.Sender.SendAsync(TargetOf(Browser.Subscribe()), Alert(), default);

        Assert.Equal(PushOutcome.Unavailable, outcome);
    }

    [Fact]
    public async Task A_cancellation_by_the_caller_is_not_swallowed()
    {
        var rig = Build();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.Sender.SendAsync(TargetOf(Browser.Subscribe()), Alert(), cancelled.Token));
    }

    // ##### What is never posted #####

    [Fact]
    public async Task An_address_that_is_not_allowed_is_never_posted_to_and_its_registration_is_gone()
    {
        var rig = Build();
        var browser = Browser.Subscribe(host: "internal.example");

        var outcome = await rig.Sender.SendAsync(TargetOf(browser), Alert(), default);

        Assert.Equal(PushOutcome.Gone, outcome);
        Assert.Empty(rig.Service.Received);
    }

    [Fact]
    public async Task Keys_that_cannot_be_used_make_the_registration_gone_and_nothing_is_posted()
    {
        var rig = Build();
        var browser = Browser.Subscribe();

        // Well formed, 65 bytes starting with 0x04, but not a point on the curve.
        var notAPoint = System.Buffers.Text.Base64Url.EncodeToString([0x04, .. new byte[64]]);
        var target = TargetOf(browser) with { P256dh = notAPoint };

        var outcome = await rig.Sender.SendAsync(target, Alert(), default);

        Assert.Equal(PushOutcome.Gone, outcome);
        Assert.Empty(rig.Service.Received);
    }

    // Nothing the person typed can make this happen (names and previews are cut), so it is a fault in the server; it must not
    // look like a dead browser and take the registration with it.
    [Fact]
    public async Task An_alert_too_long_for_a_push_is_a_fault_and_not_a_reason_to_remove_the_registration()
    {
        var rig = Build();
        var alert = Alert() with { Body = new string('x', WebPushEncryption.MaxPlaintextLength) };

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Sender.SendAsync(TargetOf(Browser.Subscribe()), alert, default));
        Assert.Empty(rig.Service.Received);
    }

    [Fact]
    public async Task A_server_without_keys_never_sends_but_can_still_be_built()
    {
        var rig = Build(withKeys: false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Sender.SendAsync(TargetOf(Browser.Subscribe()), Alert(), default));
        Assert.Empty(rig.Service.Received);
    }

    // ##### What is logged #####

    // The address is a capability and the text is the content of a message: neither may reach a log, whatever happens to the push.
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.Created)]
    public async Task Nothing_of_the_address_the_text_or_the_keys_is_logged(HttpStatusCode status)
    {
        var rig = Build();
        var browser = Browser.Subscribe();
        rig.Service.Respond = FakePushService.Answer(status);
        var target = TargetOf(browser);

        await rig.Sender.SendAsync(target, Alert(), default);

        var log = string.Join("\n", rig.Log.Entries);
        Assert.DoesNotContain(browser.Endpoint, log);
        Assert.DoesNotContain(new Uri(browser.Endpoint).AbsolutePath, log);
        Assert.DoesNotContain("see you at 5", log);
        Assert.DoesNotContain(browser.P256dh, log);
        Assert.DoesNotContain(browser.Auth, log);
        Assert.DoesNotContain(PushApi.Vapid.PrivateKey, log);
    }

    [Fact]
    public async Task A_failure_is_logged_with_the_id_of_the_registration_and_the_status()
    {
        var rig = Build();
        rig.Service.Respond = FakePushService.Answer(HttpStatusCode.ServiceUnavailable);
        var target = TargetOf(Browser.Subscribe());

        await rig.Sender.SendAsync(target, Alert(), default);

        var entry = Assert.Single(rig.Log.Entries);
        Assert.StartsWith("[Warning]", entry);
        Assert.Contains(target.Id.ToString(), entry);
        Assert.Contains("503", entry);
    }

    [Fact]
    public async Task A_removal_because_of_the_address_is_logged_with_the_id_only()
    {
        var rig = Build();
        var target = TargetOf(Browser.Subscribe(host: "internal.example"));

        await rig.Sender.SendAsync(target, Alert(), default);

        var entry = Assert.Single(rig.Log.Entries);
        Assert.Contains(target.Id.ToString(), entry);
        Assert.DoesNotContain("internal.example", entry);
    }

    // The alert is plain JSON in the shape the service worker reads.
    [Fact]
    public void The_alert_is_serialised_with_the_names_the_service_worker_reads()
    {
        var json = JsonSerializer.Serialize(Alert(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var document = JsonDocument.Parse(json);
        var names = document.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(["body", "conversationId", "messageId", "tag", "title", "type"], names);
    }
}
