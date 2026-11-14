using ChatNET.API.Auth.Login;
using ChatNET.API.Auth.Logout;
using ChatNET.API.Auth.Models;
using ChatNET.API.Auth.Refresh;
using ChatNET.API.Auth.Register;
using ChatNET.API.Common.OpenApi;
using ChatNET.API.RateLimiting;
using MediatR;

namespace ChatNET.API.Auth;

internal static class NativeAuthEndpoints
{
    public static void Map(RouteGroupBuilder native)
    {
        native.AllowAnonymous();
        var credentials = native.MapGroup(string.Empty).RequireRateLimiting(RateLimitPolicies.Auth);
        var session = native.MapGroup(string.Empty).RequireRateLimiting(RateLimitPolicies.Refresh);

        credentials.MapPost("/register", async (RegisterCommand command, HttpContext http, IMediator mediator) =>
            Reply(http.Response, await mediator.Send(command, http.RequestAborted)))
        .Returns<NativeAuthResponse>(StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict, StatusCodes.Status429TooManyRequests);

        credentials.MapPost("/login", async (LoginCommand command, HttpContext http, IMediator mediator) =>
            Reply(http.Response, await mediator.Send(command with { DeviceId = DeviceHeader.Read(http.Request) }, http.RequestAborted)))
        .Returns<NativeAuthResponse>(StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status429TooManyRequests);

        session.MapPost("/refresh", async (NativeSessionRequest body, HttpContext http, IMediator mediator) =>
        {
            try
            {
                var command = new RefreshCommand(body.RefreshToken, DeviceHeader.Read(http.Request));
                return Reply(http.Response, await mediator.Send(command, http.RequestAborted));
            }
            catch (SessionRejectedException exception) when (exception.ClearCookie)
            {
                // A native refresh must leave any browser cookie on the same origin intact.
                throw new SessionRejectedException(clearCookie: false);
            }
        })
        .Returns<NativeAuthResponse>(StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status409Conflict, StatusCodes.Status429TooManyRequests);

        session.MapPost("/logout", async (NativeSessionRequest body, IMediator mediator, CancellationToken cancellationToken) =>
        {
            await mediator.Send(new LogoutCommand(body.RefreshToken), cancellationToken);
            return Results.NoContent();
        })
        .ReturnsNoContent(StatusCodes.Status400BadRequest, StatusCodes.Status429TooManyRequests);
    }

    private static IResult Reply(HttpResponse response, AuthResult result)
    {
        response.Headers.CacheControl = "no-store";
        var auth = result.Response;
        return Results.Ok(new NativeAuthResponse(
            auth.Token, auth.UserId, auth.UserName, auth.DisplayName, auth.AccessTokenExpiresAt,
            result.RefreshToken.Value, result.RefreshToken.ExpiresAt, result.DeviceId));
    }
}

public record NativeSessionRequest(string? RefreshToken);

public record NativeAuthResponse(
    string Token,
    string UserId,
    string UserName,
    string DisplayName,
    DateTime AccessTokenExpiresAt,
    string RefreshToken,
    DateTime RefreshTokenExpiresAt,
    Guid? DeviceId
);
