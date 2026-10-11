using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Tests.Common;

// Enums are part of the public contract. Sent as numbers, 0 and 1 mean nothing to a client and
// silently change meaning if the enum is ever reordered; sent as names they are self-describing.
// REST responses and SignalR pushes use separate JSON settings, so both are checked.
[Collection(IntegrationTestCollection.Name)]
public class JsonContractTests
{
    private readonly ChatApiFactory _factory;

    public JsonContractTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Direct_conversation_type_is_sent_as_a_string()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);

        var created = await client.PostAsJsonAsync("/api/conversations/direct", new { otherUserId = bob.Id });
        created.EnsureSuccessStatusCode();
        var createdJson = await created.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Direct", createdJson.GetProperty("type").GetString());

        var list = await client.GetFromJsonAsync<JsonElement>("/api/conversations");
        Assert.Equal("Direct", list[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Group_conversation_type_is_sent_as_a_string()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);

        var created = await client.PostAsJsonAsync("/api/conversations/group",
            new { name = "Team", memberIds = new[] { bob.Id } });
        created.EnsureSuccessStatusCode();
        var json = await created.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Group", json.GetProperty("type").GetString());
    }

    [Fact]
    public void SignalR_payloads_send_enums_as_strings_too()
    {
        var hubOptions = _factory.Services.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value;
        var dto = new ConversationDto(
            Guid.NewGuid(), ConversationType.Group, "Team", null, DateTime.UtcNow, null);

        var json = JsonSerializer.Serialize(dto, hubOptions.PayloadSerializerOptions);

        Assert.Contains("\"type\":\"Group\"", json);
    }
}
