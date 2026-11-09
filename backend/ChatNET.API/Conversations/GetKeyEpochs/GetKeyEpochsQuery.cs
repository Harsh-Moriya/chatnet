using ChatNET.API.Conversations.Models;
using MediatR;

namespace ChatNET.API.Conversations.GetKeyEpochs;

// The epochs of a conversation the caller has an envelope for, from `From`, oldest first, at most `Limit` of them.
public record GetKeyEpochsQuery(Guid ConversationId, Guid UserId, int From, int Limit) : IRequest<KeyEpochPageDto>;
