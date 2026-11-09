using ChatNET.API.Conversations.Membership;
using ChatNET.API.Keys;
using FluentValidation;

namespace ChatNET.API.Conversations.KeyEpochs;

// The shape of a new key period, wherever it arrives: when a conversation is made, when people are added, and on its own. The
// server cannot see inside any of it, so this is every check there is on the bytes themselves. The messages never repeat a value.
public class EpochRequestValidator : AbstractValidator<EpochRequest>
{
    public EpochRequestValidator()
    {
        RuleFor(x => x.Epoch)
            .NotNull().WithMessage("The epoch number is required.")
            .GreaterThanOrEqualTo(1).WithMessage("The epoch number must be at least 1.");

        RuleFor(x => x.Commitment)
            .Must(value => KeyFormats.Decode(value, KeyFormats.CommitmentBytes) is { Length: KeyFormats.CommitmentBytes })
            .WithMessage($"The commitment must be {KeyFormats.CommitmentBytes} bytes.");

        RuleFor(x => x.Signature)
            .Must(value => KeyFormats.Decode(value, KeyFormats.SignatureBytes) is { Length: KeyFormats.SignatureBytes })
            .WithMessage($"The signature must be {KeyFormats.SignatureBytes} bytes.");

        RuleFor(x => x.Envelopes)
            .NotNull().WithMessage("The envelopes are required.")
            .Must(envelopes => envelopes is { Count: >= 1 and <= GroupLimits.MaxActiveMembers })
            .WithMessage($"Send between 1 and {GroupLimits.MaxActiveMembers} envelopes.")
            .Must(envelopes => envelopes is null || envelopes.Select(e => e.UserId).Distinct().Count() == envelopes.Count)
            .WithMessage("Each person gets one envelope.");

        RuleForEach(x => x.Envelopes).ChildRules(envelope =>
        {
            envelope.RuleFor(e => e.UserId)
                .NotNull().WithMessage("Each envelope names a person.")
                .NotEqual(Guid.Empty).WithMessage("Each envelope names a person.");

            envelope.RuleFor(e => e.Generation)
                .NotNull().WithMessage("Each envelope names a key generation.")
                .GreaterThanOrEqualTo(1).WithMessage("A key generation is at least 1.");

            envelope.RuleFor(e => e.Envelope)
                .Must(value => KeyFormats.Decode(value, KeyFormats.EnvelopeBytes) is { } bytes && KeyFormats.IsEnvelope(bytes))
                .WithMessage("An envelope must be a version 1 envelope of the right size.");
        });
    }
}
