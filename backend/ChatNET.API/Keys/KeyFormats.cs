using System.Buffers.Text;

namespace ChatNET.API.Keys;

// What the server checks about the encrypted blobs it stores for people. It cannot open them and cannot verify the signature
// of a bundle (the clients do), so it checks shape only: the version bytes, the sizes and the generation. The layouts are the ones
// in the public specification of the encryption formats.
public static class KeyFormats
{
    public const byte Version = 1;
    public const byte Suite = 1;
    public const byte Pbkdf2Sha256 = 1;

    public const int KeyBytes = 32;
    public const int SignatureBytes = 64;

    // version, suite, generation (4), identity public key (32), account public key (32), signature (64).
    public const int BundleBytes = 2 + 4 + 2 * KeyBytes + SignatureBytes;

    // The hash a conversation key is committed to.
    public const int CommitmentBytes = 32;

    // A conversation key sealed for one member: version, suite, ephemeral public key (32), nonce (12), the key (32) and its tag (16).
    public const int EnvelopeBytes = 2 + KeyBytes + 12 + KeyBytes + 16;
    private const int BundleGenerationAt = 2;

    // version, suite, kdf, iterations (4), salt (16), nonce (12), then at least a tag.
    private const int BackupHeaderBytes = 3 + 4 + 16 + 12;
    private const int TagBytes = 16;

    // The bytes of a base64url value (without padding), or null when it is not one or would be longer than "maxBytes". Checked
    // before decoding, because decoding something that is not base64url throws and a request must not be able to make it.
    public static byte[]? Decode(string? value, int maxBytes)
    {
        if (value is null || !Base64Url.IsValid(value, out var length) || length > maxBytes)
            return null;

        return Base64Url.DecodeFromChars(value);
    }

    public static string Encode(byte[] bytes) => Base64Url.EncodeToString(bytes);

    // A bundle of the right size and version, and the generation it is for. Which generations are acceptable is for the caller to say.
    public static bool IsBundle(byte[] bundle, out int generation)
    {
        generation = 0;
        if (bundle.Length != BundleBytes || bundle[0] != Version || bundle[1] != Suite)
            return false;

        generation = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bundle.AsSpan(BundleGenerationAt));
        return true;
    }

    // An envelope of the right size and version. What is inside can only be known to the member it is sealed for.
    public static bool IsEnvelope(byte[] envelope) =>
        envelope.Length == EnvelopeBytes && envelope[0] == Version && envelope[1] == Suite;

    // A backup of a known version with a key derivation this server recognises and some ciphertext.
    public static bool IsBackup(byte[] blob, int maxBytes) =>
        blob.Length > BackupHeaderBytes + TagBytes && blob.Length <= maxBytes &&
        blob[0] == Version && blob[1] == Suite && blob[2] == Pbkdf2Sha256;
}
