using ChatNET.API.Auth.Models;
using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Media.CreateUpload;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace ChatNET.API.Media.Avatars;

public class CreateAvatarUploadHandler : IRequestHandler<CreateAvatarUploadCommand, UploadTicketDto>
{
    private readonly IConversationRepository _conversations;
    private readonly UserManager<AppUser> _userManager;
    private readonly IBlobStorage _storage;
    private readonly ILogger<CreateAvatarUploadHandler> _logger;

    public CreateAvatarUploadHandler(
        IConversationRepository conversations,
        UserManager<AppUser> userManager,
        IBlobStorage storage,
        ILogger<CreateAvatarUploadHandler> logger)
    {
        _conversations = conversations;
        _userManager = userManager;
        _storage = storage;
        _logger = logger;
    }

    public async Task<UploadTicketDto> Handle(CreateAvatarUploadCommand request, CancellationToken cancellationToken)
    {
        if (request.Owner == AvatarOwner.Group)
            await RequireGroupManagerAsync(request, cancellationToken);

        // As when uploading a file: a token outlives the account it was issued for, and an upload costs storage.
        var user = await _userManager.FindByIdAsync(request.ActorId.ToString());
        if (user is null || user.IsDeactivated)
            throw new ForbiddenException("This account can no longer be changed.");

        // The type that is signed is the one the rules list, in the form the validator matched it to. The browser has to
        // send exactly this content type, or the storage refuses the upload.
        var contentType = AvatarRules.Match(request.ContentType!)!;
        var uploadKey = AvatarKeys.NewUploadKey(request.Owner, request.OwnerId);
        var link = _storage.CreateUploadUrl(uploadKey, contentType);

        _logger.LogInformation(
            "User {UserId} was given a picture upload for {Owner} {OwnerId}", request.ActorId, request.Owner, request.OwnerId);

        return new UploadTicketDto(uploadKey, link.Url, link.ExpiresAt);
    }

    // Only the owner or an admin of a group picks its picture. Anyone else, and a direct chat (whose members are all plain
    // members), gets a refusal that does not say whether the conversation exists.
    private async Task RequireGroupManagerAsync(CreateAvatarUploadCommand request, CancellationToken cancellationToken)
    {
        var role = await _conversations.GetActiveMemberRoleAsync(request.OwnerId, request.ActorId, cancellationToken)
            ?? throw new ForbiddenException(ConversationErrors.NotAMember);

        if (role is not (MemberRole.Owner or MemberRole.Admin))
            throw new ForbiddenException("Only the owner or an admin can change this group's picture.");
    }
}
