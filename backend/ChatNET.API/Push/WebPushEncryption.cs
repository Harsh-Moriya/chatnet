using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ChatNET.API.Push;

// The encryption of a push message to the browser it is for: RFC 8291 (Web Push), which uses the record format of RFC 8188 with
// the content coding "aes128gcm". The push service relays the result without being able to read it.
//
// The sender makes a key pair of its own for every message. The key agreed between it and the browser's public key is mixed with
// the secret the browser shared when it subscribed (so only that subscription can have been the one the message was meant for),
// and from that a key and a nonce are derived for AES-128-GCM. The message is one record: the text, then the byte 0x02 that marks
// the last record. What is sent is a header (the salt, the record size, and the sender's public key, so the browser can do the same
// agreement) followed by the encrypted record.
public static class WebPushEncryption
{
    public const int RecordSize = 4096;

    private const int SaltLength = 16;
    private const int PublicKeyLength = 65;
    private const int TagLength = 16;
    private const int HeaderLength = SaltLength + 4 + 1 + PublicKeyLength;

    // The whole message, header and tag included, has to fit in 4096 bytes, which is what the push services take.
    public const int MaxPlaintextLength = RecordSize - HeaderLength - TagLength - 1;

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> browserPublicKey, ReadOnlySpan<byte> authSecret)
    {
        using var senderKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Encrypt(plaintext, browserPublicKey, authSecret, senderKey, RandomNumberGenerator.GetBytes(SaltLength));
    }

    // The same with the sender's key and the salt given, which makes the result a function of its inputs. This is what the example
    // of RFC 8291 is checked against.
    public static byte[] Encrypt(
        ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> browserPublicKey, ReadOnlySpan<byte> authSecret,
        ECDiffieHellman senderKey, ReadOnlySpan<byte> salt)
    {
        if (plaintext.Length > MaxPlaintextLength)
            throw new ArgumentException($"A push can carry at most {MaxPlaintextLength} bytes.", nameof(plaintext));

        if (browserPublicKey.Length != PublicKeyLength || browserPublicKey[0] != 0x04)
            throw new ArgumentException("The browser's public key is not an uncompressed P-256 point.", nameof(browserPublicKey));

        if (authSecret.Length != 16)
            throw new ArgumentException("The authentication secret is not 16 bytes.", nameof(authSecret));

        if (salt.Length != SaltLength)
            throw new ArgumentException("The salt is not 16 bytes.", nameof(salt));

        var senderPublicKey = PublicPoint(senderKey);

        // An invalid point (one that is not on the curve) is refused by the platform when the key is made.
        using var browserKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = browserPublicKey[1..33].ToArray(), Y = browserPublicKey[33..65].ToArray() },
        });
        var shared = senderKey.DeriveRawSecretAgreement(browserKey.PublicKey);

        // Two rounds of HKDF: the first binds the agreed secret to this subscription's authentication secret and to both public
        // keys, the second turns that and the salt into the key and the nonce.
        var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), browserPublicKey.ToArray(), senderPublicKey);
        var ikm = HKDF.Expand(
            HashAlgorithmName.SHA256, HKDF.Extract(HashAlgorithmName.SHA256, shared, authSecret.ToArray()), 32, keyInfo);

        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt.ToArray());
        var key = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        var record = new byte[plaintext.Length + 1];
        plaintext.CopyTo(record);
        record[^1] = 0x02;

        var message = new byte[HeaderLength + record.Length + TagLength];
        salt.CopyTo(message);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(SaltLength, 4), RecordSize);
        message[SaltLength + 4] = PublicKeyLength;
        senderPublicKey.CopyTo(message, SaltLength + 5);

        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(
            nonce, record, message.AsSpan(HeaderLength, record.Length), message.AsSpan(HeaderLength + record.Length, TagLength));

        return message;
    }

    // 0x04, then the two coordinates of 32 bytes each.
    private static byte[] PublicPoint(ECDiffieHellman key)
    {
        var point = key.ExportParameters(includePrivateParameters: false).Q;
        return Concat([0x04], point.X!, point.Y!);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result.AsSpan(offset));
            offset += part.Length;
        }

        return result;
    }
}
