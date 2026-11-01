using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;
using MediatR;

namespace ChatNET.API.Conversations.GetConversation;

public class GetConversationHandler : IRequestHandler<GetConversationQuery, ConversationDto>
{
    private readonly IConversationRepository _conversations;

    public GetConversationHandler(IConversationRepository conversations)
    {
        _conversations = conversations;
    }

    public async Task<ConversationDto> Handle(GetConversationQuery request, CancellationToken cancellationToken)
    {
        // The view is the caller's own, so someone who is not in the conversation has none: they get the same refusal
        // whether it exists or not. Someone who left a group still has one, read-only, which says when they left.
        return await _conversations.GetForViewerAsync(request.ConversationId, request.UserId, cancellationToken)
               ?? throw new ForbiddenException(ConversationErrors.NotAMember);
    }
}
