using ChatNET.API.Conversations.KeyEpochs;
using MediatR;

namespace ChatNET.API.Conversations.CreateKeyEpoch;

// A member stores the next key period of a conversation they are in, made in their browser for everyone who is in it now.
//
// Declared as IRequest<Unit> and not the plain IRequest, because MediatR attaches the validation behaviour only to requests that
// have a response type, and the validator next to this file must run.
public record CreateKeyEpochCommand(Guid ConversationId, Guid UserId, EpochRequest Epoch) : IRequest<Unit>;
