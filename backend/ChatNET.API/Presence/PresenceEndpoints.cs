using ChatNET.API.Common.Auth;
using ChatNET.API.Common.OpenApi;
using ChatNET.API.Presence.GetPresence;
using MediatR;

namespace ChatNET.API.Presence;

public static class PresenceEndpoints
{
    public static IEndpointRouteBuilder MapPresenceEndpoints(this IEndpointRouteBuilder app)
    {
        var presence = app.MapGroup("/api/presence")
            .WithTags("Presence")
            .RequireAuthorization();

        // The presence of everyone the caller shares an active conversation with. Loaded when a client
        // starts and after it reconnects; live changes arrive as PresenceChanged pushes.
        presence.MapGet("/", async (HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(new GetPresenceQuery(http.User.GetUserId()));
            return Results.Ok(result);
        })
        .Returns<List<PresenceDto>>(StatusCodes.Status200OK, StatusCodes.Status401Unauthorized);

        return app;
    }
}
