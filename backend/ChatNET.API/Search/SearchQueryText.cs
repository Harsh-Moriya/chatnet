using System.Text.RegularExpressions;

namespace ChatNET.API.Search;

// What a person typed into the search box, split for Postgres. The words go through websearch_to_tsquery, which reads the
// syntax people already know ("a phrase" in quotes, or, -excluded) and never throws on odd input. It has no prefix match,
// and a search box that has not seen the end of the word yet needs one ("repo" should find "report"), so the last word, when
// the text ends in one, is made a prefix query by hand.
public readonly partial record struct SearchQueryText(string Head, string? LastWord)
{
    // The longest and shortest text that is searched, counted in characters after trimming.
    public const int MinLength = 2;
    public const int MaxLength = 100;

    [GeneratedRegex(@"[\p{L}\p{N}_]+$")]
    private static partial Regex TrailingWord();

    [GeneratedRegex(@"[\p{L}\p{N}]")]
    private static partial Regex AnyLetterOrDigit();

    [GeneratedRegex(@"\S\s+or\s+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingOr();

    public static SearchQueryText From(string text)
    {
        var trimmed = text.Trim();
        var trailing = TrailingWord().Match(trimmed);

        // A word that follows a minus ("-draft") is an exclusion, and an exclusion is exact: made a prefix, it would turn into
        // a word to find. A minus inside a word ("foo-ba") is just a hyphenated word. A word that follows "or" ("cat or dog")
        // is one of two to choose from: joined to the rest as a prefix it would turn into one more word to require.
        return trailing.Success && !IsExclusion(trimmed, trailing.Index) && !IsAlternative(trimmed, trailing.Index)
            ? new SearchQueryText(trimmed[..trailing.Index], trailing.Value)
            : new SearchQueryText(trimmed, null);
    }

    private static bool IsExclusion(string text, int wordStart)
        => wordStart > 0 && text[wordStart - 1] == '-' && (wordStart == 1 || char.IsWhiteSpace(text[wordStart - 2]));

    // "or" as websearch_to_tsquery reads it: a word of its own, in any case, with something before it to choose between. One at
    // the start has nothing to choose from and is ignored by Postgres, so the word after it is still the last word typed.
    private static bool IsAlternative(string text, int wordStart)
        => TrailingOr().IsMatch(text[..wordStart]);

    // False for text with nothing a search could match, such as "!!": it finds nothing, and is not worth asking Postgres.
    public bool HasTerms => LastWord is not null || AnyLetterOrDigit().IsMatch(Head);

    public bool HasHead => AnyLetterOrDigit().IsMatch(Head);

    // The last word as to_tsquery reads it: quoted, so nothing in it is syntax, with the prefix operator. A word holds only
    // letters, digits and underscores, so there is nothing to escape; the quotes are the guard against it ever holding more.
    public string? PrefixQuery => LastWord is null ? null : $"'{LastWord.Replace("'", "''").Replace("\\", "\\\\")}':*";
}
