using MediatR;

namespace ChatNET.API.Conversations.MarkRead;

// "I have read this conversation up to and including this message."
public record MarkReadCommand(Guid ConversationId, Guid UserId, Guid MessageId) : IRequest;
