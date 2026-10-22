using ChatNET.API.Media;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Messaging.SendMessage;

public class SendMessageCommandValidator : AbstractValidator<SendMessageCommand>
{
    public const int MaxContentLength = 4000;

    // The longest key an upload ticket can produce is far shorter; this only bounds what a client may send.
    private const int MaxUploadKeyLength = 200;

    public SendMessageCommandValidator(IOptions<StorageOptions> options)
    {
        var maxFiles = options.Value.MaxFilesPerMessage;

        // A message needs something to say or a file to show. NotEmpty also rejects a string of only
        // whitespace, so a blank message cannot be sent. Length counts UTF-16 code units, which is how .NET and
        // JavaScript both count a string.
        RuleFor(x => x.Content)
            .NotEmpty()
            .When(x => x.Attachments is not { Count: > 0 });

        RuleFor(x => x.Content)
            .MaximumLength(MaxContentLength);

        RuleFor(x => x.ClientMessageId)
            .NotEqual(Guid.Empty)
            .When(x => x.ClientMessageId.HasValue);

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
