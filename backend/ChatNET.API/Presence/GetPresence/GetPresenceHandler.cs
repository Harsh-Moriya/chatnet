using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Presence.Persistence;
using MediatR;

namespace ChatNET.API.Presence.GetPresence;

// Who among the caller's contacts is online, and when the others were last seen. Only contacts are
// ever listed: the people the caller shares an active conversation with, which is exactly who the
// pushes go to, so a snapshot and the events describe the same set of people.
public class GetPresenceHandler : IRequestHandler<GetPresenceQuery, List<PresenceDto>>
{
    private readonly IConversationRepository _conversations;
    private readonly IPresenceRepository _presence;
    private readonly IPresenceStore _store;

    public GetPresenceHandler(IConversationRepository conversations, IPresenceRepository presence, IPresenceStore store)
    {
        _conversations = conversations;
        _presence = presence;
        _store = store;
    }

    public async Task<List<PresenceDto>> Handle(GetPresenceQuery request, CancellationToken cancellationToken)
    {
        var contacts = await _conversations.GetContactIdsAsync(request.UserId, cancellationToken);
        var lastSeen = await _presence.GetLastSeenAsync(contacts, cancellationToken);
        var snapshot = await _store.GetSnapshotAsync(contacts, cancellationToken);

        // One time for every entry: the moment the snapshot was read. A client applies an entry only if no
        // newer event about that person has been applied, so a push that raced the request is not undone.
        return contacts
            .Select(id => new PresenceDto(
                id,
                snapshot.Online.Contains(id),
                lastSeen.GetValueOrDefault(id),
                snapshot.At))
            .ToList();
    }
}
