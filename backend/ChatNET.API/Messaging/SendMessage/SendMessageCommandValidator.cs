using ChatNET.API.Media;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Messaging.SendMessage;

public class SendMessageCommandValidator : AbstractValidator<SendMessageCommand>
{
    // The longest key an upload ticket can produce is far shorter; this only bounds what a client may send.
    private const int MaxUploadKeyLength = 200;

    public SendMessageCommandValidator(IOptions<StorageOptions> options)
    {
        var maxFiles = options.Value.MaxFilesPerMessage;

        // The server cannot read the text, so it cannot tell a blank message from a long one: the browser keeps both within
        // limits before it seals, and the server insists only that what arrives is a message of a size and shape the formats allow.
        RuleFor(x => x.Ciphertext)
            .NotEmpty()
            .Must(ciphertext => MessageBlob.Decode(ciphertext) is not null)
            .WithMessage("The message is not a well formed encrypted message.");

        // The id is part of what the sender signed, so a message without one could not be opened by anyone.
        RuleFor(x => x.ClientMessageId)
            .NotNull()
            .NotEqual(Guid.Empty);

        RuleFor(x => x.MentionedUserIds)
            .Must(ids => ids is null || ids.Count <= MentionLimits.Max)
            .WithMessage($"A message can name at most {MentionLimits.Max} people.");

        RuleForEach(x => x.MentionedUserIds).NotEqual(Guid.Empty);

        RuleFor(x => x.Attachments)
            .Must(files => files is null || files.Count <= maxFiles)
            .WithMessage($"A message can have at most {maxFiles} files.");

        // The same upload twice would be two copies of one file, and the second would find the first one's
        // upload already gone.
        RuleFor(x => x.Attachments)
            .Must(files => files is null || files.Select(file => file?.UploadKey).Distinct().Count() == files.Count)
            .WithMessage("The same file cannot be attached twice.");

        RuleForEach(x => x.Attachments)
            .NotNull()
            .ChildRules(file =>
            {
                file.RuleFor(f => f.UploadKey).NotEmpty().MaximumLength(MaxUploadKeyLength);
                file.RuleFor(f => f.FileName).NotEmpty().MaximumLength(AttachmentNames.MaxLength);
            });
    }
}
