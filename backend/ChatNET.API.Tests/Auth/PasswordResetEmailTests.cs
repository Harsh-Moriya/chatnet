using ChatNET.API.Auth.PasswordReset;

namespace ChatNET.API.Tests.Auth;

// The words of the email are built from a link and a lifetime and nothing else: no name, nothing typed by a person.
public class PasswordResetEmailTests
{
    private const string Link = "https://chat.example.test/reset-password#token=abc";

    [Fact]
    public void The_message_is_addressed_to_the_given_address_with_the_fixed_subject()
    {
        var message = PasswordResetEmail.For("alice@example.test", Link, TimeSpan.FromMinutes(30));

        Assert.Equal("alice@example.test", message.To);
        Assert.Equal("Reset your ChatNET password", message.Subject);
    }

    [Fact]
    public void Both_parts_carry_the_link_and_the_lifetime()
    {
        var message = PasswordResetEmail.For("alice@example.test", Link, TimeSpan.FromMinutes(45));

        Assert.Contains(Link, message.TextBody);
        Assert.Contains("within 45 minutes", message.TextBody);
        Assert.Contains($"href=\"{Link}\"", message.HtmlBody);
        Assert.Contains("within 45 minutes", message.HtmlBody);
    }

    [Fact]
    public void One_minute_is_written_in_the_singular()
    {
        var message = PasswordResetEmail.For("alice@example.test", Link, TimeSpan.FromMinutes(1));

        Assert.Contains("within 1 minute:", message.TextBody);
        Assert.DoesNotContain("1 minutes", message.TextBody);
    }

    // The link goes into an attribute and into text of an HTML page; whatever it holds must not be able to leave them.
    [Fact]
    public void A_link_is_escaped_in_the_html_part_and_left_as_it_is_in_the_text_part()
    {
        const string awkward = "https://chat.example.test/reset-password?a=1&b=\"2\"<x>#token=abc";

        var message = PasswordResetEmail.For("alice@example.test", awkward, TimeSpan.FromMinutes(30));

        Assert.Contains(awkward, message.TextBody);
        Assert.DoesNotContain("<x>", message.HtmlBody);
        Assert.DoesNotContain("b=\"2\"", message.HtmlBody);
        Assert.Contains("a=1&amp;b=&quot;2&quot;&lt;x&gt;", message.HtmlBody);
    }

    [Fact]
    public void The_text_says_what_to_do_if_the_person_did_not_ask()
    {
        var message = PasswordResetEmail.For("alice@example.test", Link, TimeSpan.FromMinutes(30));

        Assert.Contains("If you did not ask for this, ignore this message.", message.TextBody);
        Assert.Contains("If you did not ask for this, ignore this message.", message.HtmlBody);
    }
}
