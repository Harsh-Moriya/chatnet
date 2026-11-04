using System.Buffers.Text;
using System.Security.Cryptography;
using ChatNET.API.Push;
using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Push;

// The key pair that identifies the server to the push services, and the tokens signed with it. A pair that does not belong together
// is caught when the host starts; a token that a push service cannot verify makes every push fail without saying why.
public class VapidTests
{
    private static readonly VapidSettings Keys = PushApi.Vapid;

    // ##### The keys #####

    [Fact]
    public void A_pair_that_belongs_together_makes_a_signer_whose_signatures_the_public_key_verifies()
    {
        using var signer = VapidKeys.CreateSigner(Keys.PublicKey, Keys.PrivateKey);
        var point = Base64Url.DecodeFromChars(Keys.PublicKey);
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..65] },
        });

        var signature = signer.SignData("hello"u8, HashAlgorithmName.SHA256);

        Assert.True(verifier.VerifyData("hello"u8, signature, HashAlgorithmName.SHA256));
    }

    [Fact]
    public void A_public_key_that_is_not_the_half_of_the_private_key_is_refused()
    {
        var other = PushApiMakeKeys();

        var failure = Assert.Throws<ArgumentException>(() => VapidKeys.CreateSigner(other.PublicKey, Keys.PrivateKey));

        Assert.Contains("public half of the private key", failure.Message);
        Assert.DoesNotContain(Keys.PrivateKey, failure.Message);
    }

    // Well formed (65 bytes, the first of them 0x04) and still not a point on the curve.
    [Fact]
    public void A_public_key_that_is_not_a_point_on_the_curve_is_refused()
    {
        var notAPoint = Base64Url.EncodeToString([0x04, .. new byte[64]]);

        Assert.Throws<ArgumentException>(() => VapidKeys.CreateSigner(notAPoint, Keys.PrivateKey));
    }

    [Theory]
    [InlineData("not base64url!")]
    [InlineData("AAAA")]
    [InlineData("")]
    public void A_public_key_that_is_malformed_is_refused_without_repeating_it(string key)
    {
        var failure = Assert.ThrowsAny<Exception>(() => VapidKeys.DecodePublicKey(key));

        Assert.True(failure is ArgumentException or FormatException);
        if (key.Length > 3)
            Assert.DoesNotContain(key, failure.Message);
    }

    [Fact]
    public void A_public_key_that_is_a_compressed_point_is_refused()
    {
        var compressed = Base64Url.EncodeToString([0x02, .. new byte[64]]);

        Assert.Throws<ArgumentException>(() => VapidKeys.DecodePublicKey(compressed));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void A_private_key_that_is_not_32_bytes_is_refused(int length)
    {
        Assert.Throws<ArgumentException>(() => VapidKeys.DecodePrivateKey(Base64Url.EncodeToString(new byte[length])));
    }

    // ##### The tokens #####

    [Fact]
    public void A_token_names_the_origin_of_the_endpoint_and_nothing_after_it()
    {
        using var signer = new VapidSigner(Keys.PublicKey, Keys.PrivateKey, Keys.Subject, TimeProvider.System);

        var header = signer.AuthorizationFor(new Uri("https://updates.push.services.mozilla.com/wpush/v2/the-capability?x=1"));

        var token = VapidToken.Read($"{header.Scheme} {header.Parameter}");
        Assert.Equal("https://updates.push.services.mozilla.com", token.Claims.GetProperty("aud").GetString());
        Assert.DoesNotContain("the-capability", header.Parameter);
    }

    // The sender uses one signer from many tasks at once, and a signature that went wrong would be refused by the push service.
    [Fact]
    public async Task Signing_from_many_tasks_at_once_gives_valid_signatures()
    {
        using var signer = new VapidSigner(Keys.PublicKey, Keys.PrivateKey, Keys.Subject, TimeProvider.System);
        var endpoint = new Uri("https://fcm.googleapis.com/fcm/send/abc");

        var tokens = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            var header = signer.AuthorizationFor(endpoint);
            return VapidToken.Read($"{header.Scheme} {header.Parameter}");
        })));

        Assert.All(tokens, token => Assert.True(token.SignatureIsValid));
    }

    private static VapidSettings PushApiMakeKeys()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: true);

        return new VapidSettings(
            Base64Url.EncodeToString([0x04, .. parameters.Q.X!, .. parameters.Q.Y!]),
            Base64Url.EncodeToString(parameters.D!),
            Keys.Subject);
    }
}
