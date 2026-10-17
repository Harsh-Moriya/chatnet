using MediatR;

namespace ChatNET.API.Conversations.MarkRead;

// "I have read this conversation up to and including this message."
//
// Declared as IRequest<Unit> rather than the plain IRequest. MediatR attaches the validation
// behaviour only to requests that implement IRequest<TResponse>, and the plain IRequest does not, so
// the validator next to this file would never run. Unit is MediatR's "no result".
public record MarkReadCommand(Guid ConversationId, Guid UserId, Guid MessageId) : IRequest<Unit>;
