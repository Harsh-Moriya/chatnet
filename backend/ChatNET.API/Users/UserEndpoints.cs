using ChatNET.API.Common.Auth;
using ChatNET.API.Common.OpenApi;
using ChatNET.API.Users.GetMe;
using ChatNET.API.Users.SearchUsers;
using ChatNET.API.Users.UpdateProfile;
using MediatR;

namespace ChatNET.API.Users;

public record UpdateProfileRequest(string? DisplayName);

public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/api/users")
            .WithTags("Users")
            .RequireAuthorization();

        users.MapGet("/", async (string query, HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(new SearchUsersQuery(http.User.GetUserId(), query));
            return Results.Ok(result);
        })
        .Returns<List<UserSummaryDto>>(StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized);

        // The caller's own profile. The picture is an address that works without a token (see AvatarEndpoints).
        users.MapGet("/me", async (HttpContext http, IMediator mediator) =>
            Results.Ok(await mediator.Send(new GetMeQuery(http.User.GetUserId()))))
        .Returns<UserSummaryDto>(
            StatusCodes.Status200OK, StatusCodes.Status401Unauthorized, StatusCodes.Status404NotFound);

        users.MapPatch("/me", async (UpdateProfileRequest body, HttpContext http, IMediator mediator) =>
            Results.Ok(await mediator.Send(new UpdateProfileCommand(http.User.GetUserId(), body.DisplayName))))
        .Returns<UserSummaryDto>(
            StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized,
            StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound);

        return app;
    }
}
