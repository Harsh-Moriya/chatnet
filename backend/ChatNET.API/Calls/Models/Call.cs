using ChatNET.API.Auth.Models;
using ChatNET.API.Conversations.Models;

namespace ChatNET.API.Calls.Models;

// One finished call of a direct conversation. A call is written once, when it has ended (CallLog); while it rings or runs it lives
// in Redis and is not in this table. Its Id is made when the call starts, so the live state and this row share it, and it is what
// makes recording a call twice harmless.
public class Call
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public Guid CallerId { get; set; }

    public Guid CalleeId { get; set; }

    public CallMedia Media { get; set; }

    // When it began to ring.
    public DateTime StartedAt { get; set; }

    // When the callee accepted. Null for a call nobody answered.
    public DateTime? AnsweredAt { get; set; }

    public DateTime EndedAt { get; set; }

    public CallEndReason EndReason { get; set; }

    public Conversation Conversation { get; set; } = null!;
    public AppUser Caller { get; set; } = null!;
    public AppUser Callee { get; set; } = null!;
}
