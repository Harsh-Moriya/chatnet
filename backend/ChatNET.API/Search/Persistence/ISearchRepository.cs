using ChatNET.API.Messaging.Models;
using ChatNET.API.Search.Models;

namespace ChatNET.API.Search.Persistence;

public interface ISearchRepository
{
    // The position of a message the person may read (in this conversation when one is named, otherwise in any conversation
    // they were in, while they were in it), or null. Anything else is "no such message", so a cursor reveals nothing.
    Task<MessageCursor?> FindCursorAsync(
        Guid userId, Guid? conversationId, Guid messageId, CancellationToken cancellationToken);

    // The messages that match, newest first, at most "take", starting after the cursor when there is one. Only messages sent
    // while the person was in the conversation, never a deleted one. A message is matched by its text or by the name of one of
    // its files.
    Task<List<SearchHitDto>> SearchAsync(
        Guid userId, Guid? conversationId, SearchQueryText text, MessageCursor? after, int take,
        CancellationToken cancellationToken);
}
