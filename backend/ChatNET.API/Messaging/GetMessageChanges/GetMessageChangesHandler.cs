using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Media;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Messaging.Persistence;
using MediatR;

namespace ChatNET.API.Messaging.GetMessageChanges;

// What changed in a conversation since a point, for a client that keeps the messages it has read (a local store, which search runs
// over): every message sent while the caller was a member that was created, edited or deleted after the point, in the order the
// changes were committed. See Conversation.ChangeSeq for why that order can be followed by a cursor without missing anything.
public class GetMessageChangesHandler : IRequestHandler<GetMessageChangesQuery, MessageChangesDto>
{
    public const int PageSize = 100;
    public const string CursorAhead = "This point is past the newest change the server has. Read the conversation again from the start.";

    private readonly IConversationRepository _conversations;
    private readonly IMessageRepository _messages;
    private readonly AttachmentLoader _attachments;
    private readonly ILogger<GetMessageChangesHandler> _logger;

    public GetMessageChangesHandler(
        IConversationRepository conversations,
        IMessageRepository messages,
        AttachmentLoader attachments,
        ILogger<GetMessageChangesHandler> logger)
    {
        _conversations = conversations;
        _messages = messages;
        _attachments = attachments;
        _logger = logger;
    }

    public async Task<MessageChangesDto> Handle(GetMessageChangesQuery request, CancellationToken cancellationToken)
    {
        // The same rule as the history: only a member reads, and only what was sent while they were one.
        var window = await _conversations.GetMemberWindowAsync(request.ConversationId, request.UserId, cancellationToken);
        if (window is null)
        {
            _logger.LogWarning(
                "User {UserId} tried to read the changes of conversation {ConversationId} without being a member",
                request.UserId, request.ConversationId);

            throw new ForbiddenException(ConversationErrors.NotAMember);
        }

        var since = request.Since!.Value;

        // The newest number is read first, and the changes are read up to it and no further. Every number up to a counter that has
        // been read belongs to a transaction that committed (the next number cannot be taken before the previous change commits), so
        // the cursor handed back is never past a change that is still to appear. Reading the changes first and the counter after
        // would not have this: a change committed in between would be skipped by a client that continued from the counter.
        var latest = await _messages.GetChangeSeqAsync(request.ConversationId, cancellationToken)
            ?? throw new ForbiddenException(ConversationErrors.NotAMember);

        // A client cannot hold a number the server has not issued, unless the server was restored from an older state. Whatever it
        // keeps for this conversation is then not to be trusted, and it must say so rather than be told "nothing new".
        if (since > latest)
            throw new ConflictException(CursorAhead, MessageCodes.ChangesCursorAhead);

        // One more than a page, to know whether there is another.
        var found = await _messages.GetChangesAsync(request.ConversationId, window, since, latest, PageSize + 1, cancellationToken);

        var more = found.Count > PageSize;
        var page = more ? found[..PageSize] : found;
        var cursor = more ? page[^1].ChangeSeq : latest;

        return new MessageChangesDto(await _attachments.AttachToAsync(page, cancellationToken), cursor, more);
    }
}
