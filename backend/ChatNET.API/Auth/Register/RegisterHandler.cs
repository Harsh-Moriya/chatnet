using ChatNET.API.Auth.Models;
using ChatNET.API.Auth.Services;
using ChatNET.API.Common.Exceptions;
using ChatNET.API.Common.Persistence;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChatNET.API.Auth.Register;

public class RegisterHandler : IRequestHandler<RegisterCommand, AuthResult>
{
    private readonly UserManager<AppUser> _userManager;
    private readonly JwtTokenService _jwtTokenService;
    private readonly RefreshTokenIssuer _refreshTokens;
    private readonly AppDbContext _db;
    private readonly ILogger<RegisterHandler> _logger;

    public RegisterHandler(
        UserManager<AppUser> userManager,
        JwtTokenService jwtTokenService,
        RefreshTokenIssuer refreshTokens,
        AppDbContext db,
        ILogger<RegisterHandler> logger)
    {
        _userManager = userManager;
        _jwtTokenService = jwtTokenService;
        _refreshTokens = refreshTokens;
        _db = db;
        _logger = logger;
    }

    public async Task<AuthResult> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        var user = new AppUser
        {
            UserName = request.UserName,
            Email = request.Email,
            DisplayName = request.DisplayName.Trim(),
        };

        // Identity checks "is this email taken?" and then inserts, and nothing in the database stops
        // two accounts sharing an email (its email index is not unique). Requests that arrive
        // together would all pass the check. A transaction-scoped advisory lock on the email makes
        // them go one at a time: the second waits for the first to commit, then sees its account.
        // The lock is released by the commit or rollback, and registrations for different emails
        // never wait for each other.
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var lockKey = _userManager.NormalizeEmail(request.Email);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);

        IdentityResult result;
        try
        {
            result = await _userManager.CreateAsync(user, request.Password);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The user name and email indexes are unique, so of several requests for one name or email
            // the others fail here instead of in Identity's check.
            _logger.LogInformation("Registration lost a race for a user name or email");
            throw new ConflictException("That username or email is already taken.");
        }

        if (!result.Succeeded)
            throw FailureFor(result);

        // Stored in the same transaction, so there is never an account whose first sign-in has no session.
        var refreshToken = await _refreshTokens.StartSessionAsync(user.Id, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new AuthResult(AuthResponse.For(user, _jwtTokenService.GenerateToken(user)), refreshToken);
    }

    // A password that breaks an account rule is a mistake in the request, so it is reported like any
    // other validation failure, on the Password field. A name or email that is already taken is a
    // clash with existing data, which is a conflict.
    private static Exception FailureFor(IdentityResult result)
    {
        var passwordProblems = result.Errors.Where(e => e.Code.StartsWith("Password", StringComparison.Ordinal)).ToList();
        if (passwordProblems.Count > 0)
            return new ValidationException(passwordProblems.Select(e => new ValidationFailure("Password", e.Description)));

        return new ConflictException(string.Join(" ", result.Errors.Select(e => e.Description)));
    }
}
