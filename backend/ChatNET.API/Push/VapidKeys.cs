using System.Buffers.Text;
using System.Security.Cryptography;

namespace ChatNET.API.Push;

// The key pair that identifies this server to the push services (VAPID, RFC 8292): an ECDSA key on P-256, written the way the
// browsers' push API takes it, in base64url. The public key is the uncompressed point (65 bytes, the first of them 0x04), the
// private key the 32 byte scalar. None of the failures here repeats a key: two of the values handled are secrets.
public static class VapidKeys
{
    private const int PublicKeyLength = 65;
    private const int PrivateKeyLength = 32;

    // The 65 bytes of the public key, or an exception that says what is wrong with it.
    public static byte[] DecodePublicKey(string publicKey)
    {
        var bytes = Decode(publicKey);
        if (bytes.Length != PublicKeyLength || bytes[0] != 0x04)
            throw new ArgumentException("It must be an uncompressed P-256 point: 65 bytes, base64url.");

        return bytes;
    }

    public static byte[] DecodePrivateKey(string privateKey)
    {
        var bytes = Decode(privateKey);
        if (bytes.Length != PrivateKeyLength)
            throw new ArgumentException("It must be a 32 byte P-256 private key, base64url.");

        return bytes;
    }

    private const string NotAPair = "The public key is not the public half of the private key, or is not a point on the curve.";

    // A key that signs with the private key, after the platform has checked the pair. Importing both makes it refuse a public key that
    // belongs to another private key (Windows and OpenSSL both do, which was tried), and importing the public key alone makes it
    // refuse a point that is not on the curve, which Windows did not when the private key came with it (an all-zero public key was
    // taken without a word). A public key copied from another deployment would otherwise only show as every push being refused by
    // the push services, and the platforms' own messages for these ("the requested operation is not supported") say nothing, so
    // the failure is turned into one that does.
    public static ECDsa CreateSigner(string publicKey, string privateKey)
    {
        var point = DecodePublicKey(publicKey);
        var q = new ECPoint { X = point[1..33], Y = point[33..65] };
        var d = DecodePrivateKey(privateKey);

        ECDsa? signer = null;
        try
        {
            signer = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d, Q = q });
            using var onCurve = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = q });

            return signer;
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            signer?.Dispose();
            throw new ArgumentException(NotAPair);
        }
    }

    private static byte[] Decode(string value) =>
        Base64Url.IsValid(value) ? Base64Url.DecodeFromChars(value) : throw new FormatException("It is not base64url.");
}
