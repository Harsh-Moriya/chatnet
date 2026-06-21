using System.Text;
using ChatNET.API.Auth.Login;
using ChatNET.API.Auth.Models;
using ChatNET.API.Auth.Register;
using ChatNET.API.Auth.Services;
using ChatNET.API.Common.Behaviours;
using ChatNET.API.Common.Middleware;
using ChatNET.API.Common.Persistence;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

// WebApplication.CreateBuilder bootstraps the ASP.NET Core host. It loads configuration
// from appsettings.json, then environment variables, then user-secrets (in that order,
// each layer overriding the previous), sets up the DI container, and starts Kestrel.
var builder = WebApplication.CreateBuilder(args);

// ##### OpenAPI #####
builder.Services.AddOpenApi();

// ##### CORS #####
// AllowCredentials() is required for SignalR because the WebSocket upgrade passes the
// auth token as a query parameter rather than a header. The browser spec forbids
// AllowAnyOrigin() when credentials are enabled, so the origin must be an explicit value.
// Reading it from config means no code change is needed when moving between environments.
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy
            .WithOrigins(builder.Configuration["Frontend:Url"] ?? "http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// ##### MediatR #####
// Scans the assembly for every IRequestHandler<TRequest, TResponse> and registers them
// automatically. Endpoints and Hub methods stay thin: they call _mediator.Send(command)
// and MediatR routes to the correct handler. Neither caller nor handler knows about the
// other, which is the Mediator pattern: it removes direct coupling between features.
//
// AddOpenBehavior registers ValidationBehaviour for every request type in one line.
// Behaviours run in registration order — validation runs first before any other concern.
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
    cfg.AddOpenBehavior(typeof(ValidationBehaviour<,>));
});

// ##### Validation #####
// Registers all AbstractValidator<T> classes found in the assembly.
builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);

// ##### Database #####
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// ##### Identity #####
// AddIdentityCore registers UserManager, RoleManager, and password hashing without
// adding cookie authentication. This is the right choice for a JWT API — AddIdentity
// (the alternative) would configure cookie auth that we don't use and can't easily disable.
builder.Services.AddIdentityCore<AppUser>(options =>
{
    options.Password.RequiredLength = 8;
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = false;
    options.User.RequireUniqueEmail = true;
})
.AddRoles<IdentityRole<Guid>>()
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

// ##### Auth #####
// JwtBearer middleware validates the token on every request: it decodes the JWT,
// verifies the signature against our signing key, checks expiry, issuer, and audience,
// then populates HttpContext.User with the claims inside the token. Endpoints marked
// [Authorize] are gated on that user being populated correctly.
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(builder.Configuration["Jwt:SigningKey"]!))
    };
});

builder.Services.AddAuthorization();

// ##### Services #####
builder.Services.AddScoped<JwtTokenService>();

// ##### Exception handling #####
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// ############################################################

var app = builder.Build();

// Middleware order is a hard constraint, not a style choice. Each middleware wraps
// everything registered after it, so:
//   - The exception handler must be outermost to catch errors from every layer below.
//   - CORS must precede auth so browser preflight OPTIONS requests are answered before
//     the JWT middleware rejects them as unauthenticated.
//   - Authentication must precede Authorization so HttpContext.User is populated before
//     policy checks run against it.
// This ordering is the chain-of-responsibility pattern applied to the HTTP pipeline.

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();
app.UseCors("AllowFrontend");
app.UseAuthentication();
app.UseAuthorization();

// ##### Endpoints #####
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
   .WithTags("Health")
   .AllowAnonymous();

var auth = app.MapGroup("/api/auth").WithTags("Auth");

auth.MapPost("/register", async (RegisterCommand command, IMediator mediator) =>
{
    var result = await mediator.Send(command);
    return Results.Ok(result);
})
.AllowAnonymous();

auth.MapPost("/login", async (LoginCommand command, IMediator mediator) =>
{
    var result = await mediator.Send(command);
    return Results.Ok(result);
})
.AllowAnonymous();

app.Run();

// Makes Program visible to WebApplicationFactory<Program> in integration tests without
// requiring a separate test-specific entry point class.
public partial class Program { }
