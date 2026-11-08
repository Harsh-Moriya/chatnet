using ChatNET.API.Auth.Models;

namespace ChatNET.API.Keys.Models;

// The private keys of a person, encrypted in their browser under a key stretched from a passphrase only they know. The server
// stores the blob and can neither open it nor check the passphrase: whoever fetches it can try passphrases offline, which is
// why it is only ever returned to its owner.
//
// Version counts the times the backup was replaced (a change of passphrase). A replacement names the version it was made
// from, so two devices changing the passphrase together cannot both win.
public class KeyBackup
{
    public Guid UserId { get; set; }

    public byte[] Blob { get; set; } = [];

    public int Version { get; set; }

    public DateTime UpdatedAt { get; set; }

    public AppUser User { get; set; } = null!;
}
