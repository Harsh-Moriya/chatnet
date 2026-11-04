using System.Diagnostics;
using ChatNET.API.Common.Observability;

namespace ChatNET.API.Tests.Push;

// The call to a push service is traced like any other call the server makes, and its address is a capability. The tracing setup
// takes the address off every span before it is exported (UrlQueryRedactionProcessor); this is the part that keeps the endpoint of a
// browser out of the trace store.
public class PushTracingTests
{
    [Fact]
    public void The_address_of_a_push_service_endpoint_is_taken_off_a_span_and_the_rest_is_kept()
    {
        using var source = new ActivitySource("push-tracing-test");
        using var listener = new ActivityListener
        {
            ShouldListenTo = candidate => candidate.Name == "push-tracing-test",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var span = source.StartActivity("POST");
        Assert.NotNull(span);
        span.SetTag("url.full", "https://fcm.googleapis.com/fcm/send/the-capability-of-a-browser");
        span.SetTag("url.query", "access_token=secret");
        span.SetTag("server.address", "fcm.googleapis.com");
        span.SetTag("http.response.status_code", 201);

        new UrlQueryRedactionProcessor().OnEnd(span);

        Assert.DoesNotContain(span.TagObjects, tag => tag.Value?.ToString()?.Contains("the-capability-of-a-browser") == true);
        Assert.Null(span.GetTagItem("url.full"));
        Assert.Null(span.GetTagItem("url.query"));
        Assert.Equal("fcm.googleapis.com", span.GetTagItem("server.address"));
        Assert.Equal(201, span.GetTagItem("http.response.status_code"));
    }
}
