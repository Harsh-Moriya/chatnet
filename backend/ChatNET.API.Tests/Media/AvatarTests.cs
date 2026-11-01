using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatNET.API.Media;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChatNET.API.Tests.Media;

// A person's picture: permission to upload, the upload checked on what was really stored, the picture served to anyone
// holding its address, and the old object removed when it is replaced.
[Collection(IntegrationTestCollection.Name)]
public partial class AvatarTests
{
    private readonly ChatApiFactory _factory;

    public AvatarTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [GeneratedRegex("^/api/avatars/users/(?<owner>[0-9a-f-]{36})/(?<file>[0-9a-f-]{36})$")]
    private static partial Regex UserPictureAddress();

    private static HttpClient Authorized(WebApplicationFactory<Program> host, TestUser user)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", user.Token);
        return client;
    }

    // ##### Permission to upload #####

    [Fact]
    public async Task A_ticket_names_a_key_in_the_persons_own_folder_and_a_link_for_exactly_that_type()
    {
        var alice = await _factory.RegisterUserAsync("avatar");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.AvatarUploadResponseAsync(null, "image/png", 1234);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ticket = await response.Content.ReadFromJsonAsync<JsonElement>();
        var key = ticket.GetProperty("uploadKey").GetString()!;
        Assert.True(AvatarKeys.IsUploadKeyFor(key, AvatarOwner.User, alice.Id));
        Assert.StartsWith(
            _factory.StorageServiceUrl + "/" + ChatApiFactory.StorageBucket + "/" + key, ticket.GetProperty("url").GetString());

        var wrongType = await MediaApi.PutToTicketAsync(ticket, ProfileApi.SmallPicture, "image/jpeg");
        Assert.False(wrongType.IsSuccessStatusCode);
        Assert.False(await _factory.ObjectExistsAsync(key));

        var right = await MediaApi.PutToTicketAsync(ticket, ProfileApi.SmallPicture, "image/png");
        Assert.True(right.IsSuccessStatusCode);
        Assert.True(await _factory.ObjectExistsAsync(key));
    }

    [Theory]
    [InlineData("IMAGE/PNG")]
    [InlineData("image/jpeg")]
    [InlineData("image/webp")]
    [InlineData("image/gif; charset=binary")]
    public async Task The_four_image_types_are_accepted_whatever_their_case(string contentType)
    {
        var alice = await _factory.RegisterUserAsync("avatar");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.AvatarUploadResponseAsync(null, contentType, 100);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("image/svg+xml", 100)]
    [InlineData("application/pdf", 100)]
    [InlineData("text/html", 100)]
    [InlineData("image/", 100)]
    [InlineData("", 100)]
    [InlineData(null, 100)]
    [InlineData("image/png", 0)]
    [InlineData("image/png", -1)]
    [InlineData("image/png", 2 * 1024 * 1024 + 1)]
    public async Task A_picture_that_is_not_an_allowed_image_or_is_empty_or_too_large_gets_no_ticket(
        string? contentType, long sizeBytes)
    {
        var alice = await _factory.RegisterUserAsync("avatar");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.AvatarUploadResponseAsync(null, contentType, sizeBytes);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_picture_of_exactly_the_limit_gets_a_ticket()
    {
        var alice = await _factory.RegisterUserAsync("avatar");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.AvatarUploadResponseAsync(null, "image/png", AvatarRules.MaxBytes);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Nothing_can_be_asked_without_a_token()
    {
        using var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.AvatarUploadResponseAsync(null, "image/png", 10)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SetAvatarResponseAsync(null, "x")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.RemoveAvatarResponseAsync(null)).StatusCode);
    }

    [Fact]
    public async Task A_deactivated_account_gets_no_ticket()
    {
        var alice = await _factory.RegisterUserAsync("avatar");
        using var client = _factory.CreateClientFor(alice);
        await _factory.DeactivateAsync(alice);

        var response = await client.AvatarUploadResponseAsync(null, "image/png", 100);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ##### Setting a picture #####

    [Fact]
    public async Task A_picture_is_copied_to_a_permanent_key_and_the_upload_is_removed()
    {
        var alice = await _factory.RegisterUserAsync("avatar");
        using var client = _factory.CreateClientFor(alice);
        var uploadKey = await client.UploadAvatarAsync(null);

        var response = await client.SetAvatarResponseAsync(null, uploadKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var address = (await response.Content.ReadFromJsonAsync<JsonElement>()).AvatarUrlOf()!;
        var match = UserPictureAddress().Match(address);
        Assert.True(match.Success, address);
        Assert.Equal(alice.Id, Guid.Parse(match.Groups["owner"].Value));
        Assert.True(await _factory.ObjectExistsAsync(ProfileApi.KeyOf(address)));
        Assert.False(await _factory.ObjectExistsAsync(uploadKey));
        Assert.Equal(address, (await client.GetMeAsync()).AvatarUrlOf());
    }

    [Fact]
    public async Task Each_picture_has_an_address_of_its_own()
    {
        var alice = await _factory.RegisterUserAsync("avatar");
        using var client = _factory.CreateClientFor(alice);

        var first = await client.SetAvatarAsync();
        var second = await client.SetAvatarAsync();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Other_people_see_the_picture_in_search_in_a_direct_chat_and_in_a_group()
    {
        var (alice, bob, direct) = await _factory.DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var group = (await aliceClient.CreateGroupAsync("Team", bob.Id)).IdOf();

        var address = await aliceClient.SetAvatarAsync();

        var searched = (await bobClient.SearchUsersAsync(alice.UserName)).Single(u => u.GetProperty("id").GetGuid() == alice.Id);
        Assert.Equal(address, searched.AvatarUrlOf());

        var entry = (await bobClient.ListConversationsAsync()).Single(c => c.IdOf() == direct);
        Assert.Equal(address, entry.AvatarUrlOf());

        var people = await bobClient.GetMembersAsync(group);
        Assert.Equal(address, people.Single(m => m.GetProperty("userId").GetGuid() == alice.Id).AvatarUrlOf());
    }

    [Fact]
    public async Task A_direct_chat_shows_each_side_the_other_persons_picture()
    {
        var (alice, bob, direct) = await _factory.DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);

        var address = await aliceClient.SetAvatarAsync();

        Assert.Equal(address, (await bobClient.ListConversationsAsync()).Single(c => c.IdOf() == direct).AvatarUrlOf());
        Assert.Null((await aliceClient.ListConversationsAsync()).Single(c => c.IdOf() == direct).AvatarUrlOf());
    }

    [Fact]
    public async Task The_people_who_share_a_conversation_and_the_persons_tabs_are_sent_the_new_picture()
    {
        var (alice, bob, _) = await _factory.DirectChatAsync();
        await using var aliceTab = await HubClient.ConnectAsync(_factory, alice);
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        using var client = _factory.CreateClientFor(alice);

        var address = await client.SetAvatarAsync();

        Assert.Equal(address, (await bobTab.WaitForProfileChangedAsync(alice.Id)).AvatarUrlOf());
        Assert.Equal(address, (await aliceTab.WaitForProfileChangedAsync(alice.Id)).AvatarUrlOf());
    }

    // ##### What the upload must be #####

    [Fact]
    public async Task Someone_elses_upload_cannot_become_a_picture_and_is_left_alone()
    {
        var alice = await _factory.RegisterUserAsync("avatar");
        var bob = await _factory.RegisterUserAsync("avatar");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var bobsUpload = await bobClient.UploadAvatarAsync(null);

        var response = await aliceClient.SetAvatarResponseAsync(null, bobsUpload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await _factory.ObjectExistsAsync(bobsUpload));
        Assert.Null((await aliceClient.GetMeAsync()).AvatarUrlOf());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-key")]
    [InlineData("pending/avatars/users/not-a-guid/also-not")]
    public async Task A_missing_or_malformed_key_is_refused(string? uploadKey)
    {
        var alice = await _factory.RegisterUserAsync("avatar");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.SetAvatarResponseAsync(null, uploadKey);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_key_for_an_upload_that_never_happened_is_refused()
    {
        var alice = await _factory.RegisterUserAsync("avatar");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.SetAvatarResponseAsync(null, AvatarKeys.NewUploadKey(AvatarOwner.User, alice.Id));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not uploaded", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_key_made_for_a_group_cannot_be_used_for_a_person()
    {
        var alice = await _factory.RegisterUserAsync("avatar");
        using var client = _factory.CreateClientFor(alice);
        var groupKey = AvatarKeys.NewUploadKey(AvatarOwner.Group, alice.Id);
        await _factory.PutObjectAsync(groupKey, ProfileApi.SmallPicture);

        var response = await client.SetAvatarResponseAsync(null, groupKey);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await _factory.ObjectExistsAsync(groupKey));
    }

    [Fact]
    public async Task An_upload_larger_than_the_limit_is_refused_and_removed_even_when_the_ticket_said_it_was_small()
    {
        var alice = await _factory.RegisterUserAsync("avatar");
        using var client = _factory.CreateClientFor(alice);
        // A ticket for a small picture, and then a large one sent to it: the link cannot cap the size.
        var ticket = await (await client.AvatarUploadResponseAsync(null, "image/png", 100)).Content.ReadFromJsonAsync<JsonElement>();
        (await MediaApi.PutToTicketAsync(ticket, new byte[AvatarRules.MaxBytes + 1], "image/png")).EnsureSuccessStatusCode();
        var key = ticket.GetProperty("uploadKey").GetString()!;

        var response = await client.SetAvatarResponseAsync(null, key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("larger than the limit", await response.Content.ReadAsStringAsync());
        Assert.False(await _factory.ObjectExistsAsync(key));
    }

    [Fact]
    public async Task An_upload_that_is_not_one_of_the_image_types_is_refused_and_removed()
    {
        var alice = await _factory.RegisterUserAsync("avatar");
        using var client = _factory.CreateClientFor(alice);
        var key = AvatarKeys.NewUploadKey(AvatarOwner.User, alice.Id);
        await _factory.PutObjectAsync(key, "<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray(), "image/svg+xml");

        var response = await client.SetAvatarResponseAsync(null, key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("type that cannot be used", await response.Content.ReadAsStringAsync());
        Assert.False(await _factory.ObjectExistsAsync(key));
    }

    [Fact]
    public async Task An_empty_upload_is_refused_and_removed()
    {
        var alice = await _factory.RegisterUserAsync("avatar");
        using var client = _factory.CreateClientFor(alice);
        var key = AvatarKeys.NewUploadKey(AvatarOwner.User, alice.Id);
        await _factory.PutObjectAsync(key, [], "image/png");

        var response = await client.SetAvatarResponseAsync(null, key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await _factory.ObjectExistsAsync(key));
    }

    [Fact]
    public async Task An_upload_replaced_after_it_was_looked_at_is_not_used()
    {
        var alice = await _factory.RegisterUserAsync("avatar");
        string? swapped = null;
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IBlobStorage>();
            services.AddSingleton<IBlobStorage>(provider => new ReplacingBlobStorage(
                ActivatorUtilities.CreateInstance<S3BlobStorage>(provider),
                async key =>
                {
                    swapped = key;
                    await _factory.PutObjectAsync(key, "<svg/>"u8.ToArray(), "image/png");
                }));
        }));
        using var client = Authorized(host, alice);
        var key = await client.UploadAvatarAsync(null);

        var response = await client.SetAvatarResponseAsync(null, key);

        Assert.Equal(key, swapped);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null((await client.GetMeAsync()).AvatarUrlOf());
        Assert.Empty(await _factory.ObjectKeysAsync($"avatars/users/{alice.Id}/"));
    }

    [Fact]
    public async Task A_deactivated_account_cannot_set_a_picture_and_the_copy_is_not_kept()
    {
        var alice = await _factory.RegisterUserAsync("avatar");
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadAvatarAsync(null);
        await _factory.DeactivateAsync(alice);

        var response = await client.SetAvatarResponseAsync(null, key);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await _factory.ObjectKeysAsync($"avatars/users/{alice.Id}/"));
    }

    // ##### Serving a picture #####

    [Fact]
    public async Task Anyone_with_the_address_gets_the_picture_and_may_keep_it_for_a_year()
    {
        var alice = await _factory.RegisterUserAsync("avatar");
        using var client = _factory.CreateClientFor(alice);
        var address = await client.SetAvatarAsync();
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync(address);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(ProfileApi.SmallPicture, await response.Content.ReadAsByteArrayAsync());
        var cache = response.Headers.CacheControl!;
        Assert.True(cache.Public);
        Assert.Equal(TimeSpan.FromDays(365), cache.MaxAge);
        Assert.Contains("immutable", cache.ToString());
    }

    [Fact]
    public async Task An_address_that_is_not_a_stored_picture_is_a_plain_404()
    {
        using var anonymous = _factory.CreateClient();

        var user = await anonymous.GetAsync($"/api/avatars/users/{Guid.NewGuid()}/{Guid.NewGuid()}");
        var group = await anonymous.GetAsync($"/api/avatars/groups/{Guid.NewGuid()}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, user.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, group.StatusCode);
        Assert.Equal("application/problem+json", user.Content.Headers.ContentType!.MediaType);
        Assert.True(user.Headers.CacheControl!.NoStore);
        Assert.Null(user.Headers.CacheControl.MaxAge);
    }

    // The router answers this one before any endpoint runs, so nothing but the headers middleware can say how long it may be
    // kept: only a picture that was found may be kept.
    [Fact]
    public async Task An_address_with_something_that_is_not_an_id_is_a_404_that_may_not_be_kept()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync("/api/avatars/users/not-an-id/also-not-an-id");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task The_address_cannot_be_used_to_read_anything_else_in_the_bucket()
    {
        var alice = await _factory.RegisterUserAsync("avatar");
        using var client = _factory.CreateClientFor(alice);
        var uploadKey = await client.UploadAvatarAsync(null);
        using var anonymous = _factory.CreateClient();

        var traversal = await anonymous.GetAsync($"/api/avatars/users/{alice.Id}/..%2F..%2F{uploadKey}");
        var notAGuid = await anonymous.GetAsync($"/api/avatars/users/{alice.Id}/{uploadKey}");

        Assert.False(traversal.IsSuccessStatusCode);
        Assert.False(notAGuid.IsSuccessStatusCode);
    }

    // ##### Replacing and removing #####

    [Fact]
    public async Task A_new_picture_removes_the_old_object_and_the_old_address_stops_working()
    {
        var alice = await _factory.RegisterUserAsync("avatar");
        using var client = _factory.CreateClientFor(alice);
        using var anonymous = _factory.CreateClient();
        var first = await client.SetAvatarAsync();

        var second = await client.SetAvatarAsync();

        Assert.False(await _factory.ObjectExistsAsync(ProfileApi.KeyOf(first)));
        Assert.True(await _factory.ObjectExistsAsync(ProfileApi.KeyOf(second)));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(first)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(second)).StatusCode);
        var job = Assert.Single(await RemovalRowsAsync(ProfileApi.KeyOf(first)));
        Assert.NotNull(job.DispatchedAt);
    }

    [Fact]
    public async Task Removing_the_picture_removes_the_object_and_tells_the_contacts()
    {
        var (alice, bob, _) = await _factory.DirectChatAsync();
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        using var client = _factory.CreateClientFor(alice);
        var address = await client.SetAvatarAsync();
        await bobTab.WaitForProfileChangedAsync(alice.Id);

        var response = await client.RemoveAvatarResponseAsync(null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await response.Content.ReadFromJsonAsync<JsonElement>()).AvatarUrlOf());
        Assert.Null((await client.GetMeAsync()).AvatarUrlOf());
        Assert.False(await _factory.ObjectExistsAsync(ProfileApi.KeyOf(address)));
        await WaitUntilAsync(() => bobTab.ProfileChanges.Count(p => p.GetProperty("id").GetGuid() == alice.Id) == 2);
        Assert.Null(bobTab.ProfileChanges.Last(p => p.GetProperty("id").GetGuid() == alice.Id).AvatarUrlOf());
    }

    [Fact]
    public async Task Removing_a_picture_that_is_not_there_is_a_200_that_stores_and_says_nothing()
    {
        var (alice, bob, _) = await _factory.DirectChatAsync();
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        using var client = _factory.CreateClientFor(alice);

        var response = await client.RemoveAvatarResponseAsync(null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await response.Content.ReadFromJsonAsync<JsonElement>()).AvatarUrlOf());
        Assert.False(await bobTab.ReceivedProfileChangedAsync(alice.Id));
        Assert.Empty(await _factory.OutboxRowsAboutAsync("userId", alice.Id));
    }

    // ##### Failures #####

    [Fact]
    public async Task Two_pictures_set_at_once_leave_exactly_one_object_and_it_is_the_one_in_use()
    {
        var alice = await _factory.RegisterUserAsync("avatar");
        using var client = _factory.CreateClientFor(alice);
        var one = await client.UploadAvatarAsync(null);
        var two = await client.UploadAvatarAsync(null);

        var responses = await Task.WhenAll(client.SetAvatarResponseAsync(null, one), client.SetAvatarResponseAsync(null, two));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var inUse = (await client.GetMeAsync()).AvatarUrlOf()!;
        var stored = await _factory.ObjectKeysAsync($"avatars/users/{alice.Id}/");
        Assert.Equal(ProfileApi.KeyOf(inUse), Assert.Single(stored));
    }

    [Fact]
    public async Task When_the_old_object_cannot_be_removed_the_change_stands_and_the_relay_removes_it_later()
    {
        var alice = await _factory.RegisterUserAsync("avatar");
        using var client = _factory.CreateClientFor(alice);
        var old = ProfileApi.KeyOf(await client.SetAvatarAsync());
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IBlobStorage>();
            services.AddSingleton<IBlobStorage>(provider => new UndeletableBlobStorage(
                ActivatorUtilities.CreateInstance<S3BlobStorage>(provider), key => key == old));
        }));
        using var hostClient = Authorized(host, alice);

        var address = await hostClient.SetAvatarAsync();

        Assert.Equal(address, (await client.GetMeAsync()).AvatarUrlOf());
        Assert.True(await _factory.ObjectExistsAsync(old));
        var job = Assert.Single(await RemovalRowsAsync(old));
        Assert.Null(job.DispatchedAt);

        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        clock.Advance(TimeSpan.FromMinutes(3));
        await relayHost.Services.GetRequiredService<OutboxRelayService>().RunOnceAsync(CancellationToken.None);

        Assert.False(await _factory.ObjectExistsAsync(old));
        Assert.NotNull((await _factory.OutboxRowAsync(job.Id)).DispatchedAt);
        Assert.True(await _factory.ObjectExistsAsync(ProfileApi.KeyOf(address)));
    }

    [Fact]
    public async Task A_removal_that_names_a_picture_in_use_removes_nothing()
    {
        var alice = await _factory.RegisterUserAsync("avatar");
        using var client = _factory.CreateClientFor(alice);
        var inUse = ProfileApi.KeyOf(await client.SetAvatarAsync());
        var now = DateTime.UtcNow;
        var rowId = Guid.CreateVersion7();
        await _factory.ExecuteDbAsync(async db =>
        {
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = rowId,
                Kind = AvatarFileRemoved.Name,
                Payload = OutboxEvents.Serialize(new AvatarFileRemoved(inUse)),
                CreatedAt = now.AddMinutes(-10),
                NextAttemptAt = now.AddMinutes(-10),
            });
            await db.SaveChangesAsync();
        });

        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        clock.Advance(TimeSpan.FromMinutes(3));
        await relayHost.Services.GetRequiredService<OutboxRelayService>().RunOnceAsync(CancellationToken.None);

        Assert.True(await _factory.ObjectExistsAsync(inUse));
        Assert.NotNull((await _factory.OutboxRowAsync(rowId)).DispatchedAt);
    }

    // ##### Helpers #####

    private Task<List<OutboxMessage>> RemovalRowsAsync(string key) =>
        _factory.QueryDbAsync(db => db.OutboxMessages
            .FromSqlInterpolated($"""SELECT * FROM "OutboxMessages" WHERE "Kind" = {AvatarFileRemoved.Name} AND "Payload" ->> 'key' = {key}""")
            .AsNoTracking()
            .ToListAsync());

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition did not become true.");

            await Task.Delay(50);
        }
    }
}
