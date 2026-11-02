using ChatNET.API.Common.Auth;
using ChatNET.API.Common.OpenApi;
using ChatNET.API.RateLimiting;
using ChatNET.API.Search.Models;
using ChatNET.API.Search.SearchMessages;
using MediatR;

namespace ChatNET.API.Search;

public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var search = app.MapGroup("/api/search")
            .WithTags("Search")
            .RequireAuthorization();

        // Newest first, 20 at a time. `before` is the id of the oldest result already held, and the next page starts after it.
        search.MapGet("/messages", async (
            string? q, Guid? conversationId, Guid? before, HttpContext http, IMediator mediator) =>
        {
            var hits = await mediator.Send(new SearchMessagesQuery(http.User.GetUserId(), q, conversationId, before));
            return Results.Ok(hits);
        })
        .RequireRateLimiting(RateLimitPolicies.Search)
        .Returns<List<SearchHitDto>>(
            StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized,
            StatusCodes.Status403Forbidden, StatusCodes.Status429TooManyRequests);

        return app;
    }
}
