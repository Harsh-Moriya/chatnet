using ChatNET.API.Devices.Models;
using MediatR;

namespace ChatNET.API.Devices.Linking.CreateLinkRequest;

// A new device asks to be given the keys. TemporaryPublicKey (base64url, 32 bytes) is the half of a one-time key pair the
// secrets will be sealed to, and Code is the ten character commitment to it that the device shows.
public record CreateLinkRequestCommand(Guid UserId, string? TemporaryPublicKey, string? Code, string? Kind, string? Name)
    : IRequest<LinkRequestCreatedDto>;
