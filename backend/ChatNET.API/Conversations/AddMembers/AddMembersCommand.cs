using ChatNET.API.Conversations.Models;
using MediatR;

namespace ChatNET.API.Conversations.AddMembers;

// The answer is the people of the group as they are after the change, so the caller shows exactly what was stored.
public record AddMembersCommand(Guid ConversationId, Guid ActorId, List<Guid> UserIds) : IRequest<List<MemberDto>>;
