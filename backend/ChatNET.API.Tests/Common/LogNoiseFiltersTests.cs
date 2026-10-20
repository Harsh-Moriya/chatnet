using ChatNET.API.Common.Logging;
using Microsoft.AspNetCore.SignalR;
using Serilog.Events;

namespace ChatNET.API.Tests.Common;

// The filter decides which framework log events are dropped, so it is checked on both sides: the
// exact event it must drop, and the near misses it must keep.
public class LogNoiseFiltersTests
{
    private const string HubDispatcher = "Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher";

    [Fact]
    public void A_hub_exception_logged_by_the_hub_dispatcher_is_dropped()
    {
        var logEvent = Event(HubDispatcher, new HubException("Not a member."));

        Assert.True(LogNoiseFilters.IsRefusedHubCall(logEvent));
    }

    [Fact]
    public void Any_other_exception_logged_by_the_hub_dispatcher_is_kept()
    {
        var logEvent = Event(HubDispatcher, new InvalidOperationException("A real bug."));

        Assert.False(LogNoiseFilters.IsRefusedHubCall(logEvent));
    }

    [Fact]
    public void A_hub_exception_logged_by_something_else_is_kept()
    {
        var logEvent = Event("ChatNET.API.Messaging.Hubs.ChatHub", new HubException("Not a member."));

        Assert.False(LogNoiseFilters.IsRefusedHubCall(logEvent));
    }

    [Fact]
    public void An_event_without_an_exception_or_a_source_is_kept()
    {
        var withoutException = Event(HubDispatcher, null);
        var withoutSource = new LogEvent(
            DateTimeOffset.UtcNow, LogEventLevel.Error, new HubException("Not a member."),
            new MessageTemplate("x", []), []);

        Assert.False(LogNoiseFilters.IsRefusedHubCall(withoutException));
        Assert.False(LogNoiseFilters.IsRefusedHubCall(withoutSource));
    }

    private static LogEvent Event(string sourceContext, Exception? exception)
        => new(
            DateTimeOffset.UtcNow,
            LogEventLevel.Error,
            exception,
            new MessageTemplate("something failed", []),
            [new LogEventProperty("SourceContext", new ScalarValue(sourceContext))]);
}
