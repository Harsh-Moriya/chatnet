using System.Security.Cryptography;
using ChatNET.API.Auth.Models;
using ChatNET.API.Auth.Persistence;
using ChatNET.API.Auth.Services;
using ChatNET.API.Common.Hosting;
using ChatNET.API.Common.Time;
using ChatNET.API.Email;
using ChatNET.API.Outbox.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Auth.PasswordReset;

// Sends the reset email, from the outbox relay: it makes the token, stores its hash, and sends the link. Doing it here and not in the
// request means the request does the same work for every address (it only writes a row), so how long it takes says nothing about
// whether the address has an account, and a mail server that is down is retried by the relay instead of failing the request.
//
// The link has the token after a "#". A fragment is never sent to a server, so the token is not in any request line, proxy log or
// Referer header; the page reads it and sends it in the body of the reset request.
public class PasswordResetMailer
{
    private readonly UserManager<AppUser> _users;
    private readonly IPasswordResetTokenRepository _tokens;
    private readonly IEmailSender _sender;
    private readonly EmailOptions _email;
    private readonly PasswordResetOptions _options;
    private readonly FrontendOptions _frontend;
    private readonly TimeProvider _time;
    private readonly ILogger<PasswordResetMailer> _logger;

    public PasswordResetMailer(
        UserManager<AppUser> users,
        IPasswordResetTokenRepository tokens,
        IEmailSender sender,
        IOptions<EmailOptions> email,
        IOptions<PasswordResetOptions> options,
        IOptions<FrontendOptions> frontend,
        TimeProvider time,
        ILogger<PasswordResetMailer> logger)
    {
        _users = users;
        _tokens = tokens;
        _sender = sender;
        _email = email.Value;
        _options = options.Value;
        _frontend = frontend.Value;
        _time = time;
        _logger = logger;
    }

    // Throws when the email could not be sent for a reason that trying again may fix, so the relay keeps the row and retries it.
    // Everything else ends quietly: the row counts as done.
    public async Task SendAsync(OutboxMessage row, Guid? userId, CancellationToken cancellationToken)
    {
        // An address nobody has, an account that cannot be sent one, or a request over the per-address limit.
        if (userId is not { } id)
            return;

        var now = _time.GetUtcNow().UtcDateTime.TruncateToMicroseconds();

        if (!_email.Enabled)
        {
            _logger.LogInformation("Password reset requested for user {UserId} but email is off; nothing sent (outbox {OutboxId})", id, row.Id);
            return;
        }

        if (now - row.CreatedAt > _options.SendWithin)
        {
            _logger.LogWarning(
                "Password reset email for user {UserId} was not sent in time and is dropped (outbox {OutboxId})", id, row.Id);
            return;
        }

        // Read again now, and the address is the one stored, never the one that was typed: Identity matches addresses by
        // upper-casing them, which can match an address that is not spelled the same.
        var recipient = await _users.Users
            .AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => new { u.Email, u.IsDeactivated })
            .SingleOrDefaultAsync(cancellationToken);

        if (recipient is not { Email: { Length: > 0 } address, IsDeactivated: false })
            return;

        var token = TokenHash.NewToken();
        await _tokens.AddAsync(
            new PasswordResetToken
            {
                Id = Guid.NewGuid(),
                UserId = id,
                TokenHash = TokenHash.Of(token),
                CreatedAt = now,
                ExpiresAt = now + _options.TokenLifetime,
            },
            cancellationToken);

        var link = $"{_frontend.Url.TrimEnd('/')}/reset-password#token={token}";

        try
        {
            await _sender.SendAsync(PasswordResetEmail.For(address, link, _options.TokenLifetime), cancellationToken);
        }
        catch (EmailDeliveryException refusal) when (refusal.IsPermanent)
        {
            // The server said no for good: sending it again would be refused again. Said once, with the reason that is safe to log.
            _logger.LogWarning(
                "The mail server refused the password reset email for user {UserId} ({Reason}); not retried (outbox {OutboxId})",
                id, refusal.Message, row.Id);
            return;
        }

        _logger.LogInformation("Password reset email sent to user {UserId} (outbox {OutboxId})", id, row.Id);
    }
}
