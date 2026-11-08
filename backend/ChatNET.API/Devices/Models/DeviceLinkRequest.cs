using ChatNET.API.Auth.Models;

namespace ChatNET.API.Devices.Models;

// A new device asking a device that is already signed in for the keys of the person. The new device made a temporary key
// pair and shows a code derived from the public half; the signed-in device finds the request by that code, checks the
// code against the key the server returned, and answers with the secrets of the person sealed to that key. The server
// relays one sealed box and cannot open it.
//
// The request lives for a few minutes. The answer is collected once: collecting it clears it, so the box does not stay on
// the server.
public class DeviceLinkRequest
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    // Ten characters (Crockford base32), 50 bits of a hash of the temporary public key. Unique among a person's requests.
    public string Code { get; set; } = string.Empty;

    public byte[] TemporaryPublicKey { get; set; } = [];

    // What the new device calls itself, shown to the person on the device that approves.
    public DeviceKind Kind { get; set; }
    public string Name { get; set; } = string.Empty;

    // The sealed secrets, set when the request is approved and cleared when the new device collects it.
    public byte[]? Payload { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? CollectedAt { get; set; }

    public AppUser User { get; set; } = null!;
}
