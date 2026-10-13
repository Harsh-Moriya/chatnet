namespace ChatNET.API.Messaging.Models;

// A position in a conversation's history. CreatedAt alone is not enough: two messages can share a
// timestamp, and a cursor that points between them would skip or repeat one. The id breaks the tie,
// so every message has exactly one position.
public readonly record struct MessageCursor(DateTime CreatedAt, Guid Id);
