using ChatNET.API.Conversations.Models;
using MediatR;

namespace ChatNET.API.Conversations.GetMembers;

public record GetMembersQuery(Guid ConversationId, Guid UserId) : IRequest<List<MemberDto>>;
