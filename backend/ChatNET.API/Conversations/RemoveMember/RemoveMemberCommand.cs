using ChatNET.API.Conversations.Models;
using MediatR;

namespace ChatNET.API.Conversations.RemoveMember;

// The owner removes anyone, an admin removes plain members. The answer is the people of the group as they are after.
public record RemoveMemberCommand(Guid ConversationId, Guid ActorId, Guid TargetId) : IRequest<List<MemberDto>>;
