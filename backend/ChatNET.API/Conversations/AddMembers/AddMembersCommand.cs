using ChatNET.API.Conversations.KeyEpochs;
using ChatNET.API.Conversations.Models;
using MediatR;

namespace ChatNET.API.Conversations.AddMembers;

// The answer is the people of the group as they are after the change, so the caller shows exactly what was stored. Epoch is the new
// key period made for everyone who will be in the group, the people added included; it is saved with them.
public record AddMembersCommand(Guid ConversationId, Guid ActorId, List<Guid> UserIds, EpochRequest Epoch) : IRequest<List<MemberDto>>;
