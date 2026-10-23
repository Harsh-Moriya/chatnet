using ChatNET.API.Common.Auth;
using ChatNET.API.Common.OpenApi;
using ChatNET.API.Notifications.DismissNotification;
using ChatNET.API.Notifications.GetNotifications;
using ChatNET.API.Notifications.MarkNotificationsRead;
using ChatNET.API.Notifications.Models;
using MediatR;

namespace ChatNET.API.Notifications;

public record MarkNotificationsReadRequest(List<Guid>? Ids);

public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var notifications = app.MapGroup("/api/notifications")
            .WithTags("Notifications")
            .RequireAuthorization();

        // The caller's notifications, newest first, read and unread together, 30 to a page. "before" is the id of
        // the oldest one the caller has, and returns the page just older than it.
        notifications.MapGet("/", async (Guid? before, HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(new GetNotificationsQuery(http.User.GetUserId(), before));
            return Results.Ok(result);
        })
        .Returns<NotificationListDto>(
            StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized);

        // The ids to mark read, or no ids (an empty body, "{}") for all of them. 204 whether or not anything was
        // unread: marking a notification that is already read is not an error.
        notifications.MapPost("/read", async (MarkNotificationsReadRequest body, HttpContext http, IMediator mediator) =>
        {
            await mediator.Send(new MarkNotificationsReadCommand(http.User.GetUserId(), body.Ids));
            return Results.NoContent();
        })
        .ReturnsNoContent(StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized);

        notifications.MapDelete("/{notificationId:guid}", async (Guid notificationId, HttpContext http, IMediator mediator) =>
        {
            await mediator.Send(new DismissNotificationCommand(http.User.GetUserId(), notificationId));
            return Results.NoContent();
        })
        .ReturnsNoContent(StatusCodes.Status401Unauthorized, StatusCodes.Status404NotFound);

        return app;
    }
}
