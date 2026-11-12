using System.Buffers.Binary;
using ChatNET.API.Keys;

namespace ChatNET.API.Messaging;

// What the server checks about the text of a message before it stores it. The blob is sealed and signed in the browser of the sender
// and the server cannot open it or verify the signature (the members do), so it checks shape only: the version bytes, the size, and
// the epoch in the header, which is the key period the message is stored under. The layout is the one in the public specification of
// the encryption formats: version, suite, epoch (4 bytes), nonce (12), ciphertext and tag, signature (64).
public static class MessageBlob
{
    // Version, suite, epoch, nonce, a tag and a signature: the smallest blob there can be.
    public const int MinBytes = 2 + 4 + 12 + 16 + KeyFormats.SignatureBytes;

    public const int MaxBytes = 32 * 1024;

    private const int EpochAt = 2;

    // The blob in a request, or null when it is not base64url or not the shape of a message. Checked before decoding, because
    // decoding something that is not base64url throws and a request must not be able to make it.
    public static byte[]? Decode(string? value)
    {
        var blob = KeyFormats.Decode(value, MaxBytes);
        return blob is not null && IsWellFormed(blob) ? blob : null;
    }

    public static bool IsWellFormed(byte[] blob) =>
        blob.Length is >= MinBytes and <= MaxBytes
        && blob[0] == KeyFormats.Version
        && blob[1] == KeyFormats.Suite
        && EpochOf(blob) >= 1;

    // The key period the message was sealed under. 0 for a blob whose epoch does not fit a conversation's, which is no epoch at all.
    public static int EpochOf(byte[] blob)
    {
        var epoch = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(EpochAt, 4));
        return epoch <= int.MaxValue ? (int)epoch : 0;
    }
}
