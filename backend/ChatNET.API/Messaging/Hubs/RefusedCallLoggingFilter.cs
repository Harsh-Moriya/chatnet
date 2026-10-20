using ChatNET.API.Common.Auth;
using Microsoft.AspNetCore.SignalR;

namespace ChatNET.API.Messaging.Hubs;

// A hub filter wraps every hub method call, the way middleware wraps a request. This one only
// watches for HubException, which a hub throws on purpose to refuse a call. A refusal is a normal
// outcome, so it is logged at Warning with the method and the user; the arguments are not logged
// because they can hold anything the client sent. Other exceptions pass through untouched and are
// still logged by SignalR as errors.
public class RefusedCallLoggingFilter : IHubFilter
{
    private readonly ILogger<RefusedCallLoggingFilter> _logger;

    public RefusedCallLoggingFilter(ILogger<RefusedCallLoggingFilter> logger)
    {
        _logger = logger;
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try
        {
            return await next(invocationContext);
        }
        catch (HubException)
        {
            invocationContext.Context.User.TryGetUserId(out var userId);

            _logger.LogWarning(
                "Hub method {HubMethod} was refused for user {UserId} on connection {ConnectionId}",
                invocationContext.HubMethodName, userId, invocationContext.Context.ConnectionId);

            throw;
        }
    }
}
