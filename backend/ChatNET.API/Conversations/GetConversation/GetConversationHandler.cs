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
        // The repository builds the view for any viewer it is given, so membership is checked first.
        // A conversation that does not exist gets the same refusal as one the caller is not in.
        if (!await _conversations.IsActiveMemberAsync(request.ConversationId, request.UserId, cancellationToken))
            throw new ForbiddenException(ConversationErrors.NotAMember);

        return await _conversations.GetForViewerAsync(request.ConversationId, request.UserId, cancellationToken)
               ?? throw new ForbiddenException(ConversationErrors.NotAMember);
    }
}
