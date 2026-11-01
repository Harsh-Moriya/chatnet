using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Users.Persistence;
using Microsoft.AspNetCore.SignalR;

namespace ChatNET.API.Users;

// Tells the people who see someone's name and picture that they changed. Built from the stored row when it is called, so a
// push that is made late or twice says what is true then. Called only after the change is saved, and by whoever owns the
// push (the outbox), which decides what a failure means: this class lets it throw.
public class ProfileNotifier
{
    private readonly IHubContext<ChatHub, IChatClient> _hub;
    private readonly IProfileRepository _profiles;
    private readonly IConversationRepository _conversations;

    public ProfileNotifier(
        IHubContext<ChatHub, IChatClient> hub, IProfileRepository profiles, IConversationRepository conversations)
    {
        _hub = hub;
        _profiles = profiles;
        _conversations = conversations;
    }

    // Everyone who shares an active conversation with the person, and the person's own tabs.
    public async Task NotifyChangedAsync(Guid userId, CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetSummaryAsync(userId, cancellationToken);
        if (profile is null)
            return;

        var recipients = await _conversations.GetContactIdsAsync(userId, cancellationToken);
        recipients.Add(userId);

        await _hub.Clients.Users(recipients.Select(id => id.ToString()).ToList()).ProfileChanged(profile);
    }
}
