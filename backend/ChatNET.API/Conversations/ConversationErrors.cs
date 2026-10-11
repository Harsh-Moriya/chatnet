namespace ChatNET.API.Conversations;

public static class ConversationErrors
{
    // The one refusal text for every "you may not use this conversation" case, used by REST handlers
    // and by the hub. It is the same whether the conversation does not exist or the caller is not
    // in it, so neither can be used to find out which conversation IDs are real.
    public const string NotAMember = "You are not a member of this conversation.";
}
