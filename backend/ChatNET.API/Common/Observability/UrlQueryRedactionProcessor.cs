using System.Diagnostics;
using OpenTelemetry;

namespace ChatNET.API.Common.Observability;

// The ASP.NET Core instrumentation records the query string of every request as "url.query", and
// only hides a fixed list of well-known secret names. The SignalR client sends its access token as
// "?access_token=", which is not on that list, so the token would be copied into every trace. Nothing
// in this application needs a query string in a trace, so the whole tag is dropped.
public class UrlQueryRedactionProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        activity.SetTag("url.query", null);
        activity.SetTag("url.full", null);
    }
}
