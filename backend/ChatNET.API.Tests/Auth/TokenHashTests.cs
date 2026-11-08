using ChatNET.API.Auth.Services;

namespace ChatNET.API.Tests.Auth;

// The secrets handed to people (refresh tokens, reset links) come from one generator, and only their hash is stored.
public class TokenHashTests
{
    [Fact]
    public void A_new_token_is_43_url_safe_characters_and_never_repeats()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => TokenHash.NewToken()).ToList();

        Assert.All(tokens, token => Assert.Matches("^[A-Za-z0-9_-]{43}$", token));
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    [Fact]
    public void The_stored_form_is_a_lowercase_hex_sha256_that_is_not_the_token()
    {
        var token = TokenHash.NewToken();

        var hash = TokenHash.Of(token);

        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.NotEqual(token, hash);
        Assert.Equal(hash, TokenHash.Of(token));
    }
}
