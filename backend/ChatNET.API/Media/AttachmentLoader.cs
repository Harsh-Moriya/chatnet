using ChatNET.API.Messaging.Models;
using ChatNET.API.Messaging.Persistence;

namespace ChatNET.API.Media;

// Puts the files of messages into the messages' DTOs, with a link signed for the reader. Signing is a local
// calculation, so a page of history costs one extra query for all of its files and no call to the storage.
public sealed class AttachmentLoader
{
    private readonly IAttachmentRepository _attachments;
    private readonly IBlobStorage _storage;

    public AttachmentLoader(IAttachmentRepository attachments, IBlobStorage storage)
    {
        _attachments = attachments;
        _storage = storage;
    }

    public AttachmentDto ToDto(MessageAttachment attachment)
        => new(
            attachment.Id,
            attachment.Type,
            attachment.FileName,
            attachment.ContentType,
            attachment.FileSizeBytes,
            _storage.CreateDownloadUrl(attachment.StorageKey, attachment.FileName, inline: attachment.Type != AttachmentType.File).Url);

    public async Task<List<MessageDto>> AttachToAsync(List<MessageDto> messages, CancellationToken cancellationToken)
    {
        // A deleted message (its content is null) shows no files.
        var ids = messages.Where(m => m.Content is not null).Select(m => m.Id).ToList();
        if (ids.Count == 0) return messages;

        var found = await _attachments.GetForMessagesAsync(ids, cancellationToken);
        if (found.Count == 0) return messages;

        var byMessage = found
            .GroupBy(a => a.MessageId)
            .ToDictionary(group => group.Key, group => group.Select(ToDto).ToList());

        return messages
            .Select(m => byMessage.TryGetValue(m.Id, out var files) ? m with { Attachments = files } : m)
            .ToList();
    }
}
