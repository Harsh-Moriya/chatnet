using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Messaging.Persistence;
using MediatR;

namespace ChatNET.API.Media.GetAttachmentUrl;

public class GetAttachmentUrlHandler : IRequestHandler<GetAttachmentUrlQuery, AttachmentLinkDto>
{
    private readonly IAttachmentRepository _attachments;
    private readonly IConversationRepository _conversations;
    private readonly IBlobStorage _storage;
    private readonly ILogger<GetAttachmentUrlHandler> _logger;

    public GetAttachmentUrlHandler(
        IAttachmentRepository attachments,
        IConversationRepository conversations,
        IBlobStorage storage,
        ILogger<GetAttachmentUrlHandler> logger)
    {
        _attachments = attachments;
        _conversations = conversations;
        _storage = storage;
        _logger = logger;
    }

    public async Task<AttachmentLinkDto> Handle(GetAttachmentUrlQuery request, CancellationToken cancellationToken)
    {
        var found = await _attachments.FindForReadAsync(request.AttachmentId, cancellationToken);

        // The same rule as for history: an active member, and only what was sent since they joined. A file of a
        // deleted message is gone for everyone. Every refusal is the same "not found", so an id says nothing
        // about files the caller may not read.
        var joinedAt = found is null
            ? null
            : await _conversations.GetActiveMemberJoinedAtAsync(found.ConversationId, request.UserId, cancellationToken);

        if (found is null || found.MessageDeleted || joinedAt is null || found.MessageCreatedAt < joinedAt)
        {
            _logger.LogWarning(
                "User {UserId} asked for a link to attachment {AttachmentId} that they may not read or that does not exist",
                request.UserId, request.AttachmentId);

            throw new NotFoundException("Attachment", request.AttachmentId);
        }

        var attachment = found.Attachment;
        var link = _storage.CreateDownloadUrl(
            attachment.StorageKey, attachment.FileName, inline: attachment.Type != AttachmentType.File);

        return new AttachmentLinkDto(link.Url, link.ExpiresAt);
    }
}
