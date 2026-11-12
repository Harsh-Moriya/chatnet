namespace ChatNET.API.Messaging.Models;

// One page of the changes feed. Changes are the messages whose latest change is numbered after the point the client asked from, in
// the order of that number, each in its current state: a message that was edited since appears once, as it is now, and a deleted
// one appears without its text. Cursor is the number to ask from next: the number of the last change on the page, or the
// conversation's newest number when there is nothing more to read. More says there is another page to read before the client is up
// to date.
public record MessageChangesDto(List<MessageDto> Changes, long Cursor, bool More);
