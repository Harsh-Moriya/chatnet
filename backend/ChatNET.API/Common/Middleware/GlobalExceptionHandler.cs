using ChatNET.API.Auth;
using ChatNET.API.Auth.Refresh;
using ChatNET.API.Common.Exceptions;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

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
    private readonly IProblemDetailsService _problemDetails;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IProblemDetailsService problemDetails)
    {
        _logger = logger;
        _problemDetails = problemDetails;
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
            TooManyAttemptsException => (StatusCodes.Status429TooManyRequests, "Too Many Requests"),
            ValidationException    => (StatusCodes.Status400BadRequest,        "Validation Failed"),

            // Thrown by the framework when the request cannot be bound: bad JSON, an empty
            // body, a value that is not a valid GUID, a missing required query parameter.
            // It carries its own status code (400, or 413 for an oversized body).
            BadHttpRequestException bad => (bad.StatusCode, ReasonPhrases.GetReasonPhrase(bad.StatusCode)),

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

        if (exception is TooManyAttemptsException tooMany)
            httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(tooMany.RetryAfter.TotalSeconds)).ToString();

        // Set here and not in the endpoint: the exception handler middleware clears the response before it
        // calls this, so a header set earlier would be lost.
        if (exception is SessionRejectedException { ClearCookie: true })
            RefreshCookie.Clear(httpContext.Response);

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = exception switch
            {
                _ when status == StatusCodes.Status500InternalServerError
                    => "An unexpected error occurred. Please try again later.",

                // The framework's own message names the internal parameter and type that
                // failed to bind, which a client has no use for; it is logged above instead.
                BadHttpRequestException
                    => "The request could not be read. Check the URL, query string and JSON body.",

                // ValidationException.Message is a multi-line dump ("Validation failed: --
                // Field: message Severity: Error"). The detail is shown to users as is, so it
                // is rebuilt as plain sentences; the per-field breakdown stays in "errors".
                ValidationException validation
                    => string.Join(" ", validation.Errors.Select(e => e.ErrorMessage).Distinct()),

                _ => exception.Message
            }
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

        // The framework's problem details writer is also what produces the body for bare status
        // codes (see UseStatusCodePages), so every error response is written the same way:
        // application/problem+json, with a type URI and a traceId added.
        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
