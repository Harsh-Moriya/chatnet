using ChatNET.API.Auth.Login;
using ChatNET.API.Auth.RateLimiting;
using ChatNET.API.Auth.Register;
using MediatR;

namespace ChatNET.API.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // The rate limit policy is applied to the whole group, so every route added here is limited
        // per client address without having to remember to say so.
        var auth = app.MapGroup("/api/auth")
            .WithTags("Auth")
            .RequireRateLimiting(AuthRateLimitOptions.PolicyName);

        auth.MapPost("/register", async (RegisterCommand command, IMediator mediator) =>
        {
            var result = await mediator.Send(command);
            return Results.Ok(result);
        })
        .AllowAnonymous();

        auth.MapPost("/login", async (LoginCommand command, IMediator mediator) =>
        {
            var result = await mediator.Send(command);
            return Results.Ok(result);
        })
        .AllowAnonymous();

        return app;
    }
}
