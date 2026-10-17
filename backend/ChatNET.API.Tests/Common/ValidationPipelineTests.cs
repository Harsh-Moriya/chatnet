using ChatNET.API.Common.Behaviours;
using ChatNET.API.Tests.Infrastructure;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Common;

// A validator only protects anything if the pipeline behaviour that runs it is attached to its
// request. MediatR attaches an open generic behaviour only to requests whose type satisfies the
// behaviour's constraint (IRequest<TResponse>), and a request declared as the plain IRequest does
// not: it compiled, had a validator, and was silently never validated. This test looks at every
// request type in the application, so adding a validator for one that cannot be reached fails here
// instead of being discovered by accident.
[Collection(IntegrationTestCollection.Name)]
public class ValidationPipelineTests
{
    private readonly ChatApiFactory _factory;

    public ValidationPipelineTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Every_request_that_has_a_validator_runs_through_the_validation_behaviour()
    {
        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var withValidator = new List<Type>();
        var unvalidated = new List<string>();

        foreach (var (request, response) in RequestTypes())
        {
            var validators = (System.Collections.IEnumerable)services.GetRequiredService(
                typeof(IEnumerable<>).MakeGenericType(typeof(IValidator<>).MakeGenericType(request)));
            if (!validators.Cast<object>().Any())
                continue;

            withValidator.Add(request);

            var behaviours = (System.Collections.IEnumerable)services.GetRequiredService(
                typeof(IEnumerable<>).MakeGenericType(typeof(IPipelineBehavior<,>).MakeGenericType(request, response)));
            var validated = behaviours.Cast<object>()
                .Any(b => b.GetType().IsGenericType && b.GetType().GetGenericTypeDefinition() == typeof(ValidationBehaviour<,>));

            if (!validated)
                unvalidated.Add(request.Name);
        }

        // A check that looked at nothing would pass for the wrong reason.
        Assert.True(withValidator.Count >= 8, $"Only {withValidator.Count} requests with validators were found.");
        Assert.True(unvalidated.Count == 0,
            $"These requests have a validator that never runs: {string.Join(", ", unvalidated)}.");
    }

    // Every MediatR request in the application with the response type its handler and behaviours use.
    // A request with no result is IRequest, which MediatR handles as one that returns Unit.
    private static IEnumerable<(Type Request, Type Response)> RequestTypes()
    {
        foreach (var type in typeof(Program).Assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            var generic = type.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>));
            if (generic is not null)
                yield return (type, generic.GetGenericArguments()[0]);
            else if (typeof(IRequest).IsAssignableFrom(type))
                yield return (type, typeof(Unit));
        }
    }
}
