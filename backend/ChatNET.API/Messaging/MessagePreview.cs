namespace ChatNET.API.Messaging;

// A message cut to the length shown where there is only room for a line of it: the conversation list and a
// notification. Counted in UTF-16 code units, like the message limit itself.
public static class MessagePreview
{
    public const int Length = 100;

    public static string? Of(string? content)
        => content is { Length: > Length } ? content[..Length] : content;
}
