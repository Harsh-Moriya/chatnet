using ChatNET.API.Common.Auth;
using ChatNET.API.Users.SearchUsers;
using MediatR;

namespace ChatNET.API.Users;

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
        });

        return app;
    }
}
