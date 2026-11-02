using ChatNET.API.Search.Models;
using MediatR;

namespace ChatNET.API.Search.SearchMessages;

// UserId comes from the token. Q is null only when the request left it out, which the validator refuses. ConversationId
// limits the search to one conversation. Before is the id of the oldest result the caller already has, to read further back.
public record SearchMessagesQuery(Guid UserId, string? Q, Guid? ConversationId, Guid? Before)
    : IRequest<List<SearchHitDto>>;
