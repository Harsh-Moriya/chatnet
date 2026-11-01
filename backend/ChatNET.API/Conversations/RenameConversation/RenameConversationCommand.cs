using ChatNET.API.Conversations.Models;
using MediatR;

namespace ChatNET.API.Conversations.RenameConversation;

// The owner or an admin renames a group. Name is null only when the request left it out, which the validator refuses.
public record RenameConversationCommand(Guid ConversationId, Guid ActorId, string? Name) : IRequest<ConversationUpdateDto>;
