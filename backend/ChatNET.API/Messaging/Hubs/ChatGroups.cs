namespace ChatNET.API.Messaging.Hubs;

public static class ChatGroups
{
    // One SignalR group per conversation. Defined in one place so the hub and the
    // handlers that push to the group can never disagree on the name format.
    public static string Conversation(Guid conversationId) => $"conv:{conversationId}";
}
