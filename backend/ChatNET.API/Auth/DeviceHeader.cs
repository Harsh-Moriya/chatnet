namespace ChatNET.API.Auth;

// The header a browser that holds the encryption keys sends when it signs in and when it renews its session, naming its device.
// It is how a session is tied to a device, so removing the device ends the session. A value that is not an id, or names a device
// that is not the person's or was removed, is ignored: the session simply is not tied to anything.
public static class DeviceHeader
{
    public const string Name = "X-Device-Id";

    public static Guid? Read(HttpRequest request)
        => Guid.TryParse(request.Headers[Name].FirstOrDefault(), out var id) ? id : null;
}
