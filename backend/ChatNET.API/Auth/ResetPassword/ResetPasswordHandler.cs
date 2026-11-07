using ChatNET.API.Auth.Models;
using ChatNET.API.Auth.Persistence;
using ChatNET.API.Auth.Services;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Push.Persistence;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Auth.ResetPassword;

// Sets a new password from a reset link and ends every session of the person. It is all one transaction: the link is marked used,
// the password changes, every refresh token and every push registration goes, and the person's other links are voided, or none
// of it happens. A new password that breaks an account rule rolls the transaction back, so the link still works for another try.
public class ResetPasswordHandler : IRequestHandler<ResetPasswordCommand, Unit>
{
    private const string InvalidLink = "This link is invalid or has expired.";

    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _userManager;
    private readonly IPasswordResetTokenRepository _resetTokens;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IPushSubscriptionRepository _pushSubscriptions;
    private readonly TimeProvider _time;
    private readonly ILogger<ResetPasswordHandler> _logger;

    public ResetPasswordHandler(
        AppDbContext db,
        UserManager<AppUser> userManager,
        IPasswordResetTokenRepository resetTokens,
        IRefreshTokenRepository refreshTokens,
        IPushSubscriptionRepository pushSubscriptions,
        TimeProvider time,
        ILogger<ResetPasswordHandler> logger)
    {
        _db = db;
        _userManager = userManager;
        _resetTokens = resetTokens;
        _refreshTokens = refreshTokens;
        _pushSubscriptions = pushSubscriptions;
        _time = time;
        _logger = logger;
    }

    public async Task<Unit> Handle(ResetPasswordCommand request, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        // The link is used up first, in one conditional statement, so of any number of requests with the same link exactly one
        // gets any further. An unknown, used and expired link are the same answer: nothing tells a guesser which it was.
        var userId = await _resetTokens.TryConsumeAsync(TokenHash.Of(request.Token), now, cancellationToken)
                     ?? throw InvalidLinkFailure();

        // The person's row is locked for the rest of the transaction. A renewal or a sign-in that is being made right now holds a
        // share lock on it, so this waits for it to finish and then revokes what it made; one that starts later waits for this to
        // commit and finds its token revoked, or the password changed. Without the lock a session could slip in after the
        // revocation and outlive the reset.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"AspNetUsers\" WHERE \"Id\" = {userId} FOR UPDATE", cancellationToken);

        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null || user.IsDeactivated)
            throw InvalidLinkFailure();

        await EnsureAcceptableAsync(user, request.NewPassword);

        // Written by hand, not with Identity's ResetPasswordAsync, which needs the token providers that are deliberately not
        // registered, and in one update, so the password, the stamp and the lockout change together. The stamp is what a sign-in
        // checks to know the password it verified is still the password.
        user.PasswordHash = _userManager.PasswordHasher.HashPassword(user, request.NewPassword);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.LockoutEnd = null;
        user.AccessFailedCount = 0;

        var update = await _userManager.UpdateAsync(user);
        if (!update.Succeeded)
            throw new InvalidOperationException(
                $"The new password could not be saved ({string.Join(", ", update.Errors.Select(e => e.Code))}).");

        var sessions = await _refreshTokens.RevokeAllForUserAsync(userId, now, cancellationToken);
        var browsers = await _pushSubscriptions.RemoveAllForUserAsync(userId, cancellationToken);
        await _resetTokens.VoidUnusedAsync(userId, now, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Password reset for user {UserId}: {RevokedSessions} sessions revoked, {RemovedBrowsers} push registrations removed",
            userId, sessions, browsers);

        return Unit.Value;
    }

    // The account rules (length, digit, upper and lower case) are Identity's. A failure is a mistake in the request, reported on
    // the field like any other, and nothing has been changed yet.
    private async Task EnsureAcceptableAsync(AppUser user, string newPassword)
    {
        var problems = new List<IdentityError>();
        foreach (var validator in _userManager.PasswordValidators)
        {
            var result = await validator.ValidateAsync(_userManager, user, newPassword);
            if (!result.Succeeded)
                problems.AddRange(result.Errors);
        }

        if (problems.Count > 0)
            throw new ValidationException(problems.Select(p => new ValidationFailure(nameof(ResetPasswordCommand.NewPassword), p.Description)));
    }

    private static ValidationException InvalidLinkFailure() =>
        new([new ValidationFailure(nameof(ResetPasswordCommand.Token), InvalidLink)]);
}
