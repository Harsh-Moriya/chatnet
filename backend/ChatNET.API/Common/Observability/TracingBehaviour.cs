using System.Diagnostics;
using ChatNET.API.Common.Exceptions;
using FluentValidation;
using MediatR;

namespace ChatNET.API.Common.Observability;

// Wraps every command and query in a span named after the request type. It is registered before the
// validation behaviour so the span also covers validation, and a request that fails validation shows
// up in the trace.
//
// Only the request type is recorded. The request itself is never put on the span, because it can hold
// message content. The span is marked as an error only for failures nobody expected: a refused
// request (not found, forbidden, invalid input) is the application working, and would otherwise turn
// every trace of a normal 403 red. The exception message is not recorded either, since a database
// error message can quote the values that were rejected.
public class TracingBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        using var activity = Telemetry.Source.StartActivity(typeof(TRequest).Name);
        activity?.SetTag("mediatr.request", typeof(TRequest).FullName);

        try
        {
            return await next();
        }
        catch (Exception exception) when (activity is not null)
        {
            if (IsExpected(exception))
            {
                activity.SetTag("chatnet.rejected", exception.GetType().Name);
            }
            else
            {
                activity.SetStatus(ActivityStatusCode.Error);
                activity.SetTag("error.type", exception.GetType().FullName);
            }

            throw;
        }
    }

    private static bool IsExpected(Exception exception) => exception is ChatNETException or ValidationException;
}
