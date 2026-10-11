using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Auth;

[Collection(IntegrationTestCollection.Name)]
public class RegistrationRulesTests
{
    private readonly ChatApiFactory _factory;

    public RegistrationRulesTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // ##### Password rules #####

    // A password that breaks one of the account rules is a mistake in the request, not a clash with
    // existing data, so it is a 400 with the field named (like a too-short one), not a 409.
    [Theory]
    [InlineData("alllowercase1", "uppercase")]
    [InlineData("ALLUPPERCASE1", "lowercase")]
    [InlineData("NoDigitsHere", "digit")]
    public async Task A_password_that_breaks_an_account_rule_is_a_400_on_the_password_field(string password, string rule)
    {
        var response = await RegisterAsync(Unique("pw"), $"{Unique("pw")}@example.test", password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal("Validation Failed", problem.Title);
        var messages = problem.Errors!.Value.GetProperty("Password").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains(messages, m => m.Contains(rule, StringComparison.OrdinalIgnoreCase));
    }

    // ##### Taken names #####

    [Fact]
    public async Task A_taken_user_name_is_a_409()
    {
        var existing = await _factory.RegisterUserAsync("taken");

        var response = await RegisterAsync(existing.UserName, $"{Unique("o")}@example.test", "Aa1xxxxxxxx");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_taken_email_is_a_409_whatever_its_capitalisation()
    {
        var existing = await _factory.RegisterUserAsync("taken");

        var response = await RegisterAsync(Unique("o"), existing.Email.ToUpperInvariant(), "Aa1xxxxxxxx");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ##### Tidying and limits #####

    [Fact]
    public async Task The_display_name_is_stored_without_surrounding_spaces()
    {
        var response = await RegisterAsync(
            Unique("trim"), $"{Unique("trim")}@example.test", "Aa1xxxxxxxx", displayName: "  Ada Lovelace \t");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Ada Lovelace", body.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task A_display_name_of_only_spaces_is_rejected()
    {
        var response = await RegisterAsync(
            Unique("blank"), $"{Unique("blank")}@example.test", "Aa1xxxxxxxx", displayName: "    ");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Checking a password costs time in proportion to the work done, so an unbounded one would let a
    // single request burn server time. The email limit is the one in RFC 5321.
    [Theory]
    [InlineData("Email", 255)]
    [InlineData("Password", 129)]
    public async Task Fields_have_an_upper_length(string field, int length)
    {
        var email = field == "Email" ? new string('a', length - 13) + "@example.test" : $"{Unique("long")}@example.test";
        var password = field == "Password" ? "Aa1" + new string('x', length - 3) : "Aa1xxxxxxxx";

        var response = await RegisterAsync(Unique("long"), email, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ReadProblemAsync()).Errors!.Value.TryGetProperty(field, out _));
    }

    [Fact]
    public async Task The_longest_allowed_values_are_accepted()
    {
        var email = new string('a', 254 - 13) + "@example.test";
        var password = "Aa1" + new string('x', 125);

        var response = await RegisterAsync(Unique("max"), email, password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<HttpResponseMessage> RegisterAsync(
        string userName, string email, string password, string displayName = "Someone")
    {
        using var client = _factory.CreateClient();
        return await client.PostAsJsonAsync("/api/auth/register", new { userName, displayName, email, password });
    }

    private static string Unique(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..20];
}
