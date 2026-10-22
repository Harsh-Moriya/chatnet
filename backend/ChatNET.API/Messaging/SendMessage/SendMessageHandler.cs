using ChatNET.API.Auth.Models;
using ChatNET.API.Common.Exceptions;
using ChatNET.API.Common.Time;
using ChatNET.API.Conversations;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Media;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Messaging.Persistence;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;

namespace ChatNET.API.Messaging.SendMessage;

public class SendMessageHandler : IRequestHandler<SendMessageCommand, SendMessageResult>
{
    private readonly IConversationRepository _conversations;
    private readonly IMessageRepository _messages;
    private readonly UserManager<AppUser> _userManager;
    private readonly AttachmentIntake _intake;
    private readonly AttachmentLoader _attachmentLoader;
    private readonly IHubContext<ChatHub, IChatClient> _hub;
    private readonly ILogger<SendMessageHandler> _logger;

    public SendMessageHandler(
        IConversationRepository conversations,
        IMessageRepository messages,
        UserManager<AppUser> userManager,
        AttachmentIntake intake,
        AttachmentLoader attachmentLoader,
        IHubContext<ChatHub, IChatClient> hub,
        ILogger<SendMessageHandler> logger)
    {
        _conversations = conversations;
        _messages = messages;
        _userManager = userManager;
        _intake = intake;
        _attachmentLoader = attachmentLoader;
        _hub = hub;
        _logger = logger;
    }

    public async Task<SendMessageResult> Handle(SendMessageCommand request, CancellationToken cancellationToken)
    {
        if (!await _conversations.IsActiveMemberAsync(request.ConversationId, request.SenderId, cancellationToken))
        {
            _logger.LogWarning(
                "User {UserId} tried to send to conversation {ConversationId} without being an active member",
                request.SenderId, request.ConversationId);

            throw new ForbiddenException(ConversationErrors.NotAMember);
        }

        // A token stays valid until it expires, even if the account was deactivated after it was
        // issued. Writing content other people read is where that matters, so it is checked here.
        // The user is loaded anyway: the message carries the sender's display name.
        var sender = await _userManager.FindByIdAsync(request.SenderId.ToString());
        if (sender is null || sender.IsDeactivated)
            throw new ForbiddenException("This account can no longer send messages.");

        // The same send arriving again (the client timed out and tried once more, although the first one had
        // reached the server) carries the client id it used the first time. Checked after the membership and
        // account checks, so someone who has since left, or been deactivated, is refused as for any send.
        if (request.ClientMessageId is { } clientMessageId)
        {
            var earlier = await _messages.FindByClientMessageIdAsync(
                request.ConversationId, request.SenderId, clientMessageId, cancellationToken);

            if (earlier is not null)
                return await RepeatAsync(earlier, cancellationToken);
        }

        var createdAt = DateTime.UtcNow.TruncateToMicroseconds();

        // The files the message names are checked and copied to their permanent place before anything is saved,
        // so a message is never stored with a file that is not there.
        StagedAttachments? staged = null;
        if (request.Attachments is { Count: > 0 } files)
        {
            try
            {
                staged = await _intake.StageAsync(
                    request.ConversationId, request.SenderId, files, createdAt, cancellationToken);
            }
            catch (UploadMissingException) when (request.ClientMessageId is not null)
            {
                // Two copies of one send arriving together: the other attached these files and removed the
                // uploads while this one was looking for them. Its message is the answer.
                var stored = await _messages.FindByClientMessageIdAsync(
                    request.ConversationId, request.SenderId, request.ClientMessageId!.Value, cancellationToken);

                if (stored is not null)
                    return await RepeatAsync(stored, cancellationToken);

                throw;
            }
        }

        var message = new Message
        {
            ConversationId = request.ConversationId,
            SenderId = request.SenderId,

            // Empty, not null, for a message of only files: null is how a deleted message is told.
            Content = request.Content?.Trim() ?? string.Empty,
            CreatedAt = createdAt,
            ClientMessageId = request.ClientMessageId,
        };
        if (staged is not null)
            message.Attachments = staged.Attachments;

        // Two copies of one send can arrive together, both find nothing above, and both insert. The unique
        // index lets one in; the other is told so here, and answers with the winner's message. The files this
        // copy made are of no use to anyone then, and are removed, as they are when the save fails.
        bool added;
        try
        {
            added = await _messages.TryAddAsync(message, cancellationToken);
        }
        catch
        {
            if (staged is not null)
                await _intake.DiscardAsync(staged.Attachments);

            throw;
        }

        if (!added)
        {
            if (staged is not null)
                await _intake.DiscardAsync(staged.Attachments);

            var winner = await _messages.FindByClientMessageIdAsync(
                request.ConversationId, request.SenderId, request.ClientMessageId!.Value, cancellationToken);

            return await RepeatAsync(
                winner ?? throw new InvalidOperationException("A send was refused as a repeat but the first one cannot be found."),
                cancellationToken);
        }

        // The message is stored and holds its own copies; what was uploaded for it is no longer needed.
        if (staged is not null)
            await _intake.ReleaseAsync(staged.PendingKeys);

        _logger.LogInformation(
            "Message {MessageId} sent to conversation {ConversationId} by {UserId} with {AttachmentCount} attachments",
            message.Id, message.ConversationId, message.SenderId, message.Attachments.Count);

        var dto = new MessageDto(
            message.Id, message.ConversationId, message.SenderId, sender.DisplayName,
            message.Content, message.CreatedAt, request.ClientMessageId,
            message.Attachments.Select(_attachmentLoader.ToDto).ToList());

        await PushAsync(dto);

        return new SendMessageResult(dto, Created: true);
    }

    // The answer to a send that was already stored: the stored message, not a second one. It is pushed again
    // because the first attempt may have failed at exactly that step (the push is best effort), and a
    // retry is the one moment the server learns of it. A client that already has the message merges the
    // second copy by id, so repeating the push is harmless. The links to its files are new ones.
    private async Task<SendMessageResult> RepeatAsync(MessageDto stored, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "A repeat of message {MessageId} in conversation {ConversationId} by {UserId} returned the stored message",
            stored.Id, stored.ConversationId, stored.SenderId);

        var withFiles = (await _attachmentLoader.AttachToAsync([stored], cancellationToken))[0];

        await PushAsync(withFiles);

        return new SendMessageResult(withFiles, Created: false);
    }

    // Runs only after the message is saved, so a client never hears about a message that was rolled
    // back. The group includes the sender's own connections: their sending tab uses the push to
    // confirm its optimistic copy (through ClientMessageId) and their other tabs have no other way
    // to learn about the message.
    //
    // The data is already committed, so a failing push (Redis down, for example) must not turn a
    // successful send into an error: the client would retry and send the message twice. Members
    // still get it from history on their next load or reconnect.
    private async Task PushAsync(MessageDto message)
    {
        try
        {
            await _hub.Clients.Group(ChatGroups.Conversation(message.ConversationId)).ReceiveMessage(message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Could not push message {MessageId} to conversation {ConversationId}; members will see it on their next history load",
                message.Id, message.ConversationId);
        }
    }
}
