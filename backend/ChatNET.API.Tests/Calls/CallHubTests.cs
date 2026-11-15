using System.Text.Json;
using ChatNET.API.Calls;
using ChatNET.API.Calls.Models;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Calls;

// Calls through the hub, with real SignalR clients, Redis and Postgres: a person starts a call in a direct conversation, every tab of the
// other person rings, one answers, the sides exchange signals the server relays, and either hangs up; and what the server does when a ring
// runs out or a connection drops.
[Collection(IntegrationTestCollection.Name)]
public class CallHubTests : IAsyncLifetime
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);

    private readonly ChatApiFactory _factory;

    public CallHubTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // The calls these tests write leave outbox rows (the call row, the alert of a missed one) that other tests' relay passes would take.
    public Task InitializeAsync() => _factory.RemoveWaitingOutboxRowsAsync();

    public Task DisposeAsync() => _factory.RemoveWaitingOutboxRowsAsync();

    private WebApplicationFactory<Program> Quick(Dictionary<string, string?>? more = null) => _factory.QuickCalls(more);

    private static Guid CallIdOf(JsonElement started) => started.GetProperty("callId").GetGuid();

    private Task<List<Call>> CallsOfAsync(Guid conversation) =>
        _factory.QueryDbAsync(db => db.Calls.AsNoTracking().Where(c => c.ConversationId == conversation).ToListAsync());

    private static async Task<HubException> RefusedAsync(Func<Task> invoke) => await Assert.ThrowsAsync<HubException>(invoke);

    private static void AssertCode(string code, HubException error) => Assert.Contains(code, error.Message);

    // ##### The whole call #####

    [Fact]
    public async Task A_call_is_started_answered_signalled_and_hung_up_and_ends_up_in_the_conversation()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await using var caller = await HubClient.ConnectAsync(_factory, alice);
        await using var callee = await HubClient.ConnectAsync(_factory, bob);

        var started = await caller.StartCallAsync(conversation, "Video");
        var callId = CallIdOf(started);

        Assert.False(string.IsNullOrEmpty(started.GetProperty("rejoinToken").GetString()));
        Assert.True(started.GetProperty("ringsUntil").GetDateTime() > DateTime.UtcNow);
        Assert.Equal(JsonValueKind.Array, started.GetProperty("iceServers").ValueKind);

        var ringing = await callee.WaitForIncomingCallAsync(callId);
        Assert.Equal(alice.Id, ringing.GetProperty("callerId").GetGuid());
        Assert.Equal(alice.DisplayName, ringing.GetProperty("callerDisplayName").GetString());
        Assert.Equal(conversation, ringing.GetProperty("conversationId").GetGuid());
        Assert.Equal("Video", ringing.GetProperty("media").GetString());

        var accepted = await callee.AcceptCallAsync(callId);
        Assert.Equal(alice.Id, accepted.GetProperty("callerId").GetGuid());
        Assert.Equal("Video", accepted.GetProperty("media").GetString());
        Assert.NotEqual(started.GetProperty("rejoinToken").GetString(), accepted.GetProperty("rejoinToken").GetString());
        await caller.WaitForCallAnsweredAsync(callId);

        Assert.True(await caller.SendCallSignalAsync(callId, "Offer", "v=0 offer"));
        var offer = await callee.WaitForCallSignalAsync(callId);
        Assert.Equal(("Offer", "v=0 offer"), (offer.Type, offer.Payload));

        Assert.True(await callee.SendCallSignalAsync(callId, "Answer", "v=0 answer"));
        Assert.True(await callee.SendCallSignalAsync(callId, "Candidate", "candidate:1"));
        var answer = await caller.WaitForCallSignalAsync(callId, s => s.Type == "Answer");
        await caller.WaitForCallSignalAsync(callId, s => s.Type == "Candidate");
        Assert.Equal("v=0 answer", answer.Payload);

        await caller.EndCallAsync(callId);

        var endedForCaller = await caller.WaitForCallEndedAsync(callId);
        var endedForCallee = await callee.WaitForCallEndedAsync(callId);
        Assert.Equal("Completed", endedForCaller.GetProperty("reason").GetString());
        Assert.Equal("Completed", endedForCallee.GetProperty("reason").GetString());

        var recorded = Assert.Single(await CallsOfAsync(conversation));
        Assert.Equal(callId, recorded.Id);
        Assert.Equal((CallEndReason.Completed, CallMedia.Video, alice.Id, bob.Id), (recorded.EndReason, recorded.Media, recorded.CallerId, recorded.CalleeId));
        Assert.NotNull(recorded.AnsweredAt);

        var row = await callee.WaitForMessageAsync(conversation);
        Assert.Equal("Call", row.GetProperty("kind").GetString());

        // Both are free to call again.
        var again = await callee.StartCallAsync(conversation);
        await callee.EndCallAsync(CallIdOf(again));
    }

    [Fact]
    public async Task Every_tab_of_the_callee_rings_and_when_one_answers_the_others_stop_and_signals_go_to_that_one_only()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await using var caller = await HubClient.ConnectAsync(_factory, alice);
        await using var phone = await HubClient.ConnectAsync(_factory, bob);
        await using var laptop = await HubClient.ConnectAsync(_factory, bob);

        var callId = CallIdOf(await caller.StartCallAsync(conversation));
        await phone.WaitForIncomingCallAsync(callId);
        await laptop.WaitForIncomingCallAsync(callId);

        await laptop.AcceptCallAsync(callId);

        await phone.WaitForCallAnsweredAsync(callId);
        AssertCode("call-not-ringing", await RefusedAsync(() => phone.AcceptCallAsync(callId)));
        await caller.WaitForCallAnsweredAsync(callId);

        await caller.SendCallSignalAsync(callId, "Offer", "sdp");
        await laptop.WaitForCallSignalAsync(callId);
        Assert.False(await phone.ReceivedCallSignalAsync(callId));

        await caller.EndCallAsync(callId);
        await phone.WaitForCallEndedAsync(callId);
    }

    [Fact]
    public async Task A_decline_from_any_tab_declines_the_call_for_all_of_them_and_the_caller_sees_it()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await using var caller = await HubClient.ConnectAsync(_factory, alice);
        await using var phone = await HubClient.ConnectAsync(_factory, bob);
        await using var laptop = await HubClient.ConnectAsync(_factory, bob);
        var callId = CallIdOf(await caller.StartCallAsync(conversation));
        await phone.WaitForIncomingCallAsync(callId);

        await laptop.EndCallAsync(callId);

        foreach (var client in new[] { caller, phone, laptop })
            Assert.Equal("Declined", (await client.WaitForCallEndedAsync(callId)).GetProperty("reason").GetString());
        Assert.Equal(CallEndReason.Declined, Assert.Single(await CallsOfAsync(conversation)).EndReason);
    }

    [Fact]
    public async Task The_caller_cancelling_while_it_rings_ends_it_as_cancelled_for_the_callee()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await using var caller = await HubClient.ConnectAsync(_factory, alice);
        await using var callee = await HubClient.ConnectAsync(_factory, bob);
        var callId = CallIdOf(await caller.StartCallAsync(conversation));
        await callee.WaitForIncomingCallAsync(callId);

        await caller.EndCallAsync(callId);

        Assert.Equal("Cancelled", (await callee.WaitForCallEndedAsync(callId)).GetProperty("reason").GetString());
        Assert.Equal(CallEndReason.Cancelled, Assert.Single(await CallsOfAsync(conversation)).EndReason);
    }

    [Fact]
    public async Task A_client_that_says_the_media_failed_ends_the_call_as_failed_and_a_second_hang_up_is_harmless()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await using var caller = await HubClient.ConnectAsync(_factory, alice);
        await using var callee = await HubClient.ConnectAsync(_factory, bob);
        var callId = CallIdOf(await caller.StartCallAsync(conversation));
        await callee.AcceptCallAsync(callId);

        await caller.EndCallAsync(callId, failed: true);
        await callee.WaitForCallEndedAsync(callId);
        await callee.EndCallAsync(callId);

        var recorded = Assert.Single(await CallsOfAsync(conversation));
        Assert.Equal(CallEndReason.Failed, recorded.EndReason);
        Assert.NotNull(recorded.AnsweredAt);
    }

    // ##### Who may call #####

    [Fact]
    public async Task Somebody_who_is_not_in_the_conversation_cannot_call_in_it()
    {
        var (_, _, conversation) = await _factory.DirectChatAsync();
        var carol = await _factory.RegisterUserAsync("carol");
        await using var outsider = await HubClient.ConnectAsync(_factory, carol);

        AssertCode("not-a-member", await RefusedAsync(() => outsider.StartCallAsync(conversation)));
        AssertCode("not-a-member", await RefusedAsync(() => outsider.StartCallAsync(Guid.NewGuid())));
    }

    [Fact]
    public async Task A_group_cannot_be_called()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var group = (await client.CreateGroupAsync("Team", bob.Id)).IdOf();
        await using var caller = await HubClient.ConnectAsync(_factory, alice);

        AssertCode("not-a-direct-conversation", await RefusedAsync(() => caller.StartCallAsync(group)));
    }

    [Fact]
    public async Task Somebody_whose_account_was_deactivated_cannot_be_called_and_cannot_call()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await using var caller = await HubClient.ConnectAsync(_factory, alice);
        await using var callee = await HubClient.ConnectAsync(_factory, bob);
        await _factory.ExecuteDbAsync(db => db.Users.Where(u => u.Id == bob.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDeactivated, true)));

        AssertCode("peer-unavailable", await RefusedAsync(() => caller.StartCallAsync(conversation)));
        AssertCode("peer-unavailable", await RefusedAsync(() => callee.StartCallAsync(conversation)));
    }

    [Fact]
    public async Task Calling_somebody_who_is_in_a_call_leaves_a_busy_row_and_somebody_in_a_call_cannot_call_anybody()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        var carol = await _factory.RegisterUserAsync("carol");
        using var carolsClient = _factory.CreateClientFor(carol);
        var carolAndBob = (await carolsClient.CreateDirectAsync(bob.Id)).IdOf();
        var carolAndAlice = (await carolsClient.CreateDirectAsync(alice.Id)).IdOf();
        await using var caller = await HubClient.ConnectAsync(_factory, alice);
        await using var third = await HubClient.ConnectAsync(_factory, carol);
        var callId = CallIdOf(await caller.StartCallAsync(conversation));

        AssertCode("callee-busy", await RefusedAsync(() => third.StartCallAsync(carolAndBob)));
        AssertCode("callee-busy", await RefusedAsync(() => third.StartCallAsync(carolAndAlice)));
        AssertCode("already-in-a-call", await RefusedAsync(() => caller.StartCallAsync(carolAndAlice)));

        var busy = Assert.Single(await CallsOfAsync(carolAndBob));
        Assert.Equal((CallEndReason.Busy, carol.Id, bob.Id), (busy.EndReason, busy.CallerId, busy.CalleeId));
        Assert.Null(busy.AnsweredAt);
        await caller.EndCallAsync(callId);
    }

    // ##### Answering and ending #####

    [Fact]
    public async Task Only_the_callee_can_answer_and_only_somebody_in_the_call_can_end_it()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        var carol = await _factory.RegisterUserAsync("carol");
        await using var caller = await HubClient.ConnectAsync(_factory, alice);
        await using var stranger = await HubClient.ConnectAsync(_factory, carol);
        var callId = CallIdOf(await caller.StartCallAsync(conversation));

        AssertCode("not-the-callee", await RefusedAsync(() => caller.AcceptCallAsync(callId)));
        AssertCode("not-the-callee", await RefusedAsync(() => stranger.AcceptCallAsync(callId)));
        AssertCode("call-not-found", await RefusedAsync(() => stranger.AcceptCallAsync(Guid.NewGuid())));
        AssertCode("call-not-found", await RefusedAsync(() => stranger.EndCallAsync(callId)));

        await caller.EndCallAsync(callId);
        await caller.EndCallAsync(Guid.NewGuid());
        _ = bob;
    }

    // ##### Signals #####

    [Fact]
    public async Task A_signal_is_relayed_only_from_a_side_of_an_active_call()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await using var caller = await HubClient.ConnectAsync(_factory, alice);
        await using var othertab = await HubClient.ConnectAsync(_factory, alice);
        await using var callee = await HubClient.ConnectAsync(_factory, bob);
        var callId = CallIdOf(await caller.StartCallAsync(conversation));

        // Not before the call is answered: the offer waits for it.
        Assert.False(await caller.SendCallSignalAsync(callId, "Offer", "too early"));

        await callee.AcceptCallAsync(callId);
        AssertCode("not-in-the-call", await RefusedAsync(() => othertab.SendCallSignalAsync(callId, "Offer", "from a tab that is not the caller")));
        Assert.False(await callee.ReceivedCallSignalAsync(callId));

        await caller.EndCallAsync(callId);
        Assert.False(await caller.SendCallSignalAsync(callId, "Candidate", "too late"));
    }

    [Fact]
    public async Task A_signal_that_is_too_large_and_a_call_that_has_carried_its_share_are_refused()
    {
        using var host = Quick(new() { ["Calls:MaxSignalsPerCall"] = "3", ["Calls:RingTimeout"] = "00:00:30", ["Calls:TickInterval"] = "01:00:00", ["Calls:RenewInterval"] = "01:00:00", ["Calls:Lease"] = "02:00:00" });
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await using var caller = await HubClient.ConnectAsync(host, alice);
        await using var callee = await HubClient.ConnectAsync(host, bob);
        var callId = CallIdOf(await caller.StartCallAsync(conversation));
        await callee.AcceptCallAsync(callId);

        AssertCode("signal-too-large", await RefusedAsync(() => caller.SendCallSignalAsync(callId, "Offer", new string('x', 16 * 1024 + 1))));
        Assert.True(await caller.SendCallSignalAsync(callId, "Offer", new string('x', 16 * 1024)));

        Assert.True(await caller.SendCallSignalAsync(callId, "Candidate", "1"));
        Assert.True(await callee.SendCallSignalAsync(callId, "Candidate", "2"));
        AssertCode("signal-limit", await RefusedAsync(() => caller.SendCallSignalAsync(callId, "Candidate", "3")));
        await caller.EndCallAsync(callId);
    }

    // ##### When a call is not answered, and when a connection drops #####

    [Fact]
    public async Task A_ring_that_runs_out_ends_the_call_as_missed_for_both_and_is_recorded()
    {
        using var host = Quick();
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await using var caller = await HubClient.ConnectAsync(host, alice);
        await using var callee = await HubClient.ConnectAsync(host, bob);

        var callId = CallIdOf(await caller.StartCallAsync(conversation));

        Assert.Equal("Missed", (await callee.WaitForCallEndedAsync(callId, Long)).GetProperty("reason").GetString());
        Assert.Equal("Missed", (await caller.WaitForCallEndedAsync(callId, Long)).GetProperty("reason").GetString());
        var recorded = Assert.Single(await CallsOfAsync(conversation));
        Assert.Equal(CallEndReason.Missed, recorded.EndReason);
        AssertCode("call-not-found", await RefusedAsync(() => callee.AcceptCallAsync(callId)));
    }

    [Fact]
    public async Task A_side_that_drops_comes_back_with_its_token_inside_the_grace_and_the_call_goes_on()
    {
        using var host = Quick(new() { ["Calls:RingTimeout"] = "00:00:30", ["Calls:ReconnectGrace"] = "00:00:03" });
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        var firstTab = await HubClient.ConnectAsync(host, alice);
        await using var callee = await HubClient.ConnectAsync(host, bob);
        var started = await firstTab.StartCallAsync(conversation);
        var callId = CallIdOf(started);
        await callee.AcceptCallAsync(callId);

        await firstTab.StopAsync();
        await firstTab.DisposeAsync();
        await using var secondTab = await HubClient.ConnectAsync(host, alice);
        var rejoined = await secondTab.RejoinCallAsync(callId, started.GetProperty("rejoinToken").GetString()!);

        Assert.Equal("Active", rejoined.GetProperty("state").GetString());
        Assert.True(await secondTab.SendCallSignalAsync(callId, "Offer", "restart"));
        await callee.WaitForCallSignalAsync(callId);
        Assert.True(await callee.SendCallSignalAsync(callId, "Answer", "restart-answer"));
        await secondTab.WaitForCallSignalAsync(callId);

        // The grace has passed and the call is still there.
        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.False(await callee.ReceivedCallEndedAsync(callId));
        await secondTab.EndCallAsync(callId);
    }

    [Fact]
    public async Task A_rejoin_needs_the_token_the_call_and_the_person()
    {
        using var host = Quick(new() { ["Calls:RingTimeout"] = "00:00:30" });
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        var carol = await _factory.RegisterUserAsync("carol");
        await using var caller = await HubClient.ConnectAsync(host, alice);
        await using var callee = await HubClient.ConnectAsync(host, bob);
        await using var stranger = await HubClient.ConnectAsync(host, carol);
        var started = await caller.StartCallAsync(conversation);
        var callId = CallIdOf(started);
        var token = started.GetProperty("rejoinToken").GetString()!;
        await callee.AcceptCallAsync(callId);

        AssertCode("bad-token", await RefusedAsync(() => caller.RejoinCallAsync(callId, "not-the-token")));
        AssertCode("not-in-the-call", await RefusedAsync(() => stranger.RejoinCallAsync(callId, token)));
        AssertCode("call-not-found", await RefusedAsync(() => caller.RejoinCallAsync(Guid.NewGuid(), token)));

        await caller.EndCallAsync(callId);
        AssertCode("call-not-found", await RefusedAsync(() => caller.RejoinCallAsync(callId, token)));
    }

    [Fact]
    public async Task An_active_call_whose_side_does_not_come_back_within_the_grace_ends_as_failed()
    {
        using var host = Quick(new() { ["Calls:RingTimeout"] = "00:00:30" });
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        var caller = await HubClient.ConnectAsync(host, alice);
        await using var callee = await HubClient.ConnectAsync(host, bob);
        var callId = CallIdOf(await caller.StartCallAsync(conversation));
        await callee.AcceptCallAsync(callId);

        await caller.StopAsync();
        await caller.DisposeAsync();

        Assert.Equal("Failed", (await callee.WaitForCallEndedAsync(callId, Long)).GetProperty("reason").GetString());
        Assert.Equal(CallEndReason.Failed, Assert.Single(await CallsOfAsync(conversation)).EndReason);
    }

    [Fact]
    public async Task A_caller_who_goes_away_while_it_rings_has_cancelled_once_the_grace_has_passed()
    {
        using var host = Quick(new() { ["Calls:RingTimeout"] = "00:00:30" });
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        var caller = await HubClient.ConnectAsync(host, alice);
        await using var callee = await HubClient.ConnectAsync(host, bob);
        var callId = CallIdOf(await caller.StartCallAsync(conversation));
        await callee.WaitForIncomingCallAsync(callId);

        await caller.StopAsync();
        await caller.DisposeAsync();

        Assert.Equal("Cancelled", (await callee.WaitForCallEndedAsync(callId, Long)).GetProperty("reason").GetString());
    }

    // ##### When recording the call fails #####

    // Refuses the first write of a call, as a database that was briefly unreachable would.
    private sealed class FailCallWrites : SaveChangesInterceptor
    {
        private int _failures;

        public FailCallWrites(int failures) => _failures = failures;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Call>().Any(e => e.State == EntityState.Added) && Interlocked.Decrement(ref _failures) >= 0)
                throw new DbUpdateException("The database refused the call.");

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task A_call_that_could_not_be_recorded_stays_ending_and_the_next_round_records_it_and_the_people_are_told_all_the_same()
    {
        var settings = CallHosts.QuickSettings(new()
        {
            ["Calls:RingTimeout"] = "00:00:30",
            ["Calls:TickInterval"] = "01:00:00",
            ["Calls:RenewInterval"] = "01:00:00",
            ["Calls:Lease"] = "02:00:00",
            ["Calls:EndingRetry"] = "00:00:01",
        });
        // One interceptor for the host: the options are built for every context, and each would otherwise have a failure of its own.
        var refusesOnce = new FailCallWrites(1);
        using var host = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            builder.ConfigureTestServices(services => services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(refusesOnce)));
        });
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await using var caller = await HubClient.ConnectAsync(host, alice);
        await using var callee = await HubClient.ConnectAsync(host, bob);
        var callId = CallIdOf(await caller.StartCallAsync(conversation));
        await callee.AcceptCallAsync(callId);

        await caller.EndCallAsync(callId);

        // The call is over for them, and the live state is kept until the call has been recorded.
        await callee.WaitForCallEndedAsync(callId);
        Assert.Empty(await CallsOfAsync(conversation));
        var store = host.Services.GetRequiredService<CallStore>();
        Assert.Equal(CallState.Ending, (await store.GetOfAsync(alice.Id, default))!.State);

        await Task.Delay(TimeSpan.FromSeconds(1.3));
        await host.Services.GetRequiredService<CallRefreshService>().RunOnceAsync(CancellationToken.None);

        Assert.Equal(CallEndReason.Completed, Assert.Single(await CallsOfAsync(conversation)).EndReason);
        Assert.Null(await store.GetOfAsync(alice.Id, default));
        Assert.Null(await store.GetOfAsync(bob.Id, default));
    }

    // ##### Opening a tab while it rings, and the limit on starting #####

    [Fact]
    public async Task A_tab_that_opens_while_the_call_rings_rings_too_and_one_that_opens_after_it_was_answered_does_not()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await using var caller = await HubClient.ConnectAsync(_factory, alice);
        await using var firstTab = await HubClient.ConnectAsync(_factory, bob);
        var callId = CallIdOf(await caller.StartCallAsync(conversation));
        await firstTab.WaitForIncomingCallAsync(callId);

        await using var lateTab = await HubClient.ConnectAsync(_factory, bob);
        var rung = await lateTab.WaitForIncomingCallAsync(callId);
        Assert.Equal(alice.DisplayName, rung.GetProperty("callerDisplayName").GetString());

        // The caller's own other tab is not rung for the call it is making.
        await using var callersOtherTab = await HubClient.ConnectAsync(_factory, alice);
        Assert.False(await callersOtherTab.ReceivedIncomingCallAsync(callId));

        await firstTab.AcceptCallAsync(callId);
        await using var afterwards = await HubClient.ConnectAsync(_factory, bob);
        Assert.False(await afterwards.ReceivedIncomingCallAsync(callId));
        await caller.EndCallAsync(callId);
    }

    [Fact]
    public async Task A_person_who_starts_calls_faster_than_the_limit_is_refused()
    {
        using var host = Quick(new()
        {
            ["RateLimiting:StartCall:PermitLimit"] = "2",
            ["RateLimiting:StartCall:WindowSeconds"] = "60",
            ["Calls:RingTimeout"] = "00:00:30",
            ["Calls:TickInterval"] = "01:00:00",
            ["Calls:RenewInterval"] = "01:00:00",
            ["Calls:Lease"] = "02:00:00",
        });
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        await using var caller = await HubClient.ConnectAsync(host, alice);

        for (var i = 0; i < 2; i++)
            await caller.EndCallAsync(CallIdOf(await caller.StartCallAsync(conversation)));

        AssertCode("rate-limited", await RefusedAsync(() => caller.StartCallAsync(conversation)));
    }
}
