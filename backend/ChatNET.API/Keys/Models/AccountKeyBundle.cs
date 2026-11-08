using ChatNET.API.Auth.Models;

namespace ChatNET.API.Keys.Models;

// The public keys of one generation of a person's account key, signed by their identity key, exactly as the browser made
// them (134 bytes, see KeyFormats). The server cannot open anything with them: it hands them to the people who want to send
// the person a conversation key, who check the signature themselves.
//
// A new generation is made when a device is removed, so conversation keys sealed from then on cannot be opened with the
// private key a lost device may hold. Earlier generations stay, because messages sealed to them still have to be readable.
public class AccountKeyBundle
{
    public Guid UserId { get; set; }

    public int Generation { get; set; }

    public byte[] Bundle { get; set; } = [];

    public DateTime CreatedAt { get; set; }

    public AppUser User { get; set; } = null!;
}
