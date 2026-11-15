using ChatNET.API.Common.Auth;
using ChatNET.API.Common.OpenApi;
using ChatNET.API.Media.CreateUpload;
using ChatNET.API.Media.GetAttachmentUrl;
using ChatNET.API.Messaging.GetMessageFiles;
using ChatNET.API.Messaging.Models;
using ChatNET.API.RateLimiting;
using MediatR;

namespace ChatNET.API.Media;

public record CreateUploadRequest(long SizeBytes);

public static class MediaEndpoints
{
    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        // Permission to upload one file to a conversation. The file, already encrypted by the browser, goes straight to the
        // bucket, to the address in the answer, and is attached by naming its key when the message is sent. Nothing is stored
        // by this call, so it answers 200 and not 201.
        app.MapPost("/api/conversations/{conversationId:guid}/uploads", async (
            Guid conversationId, CreateUploadRequest body, HttpContext http, IMediator mediator) =>
        {
            var ticket = await mediator.Send(new CreateUploadCommand(
                conversationId, http.User.GetUserId(), body.SizeBytes));

            return Results.Ok(ticket);
        })
        .WithTags("Media")
        .RequireAuthorization()
        .RequireRateLimiting(RateLimitPolicies.Uploads)
        .Returns<UploadTicketDto>(
            StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized,
            StatusCodes.Status403Forbidden, StatusCodes.Status429TooManyRequests);

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

        // The messages of a conversation that have files, for the gallery: newest first, at most 50 (a shorter page is the end), inside
        // the part of the history the caller may read. "before" is the id of the oldest message the caller already has from this
        // list, and returns the page just older. Unlike the history, the page is not turned around: the newest comes first.
        app.MapGet("/api/conversations/{conversationId:guid}/attachments", async (
            Guid conversationId, Guid? before, HttpContext http, IMediator mediator) =>
        {
            var page = await mediator.Send(new GetMessageFilesQuery(conversationId, http.User.GetUserId(), before));
            return Results.Ok(page);
        })
        .WithTags("Media")
        .RequireAuthorization()
        .Returns<List<MessageDto>>(
            StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized,
            StatusCodes.Status403Forbidden);

        return app;
    }
}
