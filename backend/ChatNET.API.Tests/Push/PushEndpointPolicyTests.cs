using ChatNET.API.Push;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Tests.Push;

// The server posts to the address a browser registers, so which addresses it may post to is a security rule (see
// PushEndpointPolicy). Each of the refusals below is a way someone would try to make it call a host of their choosing.
public class PushEndpointPolicyTests
{
    private static PushEndpointPolicy PolicyFor(params string[] hosts) =>
        new(Options.Create(new PushOptions { AllowedHosts = [.. hosts] }));

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc")]
    [InlineData("https://FCM.GoogleAPIs.com/fcm/send/abc")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/abc")]
    [InlineData("https://web.push.apple.com/abc")]
    [InlineData("https://wns2-par02p.notify.windows.com/w/?token=abc")]
    public void An_address_on_an_allowed_push_service_or_a_subdomain_of_one_passes(string endpoint)
    {
        var policy = PolicyFor("fcm.googleapis.com", "updates.push.services.mozilla.com", "push.apple.com", "notify.windows.com");

        Assert.True(policy.IsAllowed(endpoint));
    }

    [Theory]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc", "not https")]
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/abc", "another port")]
    [InlineData("https://fcm.googleapis.com@evil.example/fcm/send/abc", "a user name that makes another host the host")]
    [InlineData("https://fcm.googleapis.com:pass@evil.example/x", "a user name and password")]
    [InlineData("https://evil.example/fcm.googleapis.com", "the allowed name only in the path")]
    [InlineData("https://fcm.googleapis.com.evil.example/x", "the allowed name as the start of another host")]
    [InlineData("https://evilfcm.googleapis.com/x", "a host that only ends with the allowed text")]
    [InlineData("https://googleapis.com/x", "the parent of an allowed host")]
    [InlineData("https://127.0.0.1/x", "an address of this machine")]
    [InlineData("https://localhost/x", "localhost")]
    [InlineData("https://169.254.169.254/latest/meta-data", "the metadata address of a cloud host")]
    [InlineData("https://[::1]/x", "an IPv6 address of this machine")]
    [InlineData("ftp://fcm.googleapis.com/x", "another scheme")]
    [InlineData("//fcm.googleapis.com/x", "no scheme")]
    [InlineData("fcm.googleapis.com/x", "no scheme at all")]
    [InlineData("", "empty")]
    public void An_address_that_is_not_one_of_those_is_refused(string endpoint, string because)
    {
        var policy = PolicyFor("fcm.googleapis.com", "notify.windows.com");

        Assert.False(policy.IsAllowed(endpoint), because);
    }

    [Fact]
    public void Nothing_is_allowed_when_no_host_is_listed()
    {
        Assert.False(PolicyFor().IsAllowed("https://fcm.googleapis.com/fcm/send/abc"));
    }

    // The list is written by an operator, who may use capitals; the address is compared as a browser would read it.
    [Fact]
    public void The_list_is_not_case_sensitive()
    {
        Assert.True(PolicyFor("FCM.googleapis.com").IsAllowed("https://fcm.googleapis.com/fcm/send/abc"));
    }
}
