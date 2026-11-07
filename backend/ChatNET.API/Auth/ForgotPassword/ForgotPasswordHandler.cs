using ChatNET.API.Auth.Models;
using ChatNET.API.Auth.PasswordReset;
using ChatNET.API.Common.Exceptions;
using ChatNET.API.Email;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using ChatNET.API.Outbox.Persistence;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Auth.ForgotPassword;

// Takes a request for a reset link and answers the same whoever asks and whatever the address is: an address with an account, one
// without, a deactivated account and a request over the limit all do the same work (look the address up, count it, write one outbox
// row) and give the same answer. The email itself is sent later by the outbox relay (see PasswordResetMailer).
public class ForgotPasswordHandler : IRequestHandler<ForgotPasswordCommand, Unit>
{
    private readonly UserManager<AppUser> _userManager;
    private readonly PasswordResetEmailLimiter _limiter;
    private readonly OutboxPublisher _publisher;
    private readonly IOutboxRepository _outbox;
    private readonly EmailOptions _email;
    private readonly ILogger<ForgotPasswordHandler> _logger;

    public ForgotPasswordHandler(
        UserManager<AppUser> userManager,
        PasswordResetEmailLimiter limiter,
        OutboxPublisher publisher,
        IOutboxRepository outbox,
        IOptions<EmailOptions> email,
        ILogger<ForgotPasswordHandler> logger)
    {
        _userManager = userManager;
        _limiter = limiter;
        _publisher = publisher;
        _outbox = outbox;
        _email = email.Value;
        _logger = logger;
    }

    public async Task<Unit> Handle(ForgotPasswordCommand request, CancellationToken cancellationToken)
    {
        // Said to everyone, so it reveals nothing about any address, and checked first so a request that cannot be served does not
        // use up anyone's allowance of emails.
        if (!_email.Enabled)
            throw new ServiceUnavailableException("Password reset is not available.");

        var user = await _userManager.FindByEmailAsync(request.Email);

        // Counted for every address, those with an account and those without, so the count cannot tell them apart either.
        var mayEmail = await _limiter.MaySendAsync(_userManager.NormalizeEmail(request.Email), cancellationToken);

        Guid? recipient = mayEmail && user is { IsDeactivated: false } ? user.Id : null;

        if (user is not null && !mayEmail)
            _logger.LogInformation("Password reset email for user {UserId} withheld: too many for this address", user.Id);

        await _outbox.AddAsync(_publisher.CreateForRelay(new PasswordResetRequested(recipient)), cancellationToken);

        if (recipient is { } id)
            _logger.LogInformation("Password reset requested for user {UserId}", id);

        return Unit.Value;
    }
}
