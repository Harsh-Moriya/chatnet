using Microsoft.AspNetCore.SignalR;
using Serilog.Events;

namespace ChatNET.API.Common.Logging;

// SignalR logs every exception a hub method throws at Error, including a HubException, which is the
// deliberate way to refuse a call (not a member, for example). Left in place, every refusal would
// look like a failure in production logs and trip any alert on the Error level.
// RefusedCallLoggingFilter logs those refusals at Warning instead, with the user and the method.
//
// The filter is narrow: it names the exact source and the exact exception type, so anything else
// the hub dispatcher reports is still logged as an error.
public static class LogNoiseFilters
{
    private const string HubDispatcher = "Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher";

    public static bool IsRefusedHubCall(LogEvent logEvent)
        => logEvent.Exception is HubException
           && logEvent.Properties.TryGetValue("SourceContext", out var value)
           && value is ScalarValue { Value: HubDispatcher };
}
