using ChatNET.API.Auth.Models;
using ChatNET.API.Auth.Services;
using ChatNET.API.Common.Exceptions;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace ChatNET.API.Auth.Login;

public class LoginHandler : IRequestHandler<LoginCommand, AuthResponse>
{
    private readonly UserManager<AppUser> _userManager;
    private readonly JwtTokenService _jwtTokenService;
    private readonly ILogger<LoginHandler> _logger;

    public LoginHandler(
        UserManager<AppUser> userManager,
        JwtTokenService jwtTokenService,
        ILogger<LoginHandler> logger)
    {
        _userManager = userManager;
        _jwtTokenService = jwtTokenService;
        _logger = logger;
    }

    public async Task<AuthResponse> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);

        // Deliberately return the same error whether the email doesn't exist or the
        // password is wrong. Separate messages would let an attacker enumerate valid emails.
        if (user is null)
            throw new UnauthorizedException("Invalid email or password.");

        // Checked before the password so a locked account cannot be probed: while locked, even
        // the right password is refused and no guess is evaluated.
        if (await _userManager.IsLockedOutAsync(user))
            throw await LockedOutAsync(user);

        // CheckPasswordAsync only compares the hash; unlike the SignInManager it does not
        // count failures, so the lockout bookkeeping is done explicitly here.
        if (!await _userManager.CheckPasswordAsync(user, request.Password))
        {
            // Counts the failure and, on the configured number in a row, sets the lockout end.
            await _userManager.AccessFailedAsync(user);

            if (await _userManager.IsLockedOutAsync(user))
                _logger.LogWarning("Account {UserId} locked out after repeated failed sign-ins", user.Id);

            throw new UnauthorizedException("Invalid email or password.");
        }

        await _userManager.ResetAccessFailedCountAsync(user);

        if (user.IsDeactivated)
            throw new ForbiddenException("This account has been deactivated.");

        var token = _jwtTokenService.GenerateToken(user);

        return new AuthResponse(token, user.Id.ToString(), user.UserName!, user.DisplayName);
    }

    private async Task<TooManyAttemptsException> LockedOutAsync(AppUser user)
    {
        var lockoutEnd = await _userManager.GetLockoutEndDateAsync(user);
        var remaining = (lockoutEnd ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow;

        return new TooManyAttemptsException(remaining);
    }
}
