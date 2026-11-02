using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.RateLimiting;

// Sending, upload tickets, edits and search are limited per signed-in person (D-279). The shared test host has limits that no
// test reaches; each test here builds a host with the limit it needs, and every host counts under keys of its own.
[Collection(IntegrationTestCollection.Name)]
public class PersonRateLimitTests
{
    private readonly ChatApiFactory _factory;

    public PersonRateLimitTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private WebApplicationFactory<Program> HostWith(Dictionary<string, string?> settings) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, RemoteIpStartupFilter>());
        });

    private static HttpClient ClientFor(WebApplicationFactory<Program> host, TestUser user, string address = "10.1.1.1")
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, address);
        return client;
    }

    private static Dictionary<string, string?> Limit(string policy, int permits, int windowSeconds = 60) => new()
    {
        [$"RateLimiting:{policy}:PermitLimit"] = permits.ToString(),
        [$"RateLimiting:{policy}:WindowSeconds"] = windowSeconds.ToString(),
    };

    private static async Task AssertRefusedWithTheWaitAsync(HttpResponseMessage response, int atMostSeconds = 60)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.ReadProblemAsync();
        Assert.Equal(429, problem.Status);
        Assert.Equal("Too Many Requests", problem.Title);
        Assert.Contains("Try again in", problem.Detail);

        var retryAfter = Assert.Single(response.Headers.GetValues("Retry-After"));
        Assert.InRange(int.Parse(retryAfter), 1, atMostSeconds);
    }

    // ##### Sending #####

    [Fact]
    public async Task Sending_beyond_the_limit_is_refused_with_the_wait_and_nothing_is_stored()
    {
        using var host = HostWith(Limit("Send", 3));
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = ClientFor(host, alice);

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Created, (await client.PostMessageAsync(conversation, $"message {i}")).StatusCode);

        await AssertRefusedWithTheWaitAsync(await client.PostMessageAsync(conversation, "one too many"));

        var history = await client.GetHistoryAsync(conversation);
        Assert.Equal(["message 0", "message 1", "message 2"], history.Select(m => m.ContentOf()));
    }

    [Fact]
    public async Task The_limit_counts_the_person_and_not_the_conversation_or_the_address()
    {
        using var host = HostWith(Limit("Send", 2));
        var (alice, bob, first) = await _factory.DirectChatAsync();
        var (carol, _, third) = await _factory.DirectChatAsync();
        using var aliceClient = ClientFor(host, alice);
        using var bobClient = ClientFor(host, bob);
        using var carolFromAlicesAddress = ClientFor(host, carol);

        // Alice's budget is spent in the first conversation, so it is spent in all of them.
        await aliceClient.SendMessageAsync(first, "one");
        await aliceClient.SendMessageAsync(first, "two");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await aliceClient.PostMessageAsync(first, "three")).StatusCode);
        var second = (await aliceClient.CreateGroupAsync("Team", bob.Id)).IdOf();
        Assert.Equal(HttpStatusCode.TooManyRequests, (await aliceClient.PostMessageAsync(second, "four")).StatusCode);

        // Bob, in the same conversation and from the same address, has a budget of his own, and so has Carol.
        Assert.Equal(HttpStatusCode.Created, (await bobClient.PostMessageAsync(first, "bob one")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await bobClient.PostMessageAsync(first, "bob two")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await carolFromAlicesAddress.PostMessageAsync(third, "carol one")).StatusCode);
    }

    [Fact]
    public async Task A_person_gets_their_allowance_back_as_the_window_passes()
    {
        using var host = HostWith(Limit("Send", 1, windowSeconds: 1));
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = ClientFor(host, alice);

        await client.SendMessageAsync(conversation, "one");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostMessageAsync(conversation, "two")).StatusCode);

        await Task.Delay(TimeSpan.FromSeconds(1.3));

        Assert.Equal(HttpStatusCode.Created, (await client.PostMessageAsync(conversation, "two")).StatusCode);
    }

    // Two servers of one deployment count under one prefix, so a person who sends to both has one budget (D-279).
    [Fact]
    public async Task A_persons_sends_are_counted_across_servers()
    {
        var shared = new Dictionary<string, string?>(Limit("Send", 4)) { ["RateLimiting:Store:KeyPrefix"] = $"shared{Guid.NewGuid():N}" };
        using var first = HostWith(shared);
        using var second = HostWith(shared);
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var onFirst = ClientFor(first, alice);
        using var onSecond = ClientFor(second, alice);

        // Two on each server is four in all: a count per server would have allowed four on each.
        Assert.Equal(HttpStatusCode.Created, (await onFirst.PostMessageAsync(conversation, "1")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await onSecond.PostMessageAsync(conversation, "2")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await onFirst.PostMessageAsync(conversation, "3")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await onSecond.PostMessageAsync(conversation, "4")).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await onFirst.PostMessageAsync(conversation, "5")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await onSecond.PostMessageAsync(conversation, "6")).StatusCode);
    }

    // A wait of less than a second is still a second to wait: a Retry-After of 0 would send a client straight back.
    [Fact]
    public async Task The_wait_is_never_told_as_less_than_a_second()
    {
        using var host = HostWith(Limit("Send", 1, windowSeconds: 1));
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = ClientFor(host, alice);

        await client.SendMessageAsync(conversation, "one");
        var refused = await client.PostMessageAsync(conversation, "two");

        await AssertRefusedWithTheWaitAsync(refused, atMostSeconds: 1);
    }

    // ##### Editing #####

    [Fact]
    public async Task Editing_is_limited()
    {
        using var host = HostWith(Limit("Edit", 2));
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = ClientFor(host, alice);
        var message = (await client.SendMessageAsync(conversation, "first")).MessageIdOf();

        Assert.Equal(HttpStatusCode.OK, (await client.PatchMessageAsync(conversation, message, "second")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchMessageAsync(conversation, message, "third")).StatusCode);

        await AssertRefusedWithTheWaitAsync(await client.PatchMessageAsync(conversation, message, "fourth"));

        // Sending has its own budget, and so does deleting, which is not limited.
        Assert.Equal(HttpStatusCode.Created, (await client.PostMessageAsync(conversation, "still here")).StatusCode);
        Assert.Equal("third", (await client.GetHistoryAsync(conversation))[0].ContentOf());
    }

    // ##### Upload tickets #####

    [Fact]
    public async Task Tickets_for_files_and_for_pictures_share_one_limit()
    {
        using var host = HostWith(Limit("Uploads", 3, windowSeconds: 3600));
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var client = ClientFor(host, alice);
        var group = (await client.CreateGroupAsync("Team", bob.Id)).IdOf();

        Assert.Equal(HttpStatusCode.OK, (await client.RequestUploadAsync(conversation, "a.txt", "text/plain", 10)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.AvatarUploadResponseAsync(null, "image/png", 100)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.AvatarUploadResponseAsync(group, "image/png", 100)).StatusCode);

        await AssertRefusedWithTheWaitAsync(await client.RequestUploadAsync(conversation, "b.txt", "text/plain", 10), atMostSeconds: 3600);
        await AssertRefusedWithTheWaitAsync(await client.AvatarUploadResponseAsync(null, "image/png", 100), atMostSeconds: 3600);
        await AssertRefusedWithTheWaitAsync(await client.AvatarUploadResponseAsync(group, "image/png", 100), atMostSeconds: 3600);
    }

    // ##### Search #####

    [Fact]
    public async Task Search_is_limited()
    {
        using var host = HostWith(Limit("Search", 3));
        var (alice, _, _) = await _factory.DirectChatAsync();
        using var client = ClientFor(host, alice);

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.SearchResponseAsync("anything")).StatusCode);

        await AssertRefusedWithTheWaitAsync(await client.SearchResponseAsync("anything"));
    }

    // ##### What is not limited #####

    [Fact]
    public async Task Reading_and_deleting_are_not_limited()
    {
        using var host = HostWith(new[] { "Send", "Edit", "Uploads", "Search" }
            .SelectMany(policy => Limit(policy, 1)).ToDictionary(p => p.Key, p => p.Value));
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = ClientFor(host, alice);
        var message = (await client.SendMessageAsync(conversation, "one")).MessageIdOf();

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/conversations")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetHistoryResponseAsync(conversation)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteMessageResponseAsync(conversation, message)).StatusCode);
    }

    // ##### Who is counted #####

    // A request that carries no person is counted by its address, in a count of its own: it is answered with 401 after the
    // limiter, and it must not use up what a signed-in person has.
    [Fact]
    public async Task A_request_without_a_token_is_counted_by_address_and_leaves_a_persons_allowance_alone()
    {
        using var host = HostWith(Limit("Search", 2));
        var (alice, _, _) = await _factory.DirectChatAsync();
        using var signedIn = ClientFor(host, alice, address: "10.2.2.2");
        using var anonymous = host.CreateClient();
        anonymous.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, "10.2.2.2");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SearchResponseAsync("anything")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SearchResponseAsync("anything")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await anonymous.SearchResponseAsync("anything")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await signedIn.SearchResponseAsync("anything")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await signedIn.SearchResponseAsync("anything")).StatusCode);
    }

    // ##### Every limited endpoint says so #####

    // An endpoint that loses its policy keeps working, so nothing else would notice.
    [Theory]
    [InlineData("POST", "/api/conversations/{conversationId:guid}/messages", "send")]
    [InlineData("PATCH", "/api/conversations/{conversationId:guid}/messages/{messageId:guid}", "edit")]
    [InlineData("POST", "/api/conversations/{conversationId:guid}/uploads", "uploads")]
    [InlineData("POST", "/api/users/me/avatar/uploads", "uploads")]
    [InlineData("POST", "/api/conversations/{conversationId:guid}/avatar/uploads", "uploads")]
    [InlineData("GET", "/api/search/messages", "search")]
    public void Each_of_these_endpoints_names_its_rate_limit_policy(string method, string pattern, string policy)
    {
        var endpoint = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText?.TrimEnd('/') == pattern
                         && e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method) == true);

        Assert.Equal(policy, endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);
    }

    // ##### Settings #####

    [Theory]
    [InlineData("RateLimiting:Send:PermitLimit", "0")]
    [InlineData("RateLimiting:Send:WindowSeconds", "0")]
    [InlineData("RateLimiting:Uploads:PermitLimit", "0")]
    [InlineData("RateLimiting:Uploads:WindowSeconds", "86401")]
    [InlineData("RateLimiting:Edit:PermitLimit", "-1")]
    [InlineData("RateLimiting:Edit:WindowSeconds", "0")]
    [InlineData("RateLimiting:Search:PermitLimit", "0")]
    [InlineData("RateLimiting:Search:WindowSeconds", "0")]
    [InlineData("RateLimiting:Store:KeyPrefix", "")]
    [InlineData("RateLimiting:Store:KeyPrefix", "has a space")]
    [InlineData("RateLimiting:Store:TimeoutMilliseconds", "0")]
    [InlineData("RateLimiting:Store:PauseSeconds", "0")]
    public void Startup_fails_when_a_setting_is_not_valid(string key, string value)
    {
        using var host = HostWith(new Dictionary<string, string?> { [key] = value });

        var failure = Record.Exception(() => host.CreateClient());

        Assert.NotNull(failure);
        var messages = new List<string>();
        for (var current = failure; current is not null; current = current.InnerException)
            messages.Add(current.Message);
        Assert.Contains(key.Split(':').Last(), string.Join(" | ", messages));
    }
}
