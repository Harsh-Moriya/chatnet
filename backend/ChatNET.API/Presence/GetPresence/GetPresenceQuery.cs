using MediatR;

namespace ChatNET.API.Presence.GetPresence;

public record GetPresenceQuery(Guid UserId) : IRequest<List<PresenceDto>>;
