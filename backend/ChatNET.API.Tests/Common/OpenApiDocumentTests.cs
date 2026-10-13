using System.Net;
using System.Text.Json;
using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Common;

// The OpenAPI document is what the Scalar page and any generated client are built from. These tests
// pin what it says about every operation (what comes back on success, which errors are possible)
// so it cannot drift from the real behaviour the other test classes establish.
[Collection(IntegrationTestCollection.Name)]
public class OpenApiDocumentTests
{
    private readonly ChatApiFactory _factory;

    public OpenApiDocumentTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // Method, path, success status, schema of the success body (array of it when the name ends in []),
    // and the error statuses the operation can return.
    private static readonly (string Method, string Path, string Status, string Schema, string Errors)[] All =
    [
        ("post", "/api/auth/register", "200", "AuthResponse", "400,409,429"),
        ("post", "/api/auth/login", "200", "AuthResponse", "400,401,403,429"),
        ("get", "/api/users", "200", "UserSummaryDto[]", "400,401"),
        ("get", "/api/conversations", "200", "ConversationDto[]", "401"),
        ("get", "/api/conversations/{conversationId}", "200", "ConversationDto", "401,403"),
        ("post", "/api/conversations/direct", "200", "ConversationDto", "400,401,404"),
        ("post", "/api/conversations/group", "201", "ConversationDto", "400,401,404,409"),
        ("post", "/api/conversations/{conversationId}/messages", "201", "MessageDto", "400,401,403"),
        ("get", "/api/conversations/{conversationId}/messages", "200", "MessageDto[]", "400,401,403"),
    ];

    public static IEnumerable<object[]> Successes => All.Select(o => new object[] { o.Method, o.Path, o.Status, o.Schema });

    public static IEnumerable<object[]> Failures => All.Select(o => new object[] { o.Method, o.Path, o.Status, o.Errors });

    [Theory]
    [MemberData(nameof(Successes))]
    public async Task The_success_response_names_its_body(string method, string path, string status, string schema)
    {
        var operation = await OperationAsync(method, path);

        var content = operation.GetProperty("responses").GetProperty(status).GetProperty("content");
        var body = content.GetProperty("application/json").GetProperty("schema");

        var isArray = schema.EndsWith("[]");
        var name = schema.TrimEnd('[', ']');
        var reference = isArray ? body.GetProperty("items").GetProperty("$ref") : body.GetProperty("$ref");
        if (isArray)
            Assert.Equal("array", body.GetProperty("type").GetString());
        Assert.EndsWith($"/{name}", reference.GetString());
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Every_error_the_operation_can_return_is_documented_as_problem_details(
        string method, string path, string status, string errors)
    {
        var operation = await OperationAsync(method, path);
        var responses = operation.GetProperty("responses");

        foreach (var code in errors.Split(','))
        {
            Assert.True(responses.TryGetProperty(code, out var response), $"{method} {path} does not document {code}.");
            Assert.True(response.GetProperty("content").TryGetProperty("application/problem+json", out _),
                $"{method} {path} documents {code} without a problem+json body.");
        }

        // Nothing else is promised: the documented statuses are the success one plus these.
        var documented = responses.EnumerateObject().Select(p => p.Name).Order().ToList();
        var expected = errors.Split(',').Append(status).Order().ToList();
        Assert.Equal(expected, documented);
    }

    [Fact]
    public async Task Validation_failures_describe_the_per_field_errors_map()
    {
        var operation = await OperationAsync("post", "/api/conversations/direct");

        var schema = operation.GetProperty("responses").GetProperty("400").GetProperty("content")
            .GetProperty("application/problem+json").GetProperty("schema");

        Assert.Contains("HttpValidationProblemDetails", schema.GetRawText());
    }

    [Fact]
    public async Task Enums_are_documented_as_the_strings_the_api_sends()
    {
        var document = await DocumentAsync();

        var type = document.GetProperty("components").GetProperty("schemas").GetProperty("ConversationType");

        Assert.Equal("string", type.GetProperty("type").GetString());
        Assert.Equivalent(new[] { "Direct", "Group" }, type.GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Every_authenticated_operation_documents_how_a_token_is_sent()
    {
        var document = await DocumentAsync();

        foreach (var entry in document.GetProperty("paths").EnumerateObject())
        {
            var (path, item) = (entry.Name, entry.Value);
            if (!path.StartsWith("/api/") || path.StartsWith("/api/auth/"))
                continue;

            foreach (var operation in item.EnumerateObject())
                Assert.True(operation.Value.GetProperty("responses").TryGetProperty("401", out _),
                    $"{operation.Name} {path} requires a token but does not document 401.");
        }
    }

    [Fact]
    public async Task The_health_endpoint_is_documented()
    {
        var operation = await OperationAsync("get", "/health");

        var responses = operation.GetProperty("responses");
        Assert.True(responses.TryGetProperty("200", out _));
        Assert.True(responses.TryGetProperty("503", out _), "A failing dependency answers 503, which a monitor needs to know.");
    }

    private async Task<JsonElement> OperationAsync(string method, string path)
    {
        var document = await DocumentAsync();
        return document.GetProperty("paths").GetProperty(path).GetProperty(method);
    }

    private async Task<JsonElement> DocumentAsync()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
