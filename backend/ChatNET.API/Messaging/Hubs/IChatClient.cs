using ChatNET.API.Conversations.Models;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Presence;
using ChatNET.API.Users.SearchUsers;

namespace ChatNET.API.Messaging.Hubs;

// The methods the server can call on a connected browser. A strongly typed hub
// (Hub<IChatClient>) turns "SendAsync("ReceiveMessage", ...)" into a compile-checked
// method call, so a typo in an event name or a wrong argument type fails the build
// instead of silently sending an event nobody listens for.
// Each method name here is the event name the JavaScript client subscribes to.
public interface IChatClient
{
    Task ReceiveMessage(MessageDto message);

    // A message already shown changed: it was edited or deleted. Carries the whole message as it is now. Sent to the
    // whole conversation, the changer's own tabs included, after the change is stored. A client keeps the copy with
    // the higher EditCount, and a deleted copy over any other, so a late or repeated push cannot undo a change.
    Task MessageUpdated(MessageDto message);

    Task UserTyping(Guid conversationId, Guid userId, string displayName, bool isTyping);

    // Sent to users who are added to a conversation while already connected, because
    // their open connections are not yet in that conversation's group.
    Task AddedToConversation(ConversationDto conversation);

    // The people of a group changed: someone joined, someone's role changed, someone left or was removed. Sent to the
    // people who are in the group now, one by one and not to its group of connections, so someone who was just removed
    // never hears who is left. Carries the entries as they are when it is sent, so it is safe to receive twice.
    Task MembersChanged(MembersChangedDto change);

    // A group was renamed, or its picture changed. Sent to the people who are in it.
    Task ConversationUpdated(ConversationUpdateDto update);

    // Someone changed their name or picture. Sent to the people who share a conversation with them and to their own tabs,
    // carrying how they are now, so it is safe to receive twice or late.
    Task ProfileChanged(UserSummaryDto profile);

    // The receiver is no longer in the group: they left it, or were removed. Sent to every open tab of that person. The
    // conversation stays in their list, read-only, with what was said up to leftAt.
    Task RemovedFromConversation(Guid conversationId, DateTime leftAt);

    // A member read the conversation up to and including every message sent at or before readAt.
    // Sent to the whole conversation, the reader's own other tabs included, after the new position
    // is stored; it is only sent when the position moved.
    Task MessageRead(Guid conversationId, Guid userId, DateTime readAt);

    // Someone who shares a conversation with the receiver came online or went offline. Sent to the
    // people who may see their presence (and nobody else), once per change: a second tab or a page
    // refresh is not a change.
    Task PresenceChanged(PresenceDto presence);

    // The receiver muted or unmuted a conversation, in this tab or another. Sent to every open tab of that person
    // and nobody else, so each of their tabs agrees on which alerts to raise.
    Task ConversationMuteChanged(Guid conversationId, bool isMuted);

    // Someone mentioned the receiver, or added them to a group. Sent to every open tab of that one person, once the
    // notification is saved.
    Task NotificationReceived(NotificationDto notification);

    // These notifications of the receiver were marked read: by this tab, by another tab, or by reading the
    // conversation they are about. Sent to every open tab of that person, so their badge agrees everywhere.
    Task NotificationsRead(Guid[] ids);

    // This notification of the receiver was dismissed, in this tab or another.
    Task NotificationDismissed(Guid id);
}
