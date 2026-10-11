using ChatNET.API.Conversations.Models;
using MediatR;

namespace ChatNET.API.Conversations.CreateDirect;

public record CreateDirectCommand(Guid UserId, Guid OtherUserId) : IRequest<ConversationDto>;
