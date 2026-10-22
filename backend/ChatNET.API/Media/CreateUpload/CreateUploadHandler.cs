using ChatNET.API.Auth.Models;
using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations;
using ChatNET.API.Conversations.Persistence;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace ChatNET.API.Media.CreateUpload;

public class CreateUploadHandler : IRequestHandler<CreateUploadCommand, UploadTicketDto>
{
    private readonly IConversationRepository _conversations;
    private readonly UserManager<AppUser> _userManager;
    private readonly IBlobStorage _storage;
    private readonly ILogger<CreateUploadHandler> _logger;

    public CreateUploadHandler(
        IConversationRepository conversations,
        UserManager<AppUser> userManager,
        IBlobStorage storage,
        ILogger<CreateUploadHandler> logger)
    {
        _conversations = conversations;
        _userManager = userManager;
        _storage = storage;
        _logger = logger;
    }

    public async Task<UploadTicketDto> Handle(CreateUploadCommand request, CancellationToken cancellationToken)
    {
        if (!await _conversations.IsActiveMemberAsync(request.ConversationId, request.UserId, cancellationToken))
        {
            _logger.LogWarning(
                "User {UserId} asked to upload to conversation {ConversationId} without being an active member",
                request.UserId, request.ConversationId);

            throw new ForbiddenException(ConversationErrors.NotAMember);
        }

        // As when sending: a token outlives the account it was issued for, and an upload costs storage.
        var user = await _userManager.FindByIdAsync(request.UserId.ToString());
        if (user is null || user.IsDeactivated)
            throw new ForbiddenException("This account can no longer send messages.");

        // The type that is signed is the one the options list, in the form the validator matched it to. The
        // browser has to send exactly this content type, or the storage refuses the upload.
        var contentType = AttachmentKinds.Normalize(request.ContentType!);
        var uploadKey = AttachmentKeys.NewUploadKey(request.ConversationId, request.UserId);
        var link = _storage.CreateUploadUrl(uploadKey, contentType);

        _logger.LogInformation(
            "User {UserId} was given an upload for conversation {ConversationId}", request.UserId, request.ConversationId);

        return new UploadTicketDto(uploadKey, link.Url, link.ExpiresAt);
    }
}
