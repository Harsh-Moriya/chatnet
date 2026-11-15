namespace ChatNET.API.Conversations.Models;

// The other person of a direct conversation, and whether either of them can no longer be reached: a token stays valid until it expires, so
// an account that was deactivated after it was issued can still ask for a call, and cannot be called.
public sealed record DirectPeer(Guid PeerId, bool PeerIsDeactivated, bool SelfIsDeactivated);
