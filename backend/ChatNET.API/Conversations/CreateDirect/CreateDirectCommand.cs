using ChatNET.API.Conversations.KeyEpochs;
using ChatNET.API.Conversations.Models;
using MediatR;

namespace ChatNET.API.Conversations.CreateDirect;

// Id is the id the browser gave the conversation, which it needs to know before the conversation exists: the first key period is
// made for that id. Epoch is that first period; it is only used when the conversation is new.
public record CreateDirectCommand(Guid UserId, Guid Id, Guid OtherUserId, EpochRequest Epoch) : IRequest<ConversationDto>;
