namespace ChatNET.API.Calls.Models;

// A finished call as its timeline row shows it. The caller is the sender of the row and the callee is the other member of the
// conversation, so neither is repeated here. The length of the call is EndedAt minus AnsweredAt; a call nobody answered has none.
//
// IsMissed is the one definition of a missed call that every client and the server go by: nobody answered and the callee did not
// turn it down (so it covers Missed, Cancelled, Busy and a Failed call that never connected). A missed call counts as unread for
// the callee and alerts them when they are away. The unread count in the conversation list is the same rule written in SQL.
public record CallDto(
    Guid Id,
    CallMedia Media,
    DateTime StartedAt,
    DateTime? AnsweredAt,
    DateTime EndedAt,
    CallEndReason EndReason
)
{
    public bool IsMissed => AnsweredAt is null && EndReason != CallEndReason.Declined;
}
