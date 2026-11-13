namespace ChatNET.API.Messaging;

// The "code" a refused change of a message carries in its problem details, so the browser can tell what to do without reading text.
public static class MessageCodes
{
    // The edit was made from a version of the message that is no longer the newest: it was edited somewhere else first. The edit
    // count is part of what the sender signed, so the browser reloads the message and makes the edit again from it.
    public const string EditCountStale = "edit-count-stale";

    // The changes feed was asked for a point past the newest change the server has, which a client can only hold if the server went back
    // to an older state (a restore). What the client keeps for the conversation cannot be trusted any more, so it reads it again.
    public const string ChangesCursorAhead = "changes-cursor-ahead";
}
