using ChatNET.API.Search;

namespace ChatNET.API.Tests.Search;

// What is typed into the search box is split into the part that Postgres reads with its web search syntax and the last word,
// which is searched as a prefix because the person may not have finished typing it.
public class SearchQueryTextTests
{
    [Fact]
    public void A_single_word_is_all_prefix()
    {
        var text = SearchQueryText.From("repo");

        Assert.Equal("", text.Head);
        Assert.Equal("repo", text.LastWord);
        Assert.False(text.HasHead);
        Assert.Equal("'repo':*", text.PrefixQuery);
    }

    [Fact]
    public void Only_the_last_word_is_a_prefix()
    {
        var text = SearchQueryText.From("  budget meeting repo ");

        Assert.Equal("budget meeting ", text.Head);
        Assert.Equal("repo", text.LastWord);
        Assert.True(text.HasHead);
    }

    [Fact]
    public void Text_that_ends_in_a_quote_or_an_operator_has_no_prefix_word()
    {
        var quoted = SearchQueryText.From("\"budget meeting\"");
        var excluded = SearchQueryText.From("budget -");

        Assert.Null(quoted.LastWord);
        Assert.Null(quoted.PrefixQuery);
        Assert.Equal("\"budget meeting\"", quoted.Head);
        Assert.Null(excluded.LastWord);
    }

    [Fact]
    public void A_word_after_a_minus_is_an_exact_exclusion_and_not_a_prefix_to_find()
    {
        var excluded = SearchQueryText.From("budget -dra");
        var alone = SearchQueryText.From("-dra");
        var hyphenated = SearchQueryText.From("foo-ba");

        Assert.Equal("budget -dra", excluded.Head);
        Assert.Null(excluded.LastWord);
        Assert.Null(alone.LastWord);
        Assert.Equal("foo-", hyphenated.Head);
        Assert.Equal("ba", hyphenated.LastWord);
    }

    [Theory]
    [InlineData("!!")]
    [InlineData("-")]
    [InlineData("\"\"")]
    [InlineData(" ... ")]
    public void Text_with_no_letters_or_digits_has_nothing_to_match(string typed)
    {
        Assert.False(SearchQueryText.From(typed).HasTerms);
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("\"phrase\"")]
    [InlineData("x")]
    [InlineData("привет")]
    public void Text_with_a_letter_or_a_digit_has_something_to_match(string typed)
    {
        Assert.True(SearchQueryText.From(typed).HasTerms);
    }

    [Theory]
    [InlineData("o'brien", "brien")]
    [InlineData("a:b", "b")]
    [InlineData("x&y", "y")]
    public void A_word_holds_only_letters_digits_and_underscores_so_the_prefix_query_has_no_syntax_in_it(string typed, string word)
    {
        var text = SearchQueryText.From(typed);

        Assert.Equal(word, text.LastWord);
        Assert.Equal($"'{word}':*", text.PrefixQuery);
    }

    [Fact]
    public void Letters_of_other_alphabets_and_digits_are_words()
    {
        Assert.Equal("отчёт2", SearchQueryText.From("привет отчёт2").LastWord);
        Assert.Equal("snake_case", SearchQueryText.From("snake_case").LastWord);
    }
}
