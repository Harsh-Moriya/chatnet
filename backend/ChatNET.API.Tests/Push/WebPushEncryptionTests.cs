using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using ChatNET.API.Push;
using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Push;

// The encryption is the part of push that nobody can look at in a log, and a browser that cannot read a push shows nothing and says
// nothing. So the first test is the example that RFC 8291 works through (section 5 and Appendix A): fixed keys, secret and salt in,
// the very message of the RFC out. It is written by someone else, which is the point; the others check the shape of what is made.
public class WebPushEncryptionTests
{
    private static byte[] D(string base64Url) => Base64Url.DecodeFromChars(base64Url);

    // ##### The example of the RFC #####

    [Fact]
    public void The_example_of_RFC_8291_encrypts_to_the_message_of_the_RFC()
    {
        var plaintext = Encoding.ASCII.GetBytes("When I grow up, I want to be a watermelon");
        var browserPublicKey = D("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4");
        var authSecret = D("BTBZMqHH6r4Tts7J_aSIgg");
        var salt = D("DGv6ra1nlYgDCS1FRnbzlw");

        var senderPublic = D("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8");
        using var senderKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = D("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw"),
            Q = new ECPoint { X = senderPublic[1..33], Y = senderPublic[33..65] },
        });

        var message = WebPushEncryption.Encrypt(plaintext, browserPublicKey, authSecret, senderKey, salt);

        Assert.Equal(
            "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A"
            + "_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN",
            Base64Url.EncodeToString(message));
    }

    // ##### The shape of a message #####

    [Fact]
    public void The_message_starts_with_the_salt_the_record_size_and_the_public_key_of_the_sender()
    {
        var browser = Browser.Subscribe();

        var message = WebPushEncryption.Encrypt("hello"u8, Base64Url.DecodeFromChars(browser.P256dh), browser.AuthSecret);

        Assert.Equal(16, message[..16].Length);
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(16, 4)));
        Assert.Equal(65, message[20]);
        Assert.Equal(0x04, message[21]);

        // The header (86 bytes), the text and the byte that marks the last record, the tag of the cipher.
        Assert.Equal(86 + "hello".Length + 1 + 16, message.Length);
    }

    [Fact]
    public void Every_message_has_a_key_and_a_salt_of_its_own()
    {
        var browser = Browser.Subscribe();
        var key = Base64Url.DecodeFromChars(browser.P256dh);

        var first = WebPushEncryption.Encrypt("hello"u8, key, browser.AuthSecret);
        var second = WebPushEncryption.Encrypt("hello"u8, key, browser.AuthSecret);

        Assert.NotEqual(first[..16], second[..16]);
        Assert.NotEqual(first[21..86], second[21..86]);
        Assert.NotEqual(first[86..], second[86..]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(1500)]
    [InlineData(WebPushEncryption.MaxPlaintextLength)]
    public void A_browser_reads_back_what_was_sent_whatever_its_length(int length)
    {
        var browser = Browser.Subscribe();
        var text = new string('a', length);

        var message = WebPushEncryption.Encrypt(
            Encoding.UTF8.GetBytes(text), Base64Url.DecodeFromChars(browser.P256dh), browser.AuthSecret);

        Assert.Equal(text, WebPushDecryption.Decrypt(message, browser));
    }

    // 4096 is what the push services take for a whole message, header and tag included.
    [Fact]
    public void The_longest_text_makes_a_message_of_exactly_4096_bytes_and_one_more_byte_is_refused()
    {
        var browser = Browser.Subscribe();
        var key = Base64Url.DecodeFromChars(browser.P256dh);

        var longest = WebPushEncryption.Encrypt(new byte[WebPushEncryption.MaxPlaintextLength], key, browser.AuthSecret);

        Assert.Equal(4096, longest.Length);
        Assert.Throws<ArgumentException>(
            () => WebPushEncryption.Encrypt(new byte[WebPushEncryption.MaxPlaintextLength + 1], key, browser.AuthSecret));
    }

    [Fact]
    public void A_browser_with_other_keys_or_another_secret_cannot_read_it()
    {
        var browser = Browser.Subscribe();
        var impostor = Browser.Subscribe();
        var message = WebPushEncryption.Encrypt("hello"u8, Base64Url.DecodeFromChars(browser.P256dh), browser.AuthSecret);

        Assert.ThrowsAny<CryptographicException>(() => WebPushDecryption.Decrypt(message, impostor));
    }

    // The secret is shared when the browser subscribes, so a message made with another one is one the browser did not ask for.
    [Fact]
    public void The_authentication_secret_is_part_of_the_key()
    {
        var browser = Browser.Subscribe();
        var message = WebPushEncryption.Encrypt(
            "hello"u8, Base64Url.DecodeFromChars(browser.P256dh), RandomNumberGenerator.GetBytes(16));

        Assert.ThrowsAny<CryptographicException>(() => WebPushDecryption.Decrypt(message, browser));
    }

    // ##### What is refused #####

    [Fact]
    public void A_public_key_that_is_not_a_point_on_the_curve_is_refused()
    {
        var browser = Browser.Subscribe();
        byte[] notAPoint = [0x04, .. new byte[64]];

        // Windows reports an invalid point as PlatformNotSupportedException, other systems as CryptographicException.
        var failure = Record.Exception(() => WebPushEncryption.Encrypt("hello"u8, notAPoint, browser.AuthSecret));

        Assert.True(failure is CryptographicException or PlatformNotSupportedException, failure?.ToString());
    }

    [Theory]
    [InlineData(64, 0x04, 16, 16, "a public key that is one byte short")]
    [InlineData(66, 0x04, 16, 16, "a public key that is one byte long")]
    [InlineData(65, 0x02, 16, 16, "a compressed public key")]
    [InlineData(65, 0x04, 15, 16, "a secret that is one byte short")]
    [InlineData(65, 0x04, 17, 16, "a secret that is one byte long")]
    [InlineData(65, 0x04, 16, 15, "a salt that is one byte short")]
    [InlineData(65, 0x04, 16, 17, "a salt that is one byte long")]
    public void Input_of_the_wrong_size_or_kind_is_refused(int keyLength, byte keyPrefix, int secretLength, int saltLength, string what)
    {
        var browser = Browser.Subscribe();
        var key = Base64Url.DecodeFromChars(browser.P256dh);
        var wrongKey = key.Length == keyLength ? key : new byte[keyLength];
        wrongKey[0] = keyPrefix;
        using var senderKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var failure = Assert.Throws<ArgumentException>(
            () => WebPushEncryption.Encrypt("hello"u8, wrongKey, new byte[secretLength], senderKey, new byte[saltLength]));

        Assert.False(string.IsNullOrWhiteSpace(failure.Message), what);
    }
}
