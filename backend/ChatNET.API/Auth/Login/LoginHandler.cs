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

    public LoginHandler(UserManager<AppUser> userManager, JwtTokenService jwtTokenService)
    {
        _userManager = userManager;
        _jwtTokenService = jwtTokenService;
    }

    public async Task<AuthResponse> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);

        // Deliberately return the same error whether the email doesn't exist or the
        // password is wrong. Separate messages would let an attacker enumerate valid emails.
        if (user is null || !await _userManager.CheckPasswordAsync(user, request.Password))
            throw new UnauthorizedException("Invalid email or password.");

        if (user.IsDeactivated)
            throw new ForbiddenException("This account has been deactivated.");

        var token = _jwtTokenService.GenerateToken(user);

        return new AuthResponse(token, user.Id.ToString(), user.UserName!, user.DisplayName);
    }
}
