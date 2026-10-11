namespace ChatNET.API.Conversations.Models;

public static class DirectKey
{
    // Both participants must produce the same key regardless of who starts the
    // conversation, so the two IDs are sorted before joining. Ordinal comparison on the
    // lowercase "D" format is stable across machines and cultures.
    public static string For(Guid userA, Guid userB)
    {
        var a = userA.ToString("D");
        var b = userB.ToString("D");

        return string.CompareOrdinal(a, b) <= 0 ? $"{a}:{b}" : $"{b}:{a}";
    }
}
