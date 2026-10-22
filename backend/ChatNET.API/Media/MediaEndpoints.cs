using ChatNET.API.Common.Auth;
using ChatNET.API.Common.OpenApi;
using ChatNET.API.Media.CreateUpload;
using MediatR;

namespace ChatNET.API.Media;

public record CreateUploadRequest(string? FileName, string? ContentType, long SizeBytes);

public static class MediaEndpoints
{
    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        // Permission to upload one file to a conversation. The file itself goes straight to the bucket, to the
        // address in the answer, and is attached by naming its key when the message is sent. Nothing is stored
        // by this call, so it answers 200 and not 201.
        app.MapPost("/api/conversations/{conversationId:guid}/uploads", async (
            Guid conversationId, CreateUploadRequest body, HttpContext http, IMediator mediator) =>
        {
            var ticket = await mediator.Send(new CreateUploadCommand(
                conversationId, http.User.GetUserId(), body.FileName, body.ContentType, body.SizeBytes));

            return Results.Ok(ticket);
        })
        .WithTags("Media")
        .RequireAuthorization()
        .Returns<UploadTicketDto>(
            StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized,
            StatusCodes.Status403Forbidden);

        return app;
    }
}
