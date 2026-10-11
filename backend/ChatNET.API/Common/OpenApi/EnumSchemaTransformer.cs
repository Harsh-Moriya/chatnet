using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ChatNET.API.Common.OpenApi;

public static class EnumSchemaTransformer
{
    // Enums are sent as their names (see the JSON settings in Program.cs). The generated schema lists
    // those names but leaves out the type, so a client generator cannot tell they are strings. This
    // states it.
    public static Task Transform(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type.IsEnum)
            schema.Type = JsonSchemaType.String;

        return Task.CompletedTask;
    }
}
