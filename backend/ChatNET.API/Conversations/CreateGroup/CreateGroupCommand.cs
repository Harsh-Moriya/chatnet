using ChatNET.API.Conversations.KeyEpochs;
using ChatNET.API.Conversations.Models;
using MediatR;

namespace ChatNET.API.Conversations.CreateGroup;

// Id is the id the browser gave the group, which it needs to know before the group exists: the first key period is made for that id.
public record CreateGroupCommand(
    Guid UserId,
    Guid Id,
    string Name,
    IReadOnlyList<Guid> MemberIds,
    EpochRequest Epoch
) : IRequest<ConversationDto>;
