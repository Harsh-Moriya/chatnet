using ChatNET.API.Auth.Login;
using ChatNET.API.Auth.Logout;
using ChatNET.API.Auth.Models;
using ChatNET.API.Common.OpenApi;
using ChatNET.API.Auth.RateLimiting;
using ChatNET.API.Auth.Refresh;
using ChatNET.API.Auth.Register;
using MediatR;

namespace ChatNET.API.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");

        // Each kind of endpoint has its own rate limit policy, applied to its whole group, so every route
        // added to a group is limited per client address without having to remember to say so.
        // Signing in and registering are limited tightly (they take a password to guess); renewing and
        // ending a session take a token that cannot be guessed, so their limit is looser and separate.
        var credentials = auth.MapGroup(string.Empty).RequireRateLimiting(AuthRateLimitOptions.PolicyName);
        var session = auth.MapGroup(string.Empty).RequireRateLimiting(RefreshRateLimitOptions.PolicyName);

        credentials.MapPost("/register", async (RegisterCommand command, HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(command);
            RefreshCookie.Write(http.Response, result.RefreshToken);
            return Results.Ok(result.Response);
        })
        .Returns<AuthResponse>(StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict, StatusCodes.Status429TooManyRequests)
        .AllowAnonymous();

        credentials.MapPost("/login", async (LoginCommand command, HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(command);
            RefreshCookie.Write(http.Response, result.RefreshToken);
            return Results.Ok(result.Response);
        })
        .Returns<AuthResponse>(StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status429TooManyRequests)
        .AllowAnonymous();

        // The credential here is the refresh cookie, not a bearer token, so the endpoint is anonymous as
        // far as the JWT middleware is concerned.
        session.MapPost("/refresh", async (HttpContext http, IMediator mediator) =>
        {
            RefreshCookie.RequireClientHeader(http.Request);

            // A refusal throws SessionRejectedException. When the cookie is no use any more the exception
            // handler also removes it, so the browser stops sending it.
            var result = await mediator.Send(new RefreshCommand(RefreshCookie.Read(http.Request)));
            RefreshCookie.Write(http.Response, result.RefreshToken);
            return Results.Ok(result.Response);
        })
        .Returns<AuthResponse>(StatusCodes.Status200OK, StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status409Conflict, StatusCodes.Status429TooManyRequests)
        .AllowAnonymous();

        session.MapPost("/logout", async (HttpContext http, IMediator mediator) =>
        {
            RefreshCookie.RequireClientHeader(http.Request);

            await mediator.Send(new LogoutCommand(RefreshCookie.Read(http.Request)));
            RefreshCookie.Clear(http.Response);

            return Results.NoContent();
        })
        .ReturnsNoContent(StatusCodes.Status403Forbidden, StatusCodes.Status429TooManyRequests)
        .AllowAnonymous();

        return app;
    }
}
