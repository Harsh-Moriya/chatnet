using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;
using MediatR;

namespace ChatNET.API.Conversations.GetMyConversations;

public class GetMyConversationsHandler : IRequestHandler<GetMyConversationsQuery, List<ConversationDto>>
{
    private readonly IConversationRepository _conversations;

    public GetMyConversationsHandler(IConversationRepository conversations)
    {
        _conversations = conversations;
    }

    public Task<List<ConversationDto>> Handle(GetMyConversationsQuery request, CancellationToken cancellationToken)
        => _conversations.GetForUserAsync(request.UserId, cancellationToken);
}
