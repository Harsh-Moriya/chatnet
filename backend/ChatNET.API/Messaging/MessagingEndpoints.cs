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
        //
        // 201 for a message this request created; 200 with the stored message when the same send (same
        // client message id from the same sender) had already been stored, which is what makes a retry safe.
        messages.MapPost("/", async (
            Guid conversationId, SendMessageRequest body, HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(new SendMessageCommand(
                conversationId, http.User.GetUserId(), body.Content, body.ClientMessageId));

            return Results.Json(
                result.Message,
                statusCode: result.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        })
        .Returns<MessageDto>(
            StatusCodes.Status201Created, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized,
            StatusCodes.Status403Forbidden)
        .Produces<MessageDto>(StatusCodes.Status200OK);

        // Returns at most one page (50), oldest first. Without a cursor it is the latest page;
        // "before" is the id of the oldest message the caller has, and returns the page just older;
        // "after" is the id of the newest message the caller has, and returns the page just newer.
        messages.MapGet("/", async (
            Guid conversationId, Guid? before, Guid? after, HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(
                new GetMessageHistoryQuery(conversationId, http.User.GetUserId(), before, after));
            return Results.Ok(result);
        })
        .Returns<List<MessageDto>>(
            StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized,
            StatusCodes.Status403Forbidden);

        return app;
    }
}
