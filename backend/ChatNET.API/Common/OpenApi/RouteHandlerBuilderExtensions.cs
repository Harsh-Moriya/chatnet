namespace ChatNET.API.Common.OpenApi;

public static class RouteHandlerBuilderExtensions
{
    // Documents what an endpoint returns: the body on success, and the errors it can produce. Every
    // error is an RFC 7807 ProblemDetails (application/problem+json); a 400 is documented with the
    // per-field "errors" map that validation failures carry. The OpenAPI document is generated from
    // this, and OpenApiDocumentTests checks it against the real behaviour.
    public static RouteHandlerBuilder Returns<TBody>(
        this RouteHandlerBuilder builder, int successStatus, params int[] errorStatuses)
    {
        builder.Produces<TBody>(successStatus);

        foreach (var status in errorStatuses)
        {
            if (status == StatusCodes.Status400BadRequest)
                builder.ProducesValidationProblem();
            else
                builder.ProducesProblem(status);
        }

        return builder;
    }
}
