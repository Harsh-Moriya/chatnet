using MediatR;

namespace ChatNET.API.Conversations.SetMuted;

// "Mute (or unmute) this conversation for me." IsMuted is nullable so that a body without it is refused by the
// validator instead of being read as "unmute".
//
// Declared as IRequest<Unit> so the validation behaviour applies (see MarkReadCommand).
public record SetMutedCommand(Guid ConversationId, Guid UserId, bool? IsMuted) : IRequest<Unit>;
