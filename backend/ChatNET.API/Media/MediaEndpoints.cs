using ChatNET.API.Common.Auth;
using ChatNET.API.Common.OpenApi;
using ChatNET.API.Media.CreateUpload;
using ChatNET.API.Media.GetAttachmentUrl;
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

        // A new link to a file the caller can read, for when the link that came with the message has expired.
        app.MapGet("/api/attachments/{attachmentId:guid}/url", async (
            Guid attachmentId, HttpContext http, IMediator mediator) =>
        {
            var link = await mediator.Send(new GetAttachmentUrlQuery(attachmentId, http.User.GetUserId()));
            return Results.Ok(link);
        })
        .WithTags("Media")
        .RequireAuthorization()
        .Returns<AttachmentLinkDto>(
            StatusCodes.Status200OK, StatusCodes.Status401Unauthorized, StatusCodes.Status404NotFound);

        return app;
    }
}
