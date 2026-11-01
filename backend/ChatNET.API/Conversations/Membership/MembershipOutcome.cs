namespace ChatNET.API.Conversations.Membership;

// What a change to a group's membership did, as the people it touched: who is in the group now and was not before (a new
// member, or one who is back after leaving), whose role changed, and who is no longer in it. The pushes and the
// notifications that follow are worked out from this and nothing else.
public sealed record MembershipOutcome(
    IReadOnlyList<Guid> Joined,
    IReadOnlyList<Guid> Changed,
    IReadOnlyList<Guid> Left)
{
    public static MembershipOutcome None { get; } = new([], [], []);

    public bool IsEmpty => Joined.Count == 0 && Changed.Count == 0 && Left.Count == 0;

    // Everyone whose row changed.
    public IReadOnlyList<Guid> Affected => [.. Joined, .. Changed, .. Left];
}
