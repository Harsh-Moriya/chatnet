using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;
using MediatR;

namespace ChatNET.API.Conversations.GetMembers;

public class GetMembersHandler : IRequestHandler<GetMembersQuery, List<MemberDto>>
{
    private readonly IConversationRepository _conversations;

    public GetMembersHandler(IConversationRepository conversations)
    {
        _conversations = conversations;
    }

    public async Task<List<MemberDto>> Handle(GetMembersQuery request, CancellationToken cancellationToken)
    {
        // Only members may see who else is in a conversation. A conversation that does not exist
        // gets the same refusal as one the caller is not in.
        if (!await _conversations.IsActiveMemberAsync(request.ConversationId, request.UserId, cancellationToken))
            throw new ForbiddenException(ConversationErrors.NotAMember);

        return await _conversations.GetActiveMembersAsync(request.ConversationId, cancellationToken);
    }
}
