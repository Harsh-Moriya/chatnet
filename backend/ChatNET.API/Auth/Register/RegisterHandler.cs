using ChatNET.API.Auth.Models;
using ChatNET.API.Auth.Services;
using ChatNET.API.Common.Exceptions;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace ChatNET.API.Auth.Register;

public class RegisterHandler : IRequestHandler<RegisterCommand, AuthResponse>
{
    private readonly UserManager<AppUser> _userManager;
    private readonly JwtTokenService _jwtTokenService;

    public RegisterHandler(UserManager<AppUser> userManager, JwtTokenService jwtTokenService)
    {
        _userManager = userManager;
        _jwtTokenService = jwtTokenService;
    }

    public async Task<AuthResponse> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        var user = new AppUser
        {
            UserName = request.UserName,
            Email = request.Email,
            DisplayName = request.DisplayName,
        };

        var result = await _userManager.CreateAsync(user, request.Password);

        if (!result.Succeeded)
        {
            // Identity returns structured errors (DuplicateUserName, PasswordTooShort, etc.).
            // We surface them as a single conflict message; the validator already caught
            // format issues, so these are domain-level conflicts (e.g. username taken).
            var errors = string.Join(" ", result.Errors.Select(e => e.Description));
            throw new ConflictException(errors);
        }

        var token = _jwtTokenService.GenerateToken(user);

        return new AuthResponse(token, user.Id.ToString(), user.UserName!, user.DisplayName);
    }
}
