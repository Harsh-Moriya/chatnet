using ChatNET.API.Conversations.Models;
using MediatR;

namespace ChatNET.API.Conversations.GetConversation;

public record GetConversationQuery(Guid ConversationId, Guid UserId) : IRequest<ConversationDto>;
