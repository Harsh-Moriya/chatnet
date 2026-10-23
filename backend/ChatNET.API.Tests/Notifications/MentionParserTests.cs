using ChatNET.API.Notifications;

namespace ChatNET.API.Tests.Notifications;

// Who a message names. The rule has to agree with the browser's (the same cases are in its tests): the person
// who is highlighted in the message is the person who was notified.
public class MentionParserTests
{
    private static string[] Names(string? content) => MentionParser.NormalizedUserNamesIn(content).ToArray();

    [Fact]
    public void A_user_name_after_an_at_sign_is_a_mention_in_the_form_identity_stores_names()
    {
        Assert.Equal(["ANA_SMITH"], Names("hello @ana_smith"));
    }

    [Theory]
    [InlineData("@ana can you look")]
    [InlineData("can you look @ana")]
    [InlineData("can you @ana look")]
    [InlineData("(@ana)")]
    [InlineData("thanks @ana, see you")]
    [InlineData("thanks @ana.")]
    [InlineData("really @ana?")]
    [InlineData("@ana!")]
    [InlineData("line one\n@ana\nline three")]
    [InlineData("\t@ana\t")]
    public void A_mention_is_found_at_the_start_in_the_middle_at_the_end_and_next_to_punctuation(string content)
    {
        Assert.Equal(["ANA"], Names(content));
    }

    [Theory]
    [InlineData("@ANA")]
    [InlineData("@Ana")]
    [InlineData("@aNa")]
    public void Case_does_not_matter(string content)
    {
        Assert.Equal(["ANA"], Names(content));
    }

    [Theory]
    [InlineData("write to ana@example.com")]
    [InlineData("xana@bob")]
    [InlineData("a_@bob")]
    [InlineData("9@bob")]
    public void An_at_sign_inside_a_word_is_not_a_mention(string content)
    {
        Assert.Empty(Names(content));
    }

    [Theory]
    [InlineData("@")]
    [InlineData("@ ana")]
    [InlineData("@@")]
    [InlineData("at ana")]
    [InlineData("")]
    public void An_at_sign_without_a_name_is_nothing(string content)
    {
        Assert.Empty(Names(content));
    }

    [Fact]
    public void Nothing_in_nothing_is_nothing()
    {
        Assert.Empty(Names(null));
        Assert.Empty(Names("   "));
    }

    // User names are 3 to 50 characters (RegisterCommandValidator).
    [Fact]
    public void A_name_shorter_than_three_characters_is_not_a_mention()
    {
        Assert.Empty(Names("@ab"));
        Assert.Equal(["ABC"], Names("@abc"));
    }

    [Fact]
    public void A_name_of_fifty_characters_is_a_mention_and_a_longer_run_is_not_cut_short_into_one()
    {
        var fifty = new string('a', 50);

        Assert.Equal([fifty.ToUpperInvariant()], Names($"@{fifty}"));
        Assert.Empty(Names($"@{fifty}a"));
    }

    [Theory]
    [InlineData("@ana_1", "ANA_1")]
    [InlineData("@_ana_", "_ANA_")]
    [InlineData("@123", "123")]
    public void Letters_digits_and_underscores_make_up_a_name(string content, string expected)
    {
        Assert.Equal([expected], Names(content));
    }

    // Anything else ends the name: a hyphen, a full stop, a letter outside A to Z.
    [Theory]
    [InlineData("@ana-smith", "ANA")]
    [InlineData("@ana.smith", "ANA")]
    [InlineData("@anaé", "ANA")]
    [InlineData("@ana@bob", "ANA")]
    public void A_character_that_cannot_be_in_a_name_ends_it(string content, string expected)
    {
        Assert.Equal(expected, Names(content)[0]);
    }

    [Fact]
    public void Two_at_signs_in_a_row_name_the_person_after_the_second()
    {
        Assert.Equal(["BOB"], Names("@@bob"));
    }

    [Fact]
    public void Each_name_counts_once_in_the_order_it_first_appears()
    {
        Assert.Equal(["CAT", "ANA", "BOB"], Names("@cat then @ana and @CAT again and @bob and @Ana"));
    }

    [Fact]
    public void Only_the_first_twenty_names_of_a_message_are_taken()
    {
        var content = string.Join(' ', Enumerable.Range(1, 25).Select(i => $"@user{i:D2}"));

        var names = Names(content);

        Assert.Equal(MentionParser.MaxMentions, names.Length);
        Assert.Equal("USER01", names[0]);
        Assert.Equal("USER20", names[^1]);
    }

    // The duplicates do not use up the twenty.
    [Fact]
    public void Repeating_a_name_does_not_use_up_the_limit()
    {
        var content = string.Join(' ', Enumerable.Repeat("@same", 30)) + " @other";

        Assert.Equal(["SAME", "OTHER"], Names(content));
    }
}
