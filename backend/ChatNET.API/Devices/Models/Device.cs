using ChatNET.API.Auth.Models;

namespace ChatNET.API.Devices.Models;

// What kind of client a device is. The server only records it, for the list a person sees of their devices.
public enum DeviceKind
{
    Web,
    Ios,
    Android,
}

// One browser or phone that holds the keys of a person. A device exists from the moment it has the keys (it made them, opened
// the backup, or was linked) until the person removes it or it signs out; removing it also ends the sessions it was signed in
// on. The row stays, with RemovedAt, so a device that is told it was removed can find that out.
//
// A device is not a login: signing in does not create one, and a browser that signs in without the keys is not a device yet.
public class Device
{
    // Made by the application, like the ids of messages, so it can be named before the row is saved.
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public DeviceKind Kind { get; set; }

    // What the person sees in their list of devices ("Chrome on Windows"). Chosen by the device, so it is text to display and
    // nothing more.
    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    // The last time a session of this device was renewed, so a device nobody has used for months stands out.
    public DateTime LastActiveAt { get; set; }

    public DateTime? RemovedAt { get; set; }

    public AppUser User { get; set; } = null!;
}
