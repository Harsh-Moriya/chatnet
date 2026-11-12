using FluentValidation;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Media.CreateUpload;

public class CreateUploadCommandValidator : AbstractValidator<CreateUploadCommand>
{
    public CreateUploadCommandValidator(IOptions<StorageOptions> options)
    {
        var largest = SealedFile.MaxBytes(options.Value.MaxFileBytes);

        RuleFor(x => x.SizeBytes)
            .GreaterThanOrEqualTo(SealedFile.MinBytes)
            .WithMessage("The file is too small to be a sealed file.");

        RuleFor(x => x.SizeBytes)
            .LessThanOrEqualTo(largest)
            .WithMessage("The file is larger than the limit.");
    }
}
