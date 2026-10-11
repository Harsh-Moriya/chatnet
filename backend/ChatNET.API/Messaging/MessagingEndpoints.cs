using ChatNET.API.Common.Auth;
using ChatNET.API.Common.OpenApi;
using ChatNET.API.Messaging.GetMessageHistory;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Messaging.SendMessage;
using MediatR;

namespace ChatNET.API.Messaging;

public record SendMessageRequest(string? Content, Guid? ClientMessageId);

public static class MessagingEndpoints
{
    public static IEndpointRouteBuilder MapMessagingEndpoints(this IEndpointRouteBuilder app)
    {
        var messages = app.MapGroup("/api/conversations/{conversationId:guid}/messages")
            .WithTags("Messages")
            .RequireAuthorization();

        // No Location header: nothing serves a single message by ID, so the header would point
        // at a route that answers 404. The body carries the new message's ID.
        messages.MapPost("/", async (
            Guid conversationId, SendMessageRequest body, HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(new SendMessageCommand(
                conversationId, http.User.GetUserId(), body.Content, body.ClientMessageId));

            return Results.Json(result, statusCode: StatusCodes.Status201Created);
        })
        .Returns<MessageDto>(
            StatusCodes.Status201Created, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized,
            StatusCodes.Status403Forbidden);

        messages.MapGet("/", async (Guid conversationId, HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(new GetMessageHistoryQuery(conversationId, http.User.GetUserId()));
            return Results.Ok(result);
        })
        .Returns<List<MessageDto>>(StatusCodes.Status200OK, StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden);

        return app;
    }
}
