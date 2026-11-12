using System.Text;
using ChatNET.API.Keys;
using ChatNET.API.Messaging.Models;

namespace ChatNET.API.Tests.Infrastructure;

// A message blob of the shape the server accepts, which carries its text in the clear so that a test can read it back. It is not
// sealed or signed, and nothing needs it to be: the server cannot open a blob or check a signature, so it cannot tell. What the
// server checks is the header (version, suite, epoch) and the size, and the layout here keeps both honest: the header, a nonce, the
// text, then room for the tag and the signature, so even an empty text is the smallest blob the server allows.
public static class FakeMessage
{
    private const int HeaderBytes = 2 + 4 + 12;
    private const int TagAndSignatureBytes = 16 + KeyFormats.SignatureBytes;

    public static byte[] Blob(int epoch, string? text)
    {
        var body = Encoding.UTF8.GetBytes(text ?? string.Empty);
        var blob = new byte[HeaderBytes + body.Length + TagAndSignatureBytes];
        blob[0] = KeyFormats.Version;
        blob[1] = KeyFormats.Suite;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(2, 4), (uint)epoch);
        body.CopyTo(blob, HeaderBytes);
        return blob;
    }

    public static string Ciphertext(int epoch, string? text) => KeyFormats.Encode(Blob(epoch, text));

    // The text a blob made here carries, or null for no blob (a deleted message).
    public static string? TextOf(string? ciphertext)
    {
        if (ciphertext is null)
            return null;

        var blob = KeyFormats.Decode(ciphertext, int.MaxValue)!;
        return Encoding.UTF8.GetString(blob, HeaderBytes, blob.Length - HeaderBytes - TagAndSignatureBytes);
    }

    // The text of a stored message, or null when it is deleted.
    public static string? Text(this Message message) => message.Ciphertext is null ? null : TextOf(KeyFormats.Encode(message.Ciphertext));

    public static string? TextOfBlob(byte[]? ciphertext) => ciphertext is null ? null : TextOf(KeyFormats.Encode(ciphertext));

    public static int EpochOf(string ciphertext) => ChatNET.API.Messaging.MessageBlob.EpochOf(KeyFormats.Decode(ciphertext, int.MaxValue)!);
}
