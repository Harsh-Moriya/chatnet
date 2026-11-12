namespace ChatNET.API.Messaging;

public static class MentionLimits
{
    // The most people one message can name. A message cannot be used to notify a whole conversation at once, or to make the server
    // look up an unbounded list of people.
    public const int Max = 20;
}
