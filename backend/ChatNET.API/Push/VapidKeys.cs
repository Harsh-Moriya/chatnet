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

    // A key that signs with the private key, after proving that the two belong together: something is signed with one and
    // checked with the other. A public key that was copied from another deployment would otherwise only show as every push
    // being refused by the push services.
    public static ECDsa CreateSigner(string publicKey, string privateKey)
    {
        var point = DecodePublicKey(publicKey);
        var signer = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = DecodePrivateKey(privateKey),
            Q = new ECPoint { X = point[1..33], Y = point[33..65] },
        });

        try
        {
            using var verifier = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = point[1..33], Y = point[33..65] },
            });

            byte[] proof = [1, 2, 3];
            if (!verifier.VerifyData(proof, signer.SignData(proof, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256))
                throw new ArgumentException("The public key is not the public half of the private key.");

            return signer;
        }
        catch
        {
            signer.Dispose();
            throw;
        }
    }

    private static byte[] Decode(string value) =>
        Base64Url.IsValid(value) ? Base64Url.DecodeFromChars(value) : throw new FormatException("It is not base64url.");
}
