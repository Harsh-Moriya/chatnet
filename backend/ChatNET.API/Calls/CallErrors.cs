namespace ChatNET.API.Calls;

// The codes a refused call method carries as the message of its HubException, so a client can tell what to do without reading text.
public static class CallErrors
{
    // StartCall
    public const string NotAMember = "not-a-member";
    public const string NotADirectConversation = "not-a-direct-conversation";
    public const string PeerUnavailable = "peer-unavailable";
    public const string AlreadyInACall = "already-in-a-call";
    public const string CalleeBusy = "callee-busy";
    public const string RateLimited = "rate-limited";

    // Any method, when the live state could not be reached.
    public const string Unavailable = "calls-unavailable";

    // The others
    public const string CallNotFound = "call-not-found";
    public const string CallNotRinging = "call-not-ringing";
    public const string NotTheCallee = "not-the-callee";
    public const string NotInTheCall = "not-in-the-call";
    public const string CallEnded = "call-ended";
    public const string BadToken = "bad-token";
    public const string SignalTooLarge = "signal-too-large";
    public const string SignalLimit = "signal-limit";
}
