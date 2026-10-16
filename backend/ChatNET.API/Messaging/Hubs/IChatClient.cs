using ChatNET.API.Conversations.Models;
using ChatNET.API.Messaging.Models;

namespace ChatNET.API.Messaging.Hubs;

// The methods the server can call on a connected browser. A strongly typed hub
// (Hub<IChatClient>) turns "SendAsync("ReceiveMessage", ...)" into a compile-checked
// method call, so a typo in an event name or a wrong argument type fails the build
// instead of silently sending an event nobody listens for.
// Each method name here is the event name the JavaScript client subscribes to.
public interface IChatClient
{
    Task ReceiveMessage(MessageDto message);

    Task UserTyping(Guid conversationId, Guid userId, string displayName, bool isTyping);

    // Sent to users who are added to a conversation while already connected, because
    // their open connections are not yet in that conversation's group.
    Task AddedToConversation(ConversationDto conversation);

    // A member read the conversation up to and including every message sent at or before readAt.
    // Sent to the whole conversation, the reader's own other tabs included, after the new position
    // is stored; it is only sent when the position moved.
    Task MessageRead(Guid conversationId, Guid userId, DateTime readAt);
}
