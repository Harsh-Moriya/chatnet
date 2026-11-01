using ChatNET.API.Conversations.Models;

namespace ChatNET.API.Search.Models;

// A piece of text that matched, or text around a match. The client draws the pieces itself, so nothing in the text is ever
// read as markup.
public record SnippetPart(string Text, bool Match);

// One message that matched a search, with what is needed to show it in a list and to open the conversation at it.
// ConversationName is as the searcher sees the conversation (a direct chat is named after the other person). Snippet is a
// short piece of the text around the match, empty for a message that matched by the name of a file. FileName is the name of
// the attachment that matched, null when the text did.
public record SearchHitDto(
    Guid MessageId,
    Guid ConversationId,
    string ConversationName,
    ConversationType ConversationType,
    Guid SenderId,
    string SenderDisplayName,
    DateTime CreatedAt,
    List<SnippetPart> Snippet,
    string? FileName);
