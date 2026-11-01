using ChatNET.API.Media.CreateUpload;
using MediatR;

namespace ChatNET.API.Media.Avatars;

// Whose picture is meant: a person's own (OwnerId is the caller) or a group's (OwnerId is the conversation). ActorId comes from
// the token, never from the request body. The strings are null only when the request left them out, which the validators
// refuse.

// Permission to upload one picture. The picture itself goes straight to the bucket, to the address in the answer.
public record CreateAvatarUploadCommand(
    AvatarOwner Owner, Guid OwnerId, Guid ActorId, string? ContentType, long SizeBytes) : IRequest<UploadTicketDto>;

// Makes an upload the picture.
public record SetAvatarCommand(AvatarOwner Owner, Guid OwnerId, Guid ActorId, string? UploadKey) : IRequest<AvatarDto>;

// Takes the picture away, so initials (or the group icon) show again.
public record RemoveAvatarCommand(AvatarOwner Owner, Guid OwnerId, Guid ActorId) : IRequest<AvatarDto>;

// The address of the picture after the change, null when there is none.
public record AvatarDto(string? AvatarUrl);
