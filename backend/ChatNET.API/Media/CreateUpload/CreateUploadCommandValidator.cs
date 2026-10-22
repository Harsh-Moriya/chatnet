using FluentValidation;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Media.CreateUpload;

public class CreateUploadCommandValidator : AbstractValidator<CreateUploadCommand>
{
    public CreateUploadCommandValidator(IOptions<StorageOptions> options)
    {
        var storage = options.Value;

        // The name is only checked here; it is made harmless when the file is attached (AttachmentNames).
        RuleFor(x => x.FileName)
            .NotEmpty()
            .MaximumLength(AttachmentNames.MaxLength);

        // Two rules, because a condition at the end of a chain applies to all of it: with the type missing,
        // the one rule would be skipped altogether and nothing would refuse the request.
        RuleFor(x => x.ContentType)
            .NotEmpty();

        RuleFor(x => x.ContentType)
            .Must(type => storage.Allows(type!))
            .WithMessage("This type of file cannot be sent.")
            .When(x => !string.IsNullOrWhiteSpace(x.ContentType));

        RuleFor(x => x.SizeBytes)
            .GreaterThan(0)
            .WithMessage("The file is empty.");

        RuleFor(x => x.SizeBytes)
            .LessThanOrEqualTo(storage.MaxFileBytes)
            .WithMessage("The file is larger than the limit.");
    }
}
