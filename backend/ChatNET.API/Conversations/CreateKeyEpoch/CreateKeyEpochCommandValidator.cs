using ChatNET.API.Conversations.KeyEpochs;
using FluentValidation;

namespace ChatNET.API.Conversations.CreateKeyEpoch;

public class CreateKeyEpochCommandValidator : AbstractValidator<CreateKeyEpochCommand>
{
    public CreateKeyEpochCommandValidator()
    {
        RuleFor(x => x.Epoch).NotNull().SetValidator(new EpochRequestValidator());
    }
}
