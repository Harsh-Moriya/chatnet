using ChatNET.API.Auth.Models;

namespace ChatNET.API.Push.Models;

// One browser that a person allowed to alert them while they are away. Endpoint is the address of the browser vendor's
// push service that reaches that browser; P256dh and Auth are the two keys the browser made for it, which the payload is
// encrypted to, so the push service relays text it cannot read.
//
// The endpoint is a capability: whoever holds it can ask the push service to deliver to that browser (a push also needs
// the server's signing key, which narrows that to this application). It is therefore kept out of URLs and logs, and
// only ever appears in a request body and in this table.
public class PushSubscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    // When the browser last registered (the page renews it every time it opens) or a push reached it. A subscription that
    // neither happens to for the retention period belongs to a browser that is no longer used.
    public DateTime LastUsedAt { get; set; }

    public AppUser User { get; set; } = null!;
}
