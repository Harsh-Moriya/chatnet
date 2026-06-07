using FluentValidation;
using MediatR;
using Scalar.AspNetCore;

// LEARNING: WebApplication.CreateBuilder sets up the default ASP.NET Core host:
// configuration (appsettings.json → environment variables → user-secrets in order),
// the DI container, and Kestrel (the built-in web server). Everything between here
// and builder.Build() is registering services — nothing runs yet.
var builder = WebApplication.CreateBuilder(args);

// ── OpenAPI (dev only) ────────────────────────────────────────────────────────
// Generates an OpenAPI spec at /openapi/v1.json. Scalar serves a UI on top of it.
builder.Services.AddOpenApi();

// ── CORS ──────────────────────────────────────────────────────────────────────
// LEARNING: AllowCredentials() is required for SignalR — the WebSocket upgrade
// carries the auth token as a query string parameter. When credentials are enabled,
// the origin must be explicit (AllowAnyOrigin() is forbidden by the browser spec).
// Frontend URL comes from config so it never needs a code change between environments.
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

// ── MediatR ───────────────────────────────────────────────────────────────────
// LEARNING: MediatR scans this assembly and registers every IRequestHandler<TRequest>
// automatically. Endpoints and Hub methods stay thin — they call _mediator.Send(command)
// and MediatR routes to the correct handler. No giant service classes, no coupling
// between features. Interview note: this is the Mediator pattern — decouples callers
// from handlers so neither knows about the other.
builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

// ── FluentValidation ─────────────────────────────────────────────────────────
// Registers all AbstractValidator<T> classes found in this assembly.
// A MediatR pipeline behaviour (wired in step 3) intercepts every command/query
// before it reaches the handler and runs validation — invalid input never reaches
// business logic.
builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);

// ── Authentication + Authorization ────────────────────────────────────────────
// JWT bearer scheme configured in step 4 (Auth feature). Registered here so the
// middleware pipeline order is correct from the start.
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();

// ── TODO: EF Core + Identity — wired in step 3 ───────────────────────────────
// ── TODO: JwtBearer scheme — configured in step 4 ────────────────────────────
// ── TODO: SignalR + Redis backplane — wired in Phase 2 ───────────────────────

// ═════════════════════════════════════════════════════════════════════════════
var app = builder.Build();

// LEARNING: Middleware ORDER is a real architecture decision — each middleware wraps
// every layer registered after it. The exception handler must be outermost (first)
// so it catches errors from auth, routing, and handlers alike. Reversing auth and
// routing silently breaks protected endpoints. Reversing CORS and auth causes
// preflight failures. Order here is deliberate.
// Interview note: this is the "chain of responsibility" pattern applied to HTTP.

// Global exception handler — maps our exception hierarchy to ProblemDetails responses.
// Replaced with a typed handler in step 4; this stub keeps the pipeline correct now.
app.UseExceptionHandler("/error");
app.Map("/error", () => Results.Problem("An unexpected error occurred."));

if (app.Environment.IsDevelopment())
{
    // OpenAPI spec at /openapi/v1.json
    app.MapOpenApi();
    // Scalar UI at /scalar/v1 — interactive API explorer replacing Swagger UI
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

// CORS before auth — OPTIONS preflight requests must be handled before the JWT
// middleware rejects them as unauthenticated.
app.UseCors("AllowFrontend");

// LEARNING: UseAuthentication reads the JWT from the Authorization header and
// populates HttpContext.User with the claims inside the token.
// UseAuthorization then checks those claims against [Authorize] attributes/policies.
// Authentication MUST come before Authorization — reversing them silently breaks
// all protected endpoints with no helpful error message.
app.UseAuthentication();
app.UseAuthorization();

// ── Endpoints ─────────────────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
   .WithTags("Health")
   .AllowAnonymous();

// Auth endpoints added in step 4: POST /api/auth/register, POST /api/auth/login

app.Run();

// LEARNING: This partial class makes Program visible to WebApplicationFactory<Program>
// in integration tests. Without it the test project cannot reference the app's startup
// configuration and has to duplicate it — the partial class avoids that entirely.
public partial class Program { }
