using System.Text.RegularExpressions;

namespace ChatNET.API.Notifications;

// Finds who a message names with "@username". User names are 3 to 50 letters, digits and underscores
// (RegisterCommandValidator), so a mention is an at sign followed by such a name, with no word character on
// either side. That keeps an email address ("ana@example.com") and a longer word from being read as a mention,
// and ends a name at a full stop or a comma ("thanks @ana.") without any guessing.
public static partial class MentionParser
{
    // More names than this in one message are ignored from the twenty-first on, so a message cannot be used to
    // notify a whole conversation at once, or to make the server look up an unbounded list of names.
    public const int MaxMentions = 20;

    [GeneratedRegex("(?<![A-Za-z0-9_])@([A-Za-z0-9_]{3,50})(?![A-Za-z0-9_])")]
    private static partial Regex Mention();

    // The names in the form Identity stores them (upper case), each once, in the order they first appear.
    public static IReadOnlyList<string> NormalizedUserNamesIn(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return [];

        return Mention().Matches(content)
            .Select(match => match.Groups[1].Value.ToUpperInvariant())
            .Distinct()
            .Take(MaxMentions)
            .ToList();
    }
}
