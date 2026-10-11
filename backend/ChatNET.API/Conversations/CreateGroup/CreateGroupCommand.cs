using ChatNET.API.Conversations.Models;
using MediatR;

namespace ChatNET.API.Conversations.CreateGroup;

public record CreateGroupCommand(
    Guid UserId,
    string Name,
    IReadOnlyList<Guid> MemberIds
) : IRequest<ConversationDto>;
