using ChatNET.API.Conversations.Models;
using MediatR;

namespace ChatNET.API.Conversations.ChangeMemberRole;

// Only the owner changes roles. Role is null only when the request left it out, which the validator refuses. Making
// someone the owner hands the group over. The answer is the people of the group as they are after.
public record ChangeMemberRoleCommand(Guid ConversationId, Guid ActorId, Guid TargetId, MemberRole? Role)
    : IRequest<List<MemberDto>>;
