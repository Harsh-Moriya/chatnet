using ChatNET.API.Auth.Models;
using ChatNET.API.Common.Persistence;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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
// in the DI container automatically. Endpoints and Hub methods stay thin: they call
// _mediator.Send(command) and MediatR routes to the correct handler. Neither the caller
// nor the handler knows the other exists, which is the Mediator pattern: it removes
// direct coupling so features can change independently.
builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

// ##### Validation #####
// Registers all AbstractValidator<T> classes found in the assembly. A MediatR pipeline
// behaviour (in Common/Behaviours) intercepts every command before the handler runs and
// executes these validators, so business logic only ever receives valid input.
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
// Authentication and authorization are registered here so the middleware pipeline order
// below is correct. The JWT bearer scheme is wired in the Auth feature setup.
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();

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
// Getting it wrong causes silent failures that are hard to debug.

app.UseExceptionHandler("/error");
app.Map("/error", () => Results.Problem("An unexpected error occurred."));

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    // Scalar API explorer is available at /scalar/v1 in development
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

app.Run();

// Makes Program visible to WebApplicationFactory<Program> in integration tests without
// requiring a separate test-specific entry point class.
public partial class Program { }
