using ChatNET.API.Common.Exceptions;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ChatNET.API.Common.Middleware;

// IExceptionHandler is the .NET 8+ way to centralise exception mapping.
// UseExceptionHandler() middleware calls TryHandleAsync on every unhandled exception.
// Returning true means "I handled it"; returning false passes it to the next handler.
//
// Every API error leaves with the same RFC 7807 ProblemDetails shape so the frontend
// never has to guess what a failure response looks like.
public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            NotFoundException      => (StatusCodes.Status404NotFound,          "Not Found"),
            UnauthorizedException  => (StatusCodes.Status401Unauthorized,      "Unauthorized"),
            ForbiddenException     => (StatusCodes.Status403Forbidden,         "Forbidden"),
            ConflictException      => (StatusCodes.Status409Conflict,          "Conflict"),
            ValidationException    => (StatusCodes.Status400BadRequest,        "Validation Failed"),
            _                      => (StatusCodes.Status500InternalServerError, "An unexpected error occurred")
        };

        // Log unexpected errors with full detail; expected domain errors at a lower level.
        if (status == StatusCodes.Status500InternalServerError)
            _logger.LogError(exception, "Unhandled exception for {Method} {Path}",
                httpContext.Request.Method, httpContext.Request.Path);
        else
            _logger.LogWarning("Domain exception {Type}: {Message}",
                exception.GetType().Name, exception.Message);

        httpContext.Response.StatusCode = status;

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = status == StatusCodes.Status500InternalServerError
                ? "An unexpected error occurred. Please try again later."
                : exception.Message
        };

        // Validation errors include per-field detail so the frontend can highlight fields.
        if (exception is ValidationException ve)
        {
            problem.Extensions["errors"] = ve.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(e => e.ErrorMessage).ToArray());
        }

        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}
