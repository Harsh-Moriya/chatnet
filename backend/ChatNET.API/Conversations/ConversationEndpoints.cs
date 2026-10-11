using ChatNET.API.Common.Auth;
using ChatNET.API.Conversations.CreateDirect;
using ChatNET.API.Conversations.CreateGroup;
using ChatNET.API.Conversations.GetMyConversations;
using MediatR;

namespace ChatNET.API.Conversations;

public record CreateDirectRequest(Guid OtherUserId);

public record CreateGroupRequest(string Name, List<Guid> MemberIds);

public static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        var conversations = app.MapGroup("/api/conversations")
            .WithTags("Conversations")
            .RequireAuthorization();

        conversations.MapGet("/", async (HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(new GetMyConversationsQuery(http.User.GetUserId()));
            return Results.Ok(result);
        });

        conversations.MapPost("/direct", async (CreateDirectRequest body, HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(new CreateDirectCommand(http.User.GetUserId(), body.OtherUserId));
            return Results.Ok(result);
        });

        conversations.MapPost("/group", async (CreateGroupRequest body, HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(
                new CreateGroupCommand(http.User.GetUserId(), body.Name, body.MemberIds ?? []));
            return Results.Created($"/api/conversations/{result.Id}", result);
        });

        return app;
    }
}
