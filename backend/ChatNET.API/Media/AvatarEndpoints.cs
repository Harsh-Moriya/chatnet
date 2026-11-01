using ChatNET.API.Common.Auth;
using ChatNET.API.Common.Exceptions;
using ChatNET.API.Common.OpenApi;
using ChatNET.API.Media.Avatars;
using ChatNET.API.Media.CreateUpload;
using MediatR;

namespace ChatNET.API.Media;

public record AvatarUploadRequest(string? ContentType, long SizeBytes);

public record SetAvatarRequest(string? UploadKey);

public static class AvatarEndpoints
{
    public static IEndpointRouteBuilder MapAvatarEndpoints(this IEndpointRouteBuilder app)
    {
        // ##### The caller's own picture #####

        var me = app.MapGroup("/api/users/me/avatar")
            .WithTags("Users")
            .RequireAuthorization();

        // Permission to upload one picture; nothing is stored by this call, so it answers 200 and not 201.
        me.MapPost("/uploads", async (AvatarUploadRequest body, HttpContext http, IMediator mediator) =>
        {
            var userId = http.User.GetUserId();
            var ticket = await mediator.Send(new CreateAvatarUploadCommand(
                AvatarOwner.User, userId, userId, body.ContentType, body.SizeBytes));

            return Results.Ok(ticket);
        })
        .Returns<UploadTicketDto>(
            StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized,
            StatusCodes.Status403Forbidden);

        me.MapPut("/", async (SetAvatarRequest body, HttpContext http, IMediator mediator) =>
        {
            var userId = http.User.GetUserId();
            return Results.Ok(await mediator.Send(new SetAvatarCommand(AvatarOwner.User, userId, userId, body.UploadKey)));
        })
        .Returns<AvatarDto>(
            StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized,
            StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound);

        me.MapDelete("/", async (HttpContext http, IMediator mediator) =>
        {
            var userId = http.User.GetUserId();
            return Results.Ok(await mediator.Send(new RemoveAvatarCommand(AvatarOwner.User, userId, userId)));
        })
        .Returns<AvatarDto>(
            StatusCodes.Status200OK, StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden,
            StatusCodes.Status404NotFound);

        // ##### A group's picture (owner or admin) #####

        var group = app.MapGroup("/api/conversations/{conversationId:guid}/avatar")
            .WithTags("Conversations")
            .RequireAuthorization();

        group.MapPost("/uploads", async (
            Guid conversationId, AvatarUploadRequest body, HttpContext http, IMediator mediator) =>
        {
            var ticket = await mediator.Send(new CreateAvatarUploadCommand(
                AvatarOwner.Group, conversationId, http.User.GetUserId(), body.ContentType, body.SizeBytes));

            return Results.Ok(ticket);
        })
        .Returns<UploadTicketDto>(
            StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized,
            StatusCodes.Status403Forbidden);

        group.MapPut("/", async (Guid conversationId, SetAvatarRequest body, HttpContext http, IMediator mediator) =>
            Results.Ok(await mediator.Send(new SetAvatarCommand(
                AvatarOwner.Group, conversationId, http.User.GetUserId(), body.UploadKey))))
        .Returns<AvatarDto>(
            StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized,
            StatusCodes.Status403Forbidden, StatusCodes.Status409Conflict);

        group.MapDelete("/", async (Guid conversationId, HttpContext http, IMediator mediator) =>
            Results.Ok(await mediator.Send(new RemoveAvatarCommand(
                AvatarOwner.Group, conversationId, http.User.GetUserId()))))
        .Returns<AvatarDto>(
            StatusCodes.Status200OK, StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden,
            StatusCodes.Status409Conflict);

        // ##### Serving a picture #####
        // Open to anyone who has the address, because an <img> cannot send a token. The address is made from two random ids and
        // a random file name that is new for every picture, so it cannot be guessed or listed, and a changed picture is a
        // new address. That is also what makes it safe to tell browsers to keep the answer for a year: nothing that is
        // ever served under one address changes.
        app.MapGet("/api/avatars/users/{ownerId:guid}/{fileId:guid}",
            (Guid ownerId, Guid fileId, HttpContext http, IBlobStorage storage, CancellationToken cancellationToken)
                => ServeAsync(AvatarOwner.User, ownerId, fileId, http, storage, cancellationToken))
            .WithTags("Avatars")
            .Produces(StatusCodes.Status200OK, contentType: "image/png", additionalContentTypes: ["image/jpeg", "image/webp", "image/gif"])
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapGet("/api/avatars/groups/{ownerId:guid}/{fileId:guid}",
            (Guid ownerId, Guid fileId, HttpContext http, IBlobStorage storage, CancellationToken cancellationToken)
                => ServeAsync(AvatarOwner.Group, ownerId, fileId, http, storage, cancellationToken))
            .WithTags("Avatars")
            .Produces(StatusCodes.Status200OK, contentType: "image/png", additionalContentTypes: ["image/jpeg", "image/webp", "image/gif"])
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> ServeAsync(
        AvatarOwner owner, Guid ownerId, Guid fileId, HttpContext http, IBlobStorage storage, CancellationToken cancellationToken)
    {
        var blob = await storage.OpenReadAsync(AvatarKeys.StoredKey(owner, ownerId, fileId), cancellationToken)
            ?? throw new NotFoundException("Picture", fileId);

        // Closed when the response has been sent.
        http.Response.RegisterForDispose(blob);
        http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";

        return Results.Stream(blob.Content, blob.ContentType);
    }
}
