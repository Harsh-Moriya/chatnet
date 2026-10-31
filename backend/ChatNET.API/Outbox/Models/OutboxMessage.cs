namespace ChatNET.API.Outbox.Models;

// A push that has to happen because something was saved. It is written in the same transaction as that change, so
// the two never disagree: a change that is stored always has its row, and a change that was rolled back has none.
// Whoever saved the change delivers the push right away and marks the row dispatched; a row that stays undispatched
// (the push failed, or the process died between the save and the push) is picked up by the relay.
//
// The row holds ids, not what is pushed. The message, notification or conversation is read again when the push is
// made, so a retry sends what is true then: fresh download links, and no text of a message deleted in the meantime.
public class OutboxMessage
{
    public Guid Id { get; set; }

    // Which event the payload is (see OutboxEvents).
    public string Kind { get; set; } = string.Empty;

    // The event as JSON.
    public string Payload { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    // The earliest moment the relay may take the row. A new row waits a few seconds so the request that wrote it
    // can push first; a row the relay has taken is held until the time it would be retried, so a second relay does not
    // push it as well.
    public DateTime NextAttemptAt { get; set; }

    // Null until the push has been made. A row that was pushed twice (the relay and the request, a crash between
    // the push and this mark) is harmless: clients merge a push they already have.
    public DateTime? DispatchedAt { get; set; }

    // How many times the relay has taken the row.
    public int Attempts { get; set; }

    // The reason of the latest failed push, for whoever looks at the table. Never message content.
    public string? LastError { get; set; }
}
