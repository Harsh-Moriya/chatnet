using ChatNET.API.Conversations.Models;
using MediatR;

namespace ChatNET.API.Conversations.LeaveConversation;

// The caller leaves a group. The answer is the moment they stopped being a member.
public record LeaveConversationCommand(Guid ConversationId, Guid UserId) : IRequest<LeaveResultDto>;
