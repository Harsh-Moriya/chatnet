using FluentValidation;
using MediatR;

namespace ChatNET.API.Common.Behaviours;

// A MediatR pipeline behaviour runs in the same way ASP.NET Core middleware runs:
// each behaviour wraps the next step and can short-circuit before it. This one
// intercepts every command/query, runs all registered validators for it, and throws
// if any fail — so no handler ever receives invalid input.
//
// MediatR calls behaviours in registration order. This one should be registered first
// so validation always runs before any other cross-cutting concern (e.g. logging).
public class ValidationBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehaviour(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!_validators.Any())
            return await next();

        var context = new ValidationContext<TRequest>(request);

        // Run all validators concurrently rather than sequentially.
        var results = await Task.WhenAll(
            _validators.Select(v => v.ValidateAsync(context, cancellationToken)));

        var failures = results
            .SelectMany(r => r.Errors)
            .Where(f => f is not null)
            .ToList();

        if (failures.Count > 0)
            throw new ValidationException(failures);

        return await next();
    }
}
