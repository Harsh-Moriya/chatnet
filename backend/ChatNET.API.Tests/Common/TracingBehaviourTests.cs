using System.Diagnostics;
using ChatNET.API.Common.Exceptions;
using ChatNET.API.Common.Observability;
using FluentValidation;
using FluentValidation.Results;
using MediatR;

namespace ChatNET.API.Tests.Common;

// Which failures turn a span red. A refused request (not found, forbidden, invalid input) is the
// application doing its job, so it must not; anything else is a failure somebody has to look at.
public class TracingBehaviourTests
{
    private sealed record Ping(string Text) : IRequest<int>;

    [Fact]
    public async Task A_successful_request_gets_a_span_named_after_the_request_type()
    {
        var spans = await RunAsync(() => Task.FromResult(1));

        var span = Assert.Single(spans);
        Assert.Equal(nameof(Ping), span.OperationName);
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
    }

    [Fact]
    public async Task The_request_itself_is_never_recorded()
    {
        var spans = new List<Activity>();
        using var listener = Listen(spans);

        await new TracingBehaviour<Ping, int>().Handle(new Ping("a secret"), _ => Task.FromResult(1), default);

        Assert.DoesNotContain(spans.Single().TagObjects, tag => tag.Value?.ToString()?.Contains("a secret") == true);
    }

    [Theory]
    [MemberData(nameof(ExpectedFailures))]
    public async Task A_refused_request_is_recorded_but_not_marked_as_an_error(Exception expected)
    {
        var spans = new List<Activity>();
        using var listener = Listen(spans);

        await Assert.ThrowsAsync(expected.GetType(), () => new TracingBehaviour<Ping, int>()
            .Handle(new Ping("x"), _ => throw expected, default));

        var span = Assert.Single(spans);
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.Equal(expected.GetType().Name, span.GetTagItem("chatnet.rejected"));
    }

    [Fact]
    public async Task An_unexpected_failure_marks_the_span_as_an_error_without_its_message()
    {
        var spans = new List<Activity>();
        using var listener = Listen(spans);
        var failure = new InvalidOperationException("Key (Email)=(someone@example.test) already exists.");

        await Assert.ThrowsAsync<InvalidOperationException>(() => new TracingBehaviour<Ping, int>()
            .Handle(new Ping("x"), _ => throw failure, default));

        var span = Assert.Single(spans);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, span.GetTagItem("error.type"));
        Assert.Null(span.StatusDescription);
        Assert.DoesNotContain(span.TagObjects, tag => tag.Value?.ToString()?.Contains("someone@example.test") == true);
    }

    public static TheoryData<Exception> ExpectedFailures() => new()
    {
        new NotFoundException("Thing", 1),
        new ForbiddenException("not allowed"),
        new ConflictException("taken"),
        new ValidationException([new ValidationFailure("Text", "Required")]),
    };

    private static async Task<List<Activity>> RunAsync(Func<Task<int>> next)
    {
        var spans = new List<Activity>();
        using var listener = Listen(spans);

        await new TracingBehaviour<Ping, int>().Handle(new Ping("x"), _ => next(), default);

        return spans;
    }

    private static ActivityListener Listen(List<Activity> spans)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Telemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = spans.Add,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
