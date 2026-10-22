using MediatR;

namespace ChatNET.API.Media.GetAttachmentUrl;

public record GetAttachmentUrlQuery(Guid AttachmentId, Guid UserId) : IRequest<AttachmentLinkDto>;

// A new link to a file, valid until ExpiresAt.
public record AttachmentLinkDto(string Url, DateTimeOffset ExpiresAt);
