using ChatNET.API.Calls;
using ChatNET.API.Calls.Models;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Calls;

// Calls with the two people on different API instances, which share Redis (the live state of calls and the SignalR backplane) and the
// database: what one instance does is seen by the other, a call that two instances both try to end is ended once, and a call whose
// instance stopped looking after it is ended by the other.
[Collection(IntegrationTestCollection.Name)]
public class CallTwoInstanceTests : IAsyncLifetime
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(12);

    private readonly ChatApiFactory _factory;

    public CallTwoInstanceTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.RemoveWaitingOutboxRowsAsync();

    public Task DisposeAsync() => _factory.RemoveWaitingOutboxRowsAsync();

    private static Guid CallIdOf(System.Text.Json.JsonElement started) => started.GetProperty("callId").GetGuid();

    private Task<List<Call>> CallsOfAsync(Guid conversation) =>
        _factory.QueryDbAsync(db => db.Calls.AsNoTracking().Where(c => c.ConversationId == conversation).ToListAsync());

    private Task<int> CallRowsAsync(Guid conversation) =>
        _factory.QueryDbAsync(db => db.Messages.AsNoTracking().CountAsync(m => m.ConversationId == conversation && m.CallId != null));

    [Fact]
    public async Task A_call_between_people_on_two_instances_rings_is_answered_signalled_and_ended_across_them()
    {
        using var other = _factory.WithWebHostBuilder(_ => { });
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await using var caller = await HubClient.ConnectAsync(_factory, alice);
        await using var callee = await HubClient.ConnectAsync(other, bob);

        var callId = CallIdOf(await caller.StartCallAsync(conversation));
        await callee.WaitForIncomingCallAsync(callId);
        await callee.AcceptCallAsync(callId);
        await caller.WaitForCallAnsweredAsync(callId);

        Assert.True(await caller.SendCallSignalAsync(callId, "Offer", "sdp-offer"));
        Assert.Equal("sdp-offer", (await callee.WaitForCallSignalAsync(callId)).Payload);
        Assert.True(await callee.SendCallSignalAsync(callId, "Answer", "sdp-answer"));
        Assert.Equal("sdp-answer", (await caller.WaitForCallSignalAsync(callId)).Payload);

        await callee.EndCallAsync(callId);

        Assert.Equal("Completed", (await caller.WaitForCallEndedAsync(callId)).GetProperty("reason").GetString());
        Assert.Equal("Completed", (await callee.WaitForCallEndedAsync(callId)).GetProperty("reason").GetString());
        Assert.Single(await CallsOfAsync(conversation));
    }

    [Fact]
    public async Task Two_tabs_of_the_callee_on_different_instances_answering_together_leave_one_winner()
    {
        using var other = _factory.WithWebHostBuilder(_ => { });
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await using var caller = await HubClient.ConnectAsync(_factory, alice);
        await using var here = await HubClient.ConnectAsync(_factory, bob);
        await using var there = await HubClient.ConnectAsync(other, bob);
        var callId = CallIdOf(await caller.StartCallAsync(conversation));
        await here.WaitForIncomingCallAsync(callId);
        await there.WaitForIncomingCallAsync(callId);

        var answers = await Task.WhenAll(
            Task.Run(async () => await Try(() => here.AcceptCallAsync(callId))),
            Task.Run(async () => await Try(() => there.AcceptCallAsync(callId))));

        Assert.Single(answers, answered => answered);
        await caller.WaitForCallAnsweredAsync(callId);
        await caller.EndCallAsync(callId);

        static async Task<bool> Try(Func<Task> accept)
        {
            try
            {
                await accept();
                return true;
            }
            catch (HubException)
            {
                return false;
            }
        }
    }

    [Fact]
    public async Task Two_instances_that_both_sweep_end_one_call_once_and_tell_each_person_once()
    {
        using var first = _factory.QuickCalls();
        using var second = _factory.QuickCalls();
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await using var caller = await HubClient.ConnectAsync(first, alice);
        await using var callee = await HubClient.ConnectAsync(second, bob);

        var callId = CallIdOf(await caller.StartCallAsync(conversation));
        await callee.WaitForCallEndedAsync(callId, Long);
        await caller.WaitForCallEndedAsync(callId, Long);

        // Rounds keep running on both instances; nothing is written or sent a second time.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(CallEndReason.Missed, Assert.Single(await CallsOfAsync(conversation)).EndReason);
        Assert.Equal(1, await CallRowsAsync(conversation));
        Assert.Single(callee.CallsEnded, e => e.GetProperty("callId").GetGuid() == callId);
        Assert.Single(caller.CallsEnded, e => e.GetProperty("callId").GetGuid() == callId);
    }

    [Fact]
    public async Task A_call_whose_instance_stopped_looking_after_it_is_ended_once_by_the_other_instance()
    {
        using var dying = _factory.QuickCalls(new() { ["Calls:RingTimeout"] = "00:00:30" });
        using var surviving = _factory.QuickCalls(new() { ["Calls:RingTimeout"] = "00:00:30" });
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await using var caller = await HubClient.ConnectAsync(dying, alice);
        await using var callee = await HubClient.ConnectAsync(surviving, bob);
        var callId = CallIdOf(await caller.StartCallAsync(conversation));
        await callee.AcceptCallAsync(callId);

        // The instance the caller is on stops renewing, as one that died would. Its connection stays, because nobody told the others it
        // was gone; the other instance goes on renewing the callee.
        await dying.Services.GetRequiredService<CallRefreshService>().StopAsync(CancellationToken.None);

        Assert.Equal("Failed", (await callee.WaitForCallEndedAsync(callId, Long)).GetProperty("reason").GetString());
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(CallEndReason.Failed, Assert.Single(await CallsOfAsync(conversation)).EndReason);
        Assert.Single(callee.CallsEnded, e => e.GetProperty("callId").GetGuid() == callId);
    }
}
