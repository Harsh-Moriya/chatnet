using ChatNET.API.Conversations.Models;
using MediatR;

namespace ChatNET.API.Conversations.GetMyConversations;

public record GetMyConversationsQuery(Guid UserId) : IRequest<List<ConversationDto>>;
