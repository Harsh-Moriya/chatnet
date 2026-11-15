using ChatNET.API.Calls;
using ChatNET.API.Calls.Models;
using ChatNET.API.Presence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ChatNET.API.Tests.Calls;

// The store is the part of calls that has to be right when several API instances act at once (an accept and a decline together, two
// hang-ups, an accept against the ring running out), so these tests use the real Redis and real concurrency. Everything that expires does
// so within a second, short enough to let it happen inside a test.
[Collection(IntegrationTestCollection.Name)]
public class CallStoreTests
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(1);

    private readonly ChatApiFactory _factory;

    public CallStoreTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private RedisConnection Redis() =>
        new(_factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("Redis")!);

    private CallStore NewStore(Action<CallOptions>? configure = null, RedisConnection? redis = null)
    {
        var options = new CallOptions
        {
            RingTimeout = Short,
            ReconnectGrace = Short,
            Lease = Short,
            EndingRetry = Short,
            MaxSignalsPerCall = 3,
        };
        configure?.Invoke(options);
        return new CallStore(redis ?? Redis(), Options.Create(options));
    }

    private static Task WaitOutAsync() => Task.Delay(Short + TimeSpan.FromMilliseconds(400));

    private sealed record Rig(CallStore Store, Guid Id, Guid Caller, Guid Callee)
    {
        public const string CallerConnection = "caller-1";
        public const string CalleeConnection = "callee-1";
        public const string CallerToken = "caller-token";
        public const string CalleeToken = "callee-token";
    }

    // A call that is ringing: the caller's first connection started it.
    private async Task<Rig> RingingAsync(Action<CallOptions>? configure = null, CallMedia media = CallMedia.Audio)
    {
        var rig = new Rig(NewStore(configure), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var started = await rig.Store.StartAsync(
            new NewCall(rig.Id, Guid.NewGuid(), rig.Caller, rig.Callee, media), Rig.CallerConnection, Rig.CallerToken, default);
        Assert.Equal(StartOutcome.Started, started.Outcome);
        return rig;
    }

    private static async Task<CallSnapshot> AnsweredAsync(Rig rig)
    {
        var accepted = await rig.Store.AcceptAsync(rig.Id, rig.Callee, Rig.CalleeConnection, Rig.CalleeToken, default);
        Assert.Equal(AcceptOutcome.Accepted, accepted.Outcome);
        return accepted.Call!;
    }

    private async Task<Rig> ActiveAsync(Action<CallOptions>? configure = null)
    {
        var rig = await RingingAsync(configure);
        await AnsweredAsync(rig);
        return rig;
    }

    private static Task<EndResult> EndAsync(Rig rig, Guid? by, bool failed = false) => rig.Store.EndAsync(rig.Id, by, failed, default);

    // ##### Starting #####

    [Fact]
    public async Task A_call_between_two_free_people_starts_and_rings_until_the_ring_timeout()
    {
        var store = NewStore();
        var call = new NewCall(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CallMedia.Video);

        var started = await store.StartAsync(call, "c1", "t", default);

        Assert.Equal(StartOutcome.Started, started.Outcome);
        Assert.Equal(Short, started.RingsUntil - started.At);
        var held = await store.GetAsync(call.Id, default);
        Assert.Equal(CallState.Ringing, held!.State);
        Assert.Equal(CallMedia.Video, held.Media);
        Assert.Equal("c1", held.CallerConnection);
        Assert.Equal(string.Empty, held.CalleeConnection);
        Assert.Null(held.AnsweredAt);
    }

    [Fact]
    public async Task A_person_who_is_already_in_a_call_cannot_start_another_and_neither_can_one_who_is_being_called()
    {
        var rig = await RingingAsync();

        var callerAgain = await rig.Store.StartAsync(new NewCall(Guid.NewGuid(), Guid.NewGuid(), rig.Caller, Guid.NewGuid(), CallMedia.Audio), "c2", "t", default);
        var calleeCalling = await rig.Store.StartAsync(new NewCall(Guid.NewGuid(), Guid.NewGuid(), rig.Callee, Guid.NewGuid(), CallMedia.Audio), "c3", "t", default);

        Assert.Equal(StartOutcome.CallerBusy, callerAgain.Outcome);
        Assert.Equal(StartOutcome.CallerBusy, calleeCalling.Outcome);
    }

    [Fact]
    public async Task Calling_somebody_who_is_in_a_call_is_refused_as_busy_and_leaves_nothing_behind()
    {
        var rig = await RingingAsync();
        var other = new NewCall(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), rig.Callee, CallMedia.Audio);

        var started = await rig.Store.StartAsync(other, "c9", "t", default);

        Assert.Equal(StartOutcome.CalleeBusy, started.Outcome);
        Assert.Null(await rig.Store.GetAsync(other.Id, default));
        Assert.Null(await rig.Store.GetOfAsync(other.CallerId, default));
    }

    [Fact]
    public async Task A_busy_marker_whose_call_is_gone_does_not_make_anybody_busy()
    {
        var redis = Redis();
        var store = NewStore(redis: redis);
        var caller = Guid.NewGuid();
        var db = await redis.GetDatabaseAsync();
        await db.StringSetAsync($"chatnet:call:user:{caller}", Guid.NewGuid().ToString());

        var started = await store.StartAsync(new NewCall(Guid.NewGuid(), Guid.NewGuid(), caller, Guid.NewGuid(), CallMedia.Audio), "c1", "t", default);

        Assert.Equal(StartOutcome.Started, started.Outcome);
    }

    [Fact]
    public async Task Both_people_are_found_in_the_call_by_their_id()
    {
        var rig = await RingingAsync();

        Assert.Equal(rig.Id, (await rig.Store.GetOfAsync(rig.Caller, default))!.Id);
        Assert.Equal(rig.Id, (await rig.Store.GetOfAsync(rig.Callee, default))!.Id);
        Assert.Null(await rig.Store.GetOfAsync(Guid.NewGuid(), default));
    }

    // ##### Answering #####

    [Fact]
    public async Task The_callee_answers_and_the_call_is_active_with_their_connection()
    {
        var rig = await RingingAsync();

        var call = await AnsweredAsync(rig);

        Assert.Equal(CallState.Active, call.State);
        Assert.NotNull(call.AnsweredAt);
        Assert.Equal(Rig.CalleeConnection, call.CalleeConnection);
        Assert.Equal(Rig.CallerConnection, call.CallerConnection);
    }

    [Theory]
    [InlineData("the caller")]
    [InlineData("a stranger")]
    public async Task Only_the_callee_can_answer(string who)
    {
        var rig = await RingingAsync();

        var result = await rig.Store.AcceptAsync(rig.Id, who == "the caller" ? rig.Caller : Guid.NewGuid(), "x", "t", default);

        Assert.Equal(AcceptOutcome.NotTheCallee, result.Outcome);
        Assert.Equal(CallState.Ringing, (await rig.Store.GetAsync(rig.Id, default))!.State);
    }

    [Fact]
    public async Task A_call_that_does_not_exist_cannot_be_answered()
    {
        var result = await NewStore().AcceptAsync(Guid.NewGuid(), Guid.NewGuid(), "x", "t", default);

        Assert.Equal(AcceptOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task A_second_answer_finds_the_call_answered_and_changes_nothing()
    {
        var rig = await ActiveAsync();

        var second = await rig.Store.AcceptAsync(rig.Id, rig.Callee, "callee-2", "other-token", default);

        Assert.Equal(AcceptOutcome.NotRinging, second.Outcome);
        Assert.Equal(Rig.CalleeConnection, (await rig.Store.GetAsync(rig.Id, default))!.CalleeConnection);
    }

    [Fact]
    public async Task A_call_cannot_be_answered_once_its_ring_has_run_out_even_before_anybody_has_ended_it()
    {
        var rig = await RingingAsync();
        await WaitOutAsync();

        var result = await rig.Store.AcceptAsync(rig.Id, rig.Callee, "late", "t", default);

        Assert.Equal(AcceptOutcome.NotRinging, result.Outcome);
    }

    [Fact]
    public async Task Of_several_connections_answering_at_once_exactly_one_gets_the_call()
    {
        var rig = await RingingAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(i => Task.Run(() => rig.Store.AcceptAsync(rig.Id, rig.Callee, $"tab-{i}", $"token-{i}", default))));

        var winners = results.Where(r => r.Outcome == AcceptOutcome.Accepted).ToList();
        Assert.Single(winners);
        Assert.Equal(11, results.Count(r => r.Outcome == AcceptOutcome.NotRinging));
        Assert.Equal(winners[0].Call!.CalleeConnection, (await rig.Store.GetAsync(rig.Id, default))!.CalleeConnection);
    }

    // ##### Ending #####

    [Theory]
    [InlineData("caller", false, CallEndReason.Cancelled)]
    [InlineData("callee", false, CallEndReason.Declined)]
    public async Task Ending_a_ringing_call_is_a_cancel_by_the_caller_and_a_decline_by_the_callee(string who, bool failed, CallEndReason reason)
    {
        var rig = await RingingAsync();

        var ended = await EndAsync(rig, who == "caller" ? rig.Caller : rig.Callee, failed);

        Assert.Equal(EndOutcome.Ended, ended.Outcome);
        Assert.Equal(reason, ended.Call!.Reason);
        Assert.Equal(CallState.Ending, ended.Call.State);
        Assert.Null(ended.Call.AnsweredAt);
        Assert.NotNull(ended.Call.EndedAt);
    }

    [Theory]
    [InlineData("caller", false, CallEndReason.Completed)]
    [InlineData("callee", false, CallEndReason.Completed)]
    [InlineData("caller", true, CallEndReason.Failed)]
    [InlineData("callee", true, CallEndReason.Failed)]
    public async Task Ending_an_active_call_is_completed_unless_the_person_says_the_media_failed(string who, bool failed, CallEndReason reason)
    {
        var rig = await ActiveAsync();

        var ended = await EndAsync(rig, who == "caller" ? rig.Caller : rig.Callee, failed);

        Assert.Equal(reason, ended.Call!.Reason);
        Assert.NotNull(ended.Call.AnsweredAt);
        Assert.True(ended.Call.EndedAt >= ended.Call.AnsweredAt);
    }

    [Fact]
    public async Task Somebody_who_is_not_in_the_call_cannot_end_it_and_a_call_that_is_not_there_has_no_end()
    {
        var rig = await RingingAsync();

        Assert.Equal(EndOutcome.NotInTheCall, (await EndAsync(rig, Guid.NewGuid())).Outcome);
        Assert.Equal(CallState.Ringing, (await rig.Store.GetAsync(rig.Id, default))!.State);
        Assert.Equal(EndOutcome.NotFound, (await NewStore().EndAsync(Guid.NewGuid(), Guid.NewGuid(), false, default)).Outcome);
    }

    [Fact]
    public async Task A_call_is_ended_once_and_a_second_hang_up_finds_it_already_ending()
    {
        var rig = await ActiveAsync();

        var first = await EndAsync(rig, rig.Caller);
        var second = await EndAsync(rig, rig.Callee);

        Assert.Equal(EndOutcome.Ended, first.Outcome);
        Assert.Equal(EndOutcome.AlreadyEnding, second.Outcome);
        Assert.Equal(CallEndReason.Completed, (await rig.Store.GetAsync(rig.Id, default))!.Reason);
    }

    [Fact]
    public async Task Of_both_people_hanging_up_at_once_exactly_one_ends_the_call()
    {
        var rig = await ActiveAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(i => Task.Run(() => EndAsync(rig, i % 2 == 0 ? rig.Caller : rig.Callee))));

        Assert.Single(results, r => r.Outcome == EndOutcome.Ended);
        Assert.Equal(9, results.Count(r => r.Outcome == EndOutcome.AlreadyEnding));
    }

    [Fact]
    public async Task An_answer_and_a_decline_at_the_same_moment_leave_one_story_and_never_a_declined_call_that_was_answered()
    {
        for (var round = 0; round < 25; round++)
        {
            var rig = await RingingAsync();

            var accept = Task.Run(() => rig.Store.AcceptAsync(rig.Id, rig.Callee, "tab", "token", default));
            var decline = Task.Run(() => EndAsync(rig, rig.Callee));
            await Task.WhenAll(accept, decline);

            // Either the decline came first (Declined, and the answer finds it not ringing) or the answer did (and the callee's end is then a
            // hang-up of an active call: Completed). Never both.
            var reason = decline.Result.Call!.Reason;
            if (accept.Result.Outcome == AcceptOutcome.Accepted)
                Assert.Equal(CallEndReason.Completed, reason);
            else
            {
                Assert.Equal(AcceptOutcome.NotRinging, accept.Result.Outcome);
                Assert.Equal(CallEndReason.Declined, reason);
            }
        }
    }

    [Fact]
    public async Task Finishing_a_call_removes_it_and_frees_both_people_to_call_again()
    {
        var rig = await ActiveAsync();
        var ended = await EndAsync(rig, rig.Caller);

        await rig.Store.FinishAsync(ended.Call!, default);

        Assert.Null(await rig.Store.GetAsync(rig.Id, default));
        Assert.Null(await rig.Store.GetOfAsync(rig.Caller, default));
        var again = await rig.Store.StartAsync(new NewCall(Guid.NewGuid(), Guid.NewGuid(), rig.Callee, rig.Caller, CallMedia.Audio), "x", "t", default);
        Assert.Equal(StartOutcome.Started, again.Outcome);
    }

    [Fact]
    public async Task Finishing_a_call_leaves_the_busy_marker_of_somebody_who_is_already_in_another_call()
    {
        var rig = await RingingAsync();
        var ended = await EndAsync(rig, rig.Caller);
        var db = await Redis().GetDatabaseAsync();
        var elsewhere = Guid.NewGuid().ToString();
        await db.StringSetAsync($"chatnet:call:user:{rig.Callee}", elsewhere);

        await rig.Store.FinishAsync(ended.Call!, default);

        Assert.Equal(elsewhere, (string?)await db.StringGetAsync($"chatnet:call:user:{rig.Callee}"));
        Assert.Null((string?)await db.StringGetAsync($"chatnet:call:user:{rig.Caller}"));
    }

    // ##### When the system ends a call #####

    [Fact]
    public async Task A_call_that_is_not_due_cannot_be_ended_by_the_system()
    {
        var rig = await ActiveAsync(o => { o.Lease = TimeSpan.FromMinutes(5); o.RingTimeout = TimeSpan.FromMinutes(5); });

        Assert.Equal(EndOutcome.NotDue, (await EndAsync(rig, null)).Outcome);
        Assert.Equal(CallState.Active, (await rig.Store.GetAsync(rig.Id, default))!.State);
    }

    [Fact]
    public async Task A_ring_that_runs_out_ends_the_call_as_missed()
    {
        var rig = await RingingAsync();
        await WaitOutAsync();

        var ended = await EndAsync(rig, null);

        Assert.Equal(EndOutcome.Ended, ended.Outcome);
        Assert.Equal(CallEndReason.Missed, ended.Call!.Reason);
    }

    [Fact]
    public async Task A_caller_who_dropped_and_did_not_come_back_within_the_grace_has_cancelled()
    {
        var rig = await RingingAsync(o => o.RingTimeout = TimeSpan.FromMinutes(5));
        Assert.True(await rig.Store.DetachAsync(rig.Id, rig.Caller, Rig.CallerConnection, default));
        await WaitOutAsync();

        var ended = await EndAsync(rig, null);

        Assert.Equal(CallEndReason.Cancelled, ended.Call!.Reason);
    }

    [Fact]
    public async Task An_active_call_whose_side_ran_out_of_lease_or_grace_has_failed()
    {
        var lost = await ActiveAsync();
        var dropped = await ActiveAsync();
        await dropped.Store.DetachAsync(dropped.Id, dropped.Callee, Rig.CalleeConnection, default);
        await WaitOutAsync();

        Assert.Equal(CallEndReason.Failed, (await EndAsync(lost, null)).Call!.Reason);
        Assert.Equal(CallEndReason.Failed, (await EndAsync(dropped, null)).Call!.Reason);
    }

    [Fact]
    public async Task A_side_that_is_renewed_keeps_the_call_alive_when_the_other_one_is_not()
    {
        var rig = await ActiveAsync(o => o.Lease = TimeSpan.FromSeconds(2));
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(700));
            Assert.True(await rig.Store.RenewAsync(rig.Id, rig.Caller, Rig.CallerConnection, default));
        }

        // Two seconds on, the callee's lease (never renewed) has ended while the caller's has not: the call is due.
        var ended = await EndAsync(rig, null);

        Assert.Equal(EndOutcome.Ended, ended.Outcome);
    }

    // ##### Renewing, dropping and coming back #####

    [Fact]
    public async Task Only_the_current_connection_of_a_side_can_renew_it()
    {
        var rig = await ActiveAsync();

        Assert.True(await rig.Store.RenewAsync(rig.Id, rig.Caller, Rig.CallerConnection, default));
        Assert.False(await rig.Store.RenewAsync(rig.Id, rig.Caller, "another-tab", default));
        Assert.False(await rig.Store.RenewAsync(rig.Id, Guid.NewGuid(), Rig.CallerConnection, default));
        Assert.False(await rig.Store.RenewAsync(Guid.NewGuid(), rig.Caller, Rig.CallerConnection, default));
    }

    [Fact]
    public async Task A_connection_that_closed_marks_its_side_away_and_one_that_was_replaced_does_not()
    {
        var rig = await ActiveAsync();

        Assert.False(await rig.Store.DetachAsync(rig.Id, rig.Caller, "not-the-current-one", default));
        Assert.True(await rig.Store.DetachAsync(rig.Id, rig.Caller, Rig.CallerConnection, default));

        var held = await rig.Store.GetAsync(rig.Id, default);
        Assert.Equal(string.Empty, held!.CallerConnection);
        Assert.Equal(Rig.CalleeConnection, held.CalleeConnection);
    }

    [Fact]
    public async Task A_side_that_dropped_comes_back_with_its_token_on_a_new_connection()
    {
        var rig = await ActiveAsync();
        await rig.Store.DetachAsync(rig.Id, rig.Caller, Rig.CallerConnection, default);

        var back = await rig.Store.RejoinAsync(rig.Id, rig.Caller, "caller-2", Rig.CallerToken, default);

        Assert.Equal(RejoinOutcome.Rejoined, back.Outcome);
        Assert.Equal("caller-2", back.Call!.CallerConnection);
        Assert.True(await rig.Store.RenewAsync(rig.Id, rig.Caller, "caller-2", default));
        Assert.False(await rig.Store.RenewAsync(rig.Id, rig.Caller, Rig.CallerConnection, default));
    }

    [Fact]
    public async Task A_rejoin_after_the_grace_is_still_fine_until_somebody_has_ended_the_call()
    {
        var rig = await ActiveAsync();
        await rig.Store.DetachAsync(rig.Id, rig.Caller, Rig.CallerConnection, default);
        await WaitOutAsync();

        // The callee's instance has kept renewing it; the caller's side has been away longer than its grace.
        Assert.True(await rig.Store.RenewAsync(rig.Id, rig.Callee, Rig.CalleeConnection, default));
        Assert.Equal(RejoinOutcome.Rejoined, (await rig.Store.RejoinAsync(rig.Id, rig.Caller, "caller-2", Rig.CallerToken, default)).Outcome);

        // Back inside its lease: the call is no longer due.
        Assert.Equal(EndOutcome.NotDue, (await EndAsync(rig, null)).Outcome);
    }

    [Fact]
    public async Task A_rejoin_needs_the_right_token_the_right_person_and_a_call_that_has_not_ended()
    {
        var rig = await ActiveAsync();

        Assert.Equal(RejoinOutcome.BadToken, (await rig.Store.RejoinAsync(rig.Id, rig.Caller, "c", "wrong", default)).Outcome);
        Assert.Equal(RejoinOutcome.BadToken, (await rig.Store.RejoinAsync(rig.Id, rig.Caller, "c", Rig.CalleeToken, default)).Outcome);
        Assert.Equal(RejoinOutcome.NotInTheCall, (await rig.Store.RejoinAsync(rig.Id, Guid.NewGuid(), "c", Rig.CallerToken, default)).Outcome);
        Assert.Equal(RejoinOutcome.NotFound, (await rig.Store.RejoinAsync(Guid.NewGuid(), rig.Caller, "c", Rig.CallerToken, default)).Outcome);

        await EndAsync(rig, rig.Caller);
        Assert.Equal(RejoinOutcome.WrongState, (await rig.Store.RejoinAsync(rig.Id, rig.Caller, "c", Rig.CallerToken, default)).Outcome);
    }

    [Fact]
    public async Task The_callee_has_no_token_until_it_answers_so_it_cannot_rejoin_a_ringing_call()
    {
        var rig = await RingingAsync();

        Assert.Equal(RejoinOutcome.BadToken, (await rig.Store.RejoinAsync(rig.Id, rig.Callee, "c", Rig.CalleeToken, default)).Outcome);
        Assert.Equal(RejoinOutcome.BadToken, (await rig.Store.RejoinAsync(rig.Id, rig.Callee, "c", string.Empty, default)).Outcome);
    }

    // ##### Signals #####

    [Fact]
    public async Task A_signal_goes_to_the_other_sides_connection_and_only_from_a_sides_current_connection()
    {
        var rig = await ActiveAsync();

        var toCallee = await rig.Store.RouteSignalAsync(rig.Id, rig.Caller, Rig.CallerConnection, default);
        var toCaller = await rig.Store.RouteSignalAsync(rig.Id, rig.Callee, Rig.CalleeConnection, default);
        var third = await rig.Store.RouteSignalAsync(rig.Id, rig.Caller, "a-third-tab", default);
        var stranger = await rig.Store.RouteSignalAsync(rig.Id, Guid.NewGuid(), Rig.CallerConnection, default);

        Assert.Equal((SignalOutcome.Routed, Rig.CalleeConnection), (toCallee.Outcome, toCallee.TargetConnection));
        Assert.Equal((SignalOutcome.Routed, Rig.CallerConnection), (toCaller.Outcome, toCaller.TargetConnection));
        Assert.Equal(SignalOutcome.NotASide, third.Outcome);
        Assert.Equal(SignalOutcome.NotASide, stranger.Outcome);
    }

    [Fact]
    public async Task Nothing_is_relayed_before_the_call_is_answered_or_after_it_has_ended()
    {
        var rig = await RingingAsync();
        Assert.Equal(SignalOutcome.NotActive, (await rig.Store.RouteSignalAsync(rig.Id, rig.Caller, Rig.CallerConnection, default)).Outcome);

        await AnsweredAsync(rig);
        await EndAsync(rig, rig.Caller);
        Assert.Equal(SignalOutcome.NotActive, (await rig.Store.RouteSignalAsync(rig.Id, rig.Caller, Rig.CallerConnection, default)).Outcome);
    }

    [Fact]
    public async Task A_signal_to_a_side_that_is_away_is_not_relayed_and_is_not_counted()
    {
        var rig = await ActiveAsync();
        await rig.Store.DetachAsync(rig.Id, rig.Callee, Rig.CalleeConnection, default);

        for (var i = 0; i < 5; i++)
            Assert.Equal(SignalOutcome.PeerAway, (await rig.Store.RouteSignalAsync(rig.Id, rig.Caller, Rig.CallerConnection, default)).Outcome);

        await rig.Store.RejoinAsync(rig.Id, rig.Callee, "callee-2", Rig.CalleeToken, default);
        Assert.Equal(SignalOutcome.Routed, (await rig.Store.RouteSignalAsync(rig.Id, rig.Caller, Rig.CallerConnection, default)).Outcome);
    }

    [Fact]
    public async Task A_call_carries_a_limited_number_of_signals()
    {
        var rig = await ActiveAsync();

        for (var i = 0; i < 3; i++)
            Assert.Equal(SignalOutcome.Routed, (await rig.Store.RouteSignalAsync(rig.Id, rig.Caller, Rig.CallerConnection, default)).Outcome);

        Assert.Equal(SignalOutcome.LimitReached, (await rig.Store.RouteSignalAsync(rig.Id, rig.Caller, Rig.CallerConnection, default)).Outcome);
        Assert.Equal(SignalOutcome.LimitReached, (await rig.Store.RouteSignalAsync(rig.Id, rig.Callee, Rig.CalleeConnection, default)).Outcome);
    }

    // ##### What is due #####

    [Fact]
    public async Task A_call_is_due_when_its_ring_has_run_out_and_not_before()
    {
        var rig = await RingingAsync();
        Assert.DoesNotContain(await rig.Store.DueAsync(1000, default), d => d.Id == rig.Id);

        await WaitOutAsync();

        var due = (await rig.Store.DueAsync(1000, default)).Single(d => d.Id == rig.Id);
        Assert.Equal(CallState.Ringing, due.State);
    }

    [Fact]
    public async Task A_call_that_is_ending_comes_back_for_a_retry_once_per_interval_and_to_one_looker_only()
    {
        var rig = await RingingAsync();
        await EndAsync(rig, rig.Caller);
        Assert.DoesNotContain(await rig.Store.DueAsync(1000, default), d => d.Id == rig.Id);
        await WaitOutAsync();

        var looking = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => rig.Store.DueAsync(1000, default))));

        Assert.Equal(1, looking.Count(due => due.Any(d => d.Id == rig.Id && d.State == CallState.Ending)));
        Assert.DoesNotContain(await rig.Store.DueAsync(1000, default), d => d.Id == rig.Id);
    }

    [Fact]
    public async Task An_entry_whose_call_is_gone_is_dropped_when_it_comes_due()
    {
        var rig = await RingingAsync();
        var ended = await EndAsync(rig, rig.Caller);
        await rig.Store.FinishAsync(ended.Call!, default);
        await WaitOutAsync();

        Assert.DoesNotContain(await rig.Store.DueAsync(1000, default), d => d.Id == rig.Id);
    }
}
