using ChatNET.API.Common.Auth;
using ChatNET.API.Common.OpenApi;
using ChatNET.API.Push.GetPushKey;
using ChatNET.API.Push.RemovePushSubscription;
using ChatNET.API.Push.SavePushSubscription;
using ChatNET.API.RateLimiting;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace ChatNET.API.Push;

// The shape of PushSubscription.toJSON() in the browser, so the page sends what it has.
public record PushKeysRequest(string? P256dh, string? Auth);

public record SavePushSubscriptionRequest(string? Endpoint, PushKeysRequest? Keys);

public record RemovePushSubscriptionRequest(string? Endpoint);

public static class PushEndpoints
{
    public static IEndpointRouteBuilder MapPushEndpoints(this IEndpointRouteBuilder app)
    {
        var push = app.MapGroup("/api/push")
            .WithTags("Push")
            .RequireAuthorization();

        // The key a browser subscribes with. 404 when this server has no keys (push is off), which is how the page knows to
        // leave the switch out.
        push.MapGet("/key", async (IMediator mediator) =>
            Results.Ok(await mediator.Send(new GetPushKeyQuery())))
        .Returns<PushKeyDto>(StatusCodes.Status200OK, StatusCodes.Status401Unauthorized, StatusCodes.Status404NotFound);

        // The endpoint is in the body, never in the address: it is a capability, and addresses end up in logs and traces.
        // 204 whether the browser was new or already registered (the page registers every time it opens).
        push.MapPut("/subscriptions", async (SavePushSubscriptionRequest body, HttpContext http, IMediator mediator) =>
        {
            await mediator.Send(new SavePushSubscriptionCommand(
                http.User.GetUserId(), body.Endpoint, body.Keys?.P256dh, body.Keys?.Auth));
            return Results.NoContent();
        })
        .RequireRateLimiting(RateLimitPolicies.Push)
        .ReturnsNoContent(
            StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status404NotFound,
            StatusCodes.Status429TooManyRequests);

        // 204 whether or not the browser was registered, and only the caller's own is ever removed.
        push.MapDelete("/subscriptions", async ([FromBody] RemovePushSubscriptionRequest body, HttpContext http, IMediator mediator) =>
        {
            await mediator.Send(new RemovePushSubscriptionCommand(http.User.GetUserId(), body.Endpoint));
            return Results.NoContent();
        })
        .RequireRateLimiting(RateLimitPolicies.Push)
        .ReturnsNoContent(
            StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status429TooManyRequests);

        return app;
    }
}
