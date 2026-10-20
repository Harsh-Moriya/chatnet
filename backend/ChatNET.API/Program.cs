using System.Text;
using System.Text.Json.Serialization;
using ChatNET.API.Auth;
using ChatNET.API.Auth.Models;
using ChatNET.API.Auth.Persistence;
using ChatNET.API.Auth.RateLimiting;
using ChatNET.API.Auth.Services;
using ChatNET.API.Common.Behaviours;
using ChatNET.API.Common.Health;
using ChatNET.API.Common.Logging;
using ChatNET.API.Common.Middleware;
using ChatNET.API.Common.Observability;
using ChatNET.API.Common.OpenApi;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Conversations;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Messaging;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Messaging.Persistence;
using ChatNET.API.Presence;
using ChatNET.API.Presence.Persistence;
using ChatNET.API.Users;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Serilog;
using StackExchange.Redis;

Log.Logger = LoggingSetup.CreateStartupLogger();

try
{
    // WebApplication.CreateBuilder bootstraps the ASP.NET Core host. It loads configuration
    // from appsettings.json, then environment variables, then user-secrets (in that order,
    // each layer overriding the previous), sets up the DI container, and starts Kestrel.
    var builder = WebApplication.CreateBuilder(args);

    // ##### Logging and tracing #####
    // Serilog replaces the built-in logger so every log line is structured. Levels come from the
    // "Serilog" section of configuration. Tracing is set up next to it because the two are meant to be
    // read together: Serilog stamps each log event with the trace and span it was written in.
    // writeToProviders (see UseChatNetLogging) forwards every event to the other logger providers, and the
    // host registers a console provider by default. Left in place it would print each line a second time
    // in the framework's own format.
    builder.Logging.ClearProviders();
    builder.Host.UseChatNetLogging();
    builder.Services.AddChatNetTracing(builder.Configuration);

    // ##### JSON #####
    // Enums are sent as names ("Direct") instead of numbers (0). A number means nothing to a
    // client and changes meaning if the enum is ever reordered. This setting covers REST
    // requests and responses; SignalR has its own, set below.
    builder.Services.ConfigureHttpJsonOptions(options =>
        options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

    // ##### OpenAPI #####
    builder.Services.AddOpenApi(options => options.AddSchemaTransformer(EnumSchemaTransformer.Transform));

    // ##### CORS #####
    // The SignalR browser client sends its negotiate request with credentials by default
    // (withCredentials: true), and a browser only accepts a credentialed cross-origin response
    // when the server answers with Access-Control-Allow-Credentials: true. That is why
    // AllowCredentials() is set, even though authentication uses a bearer token and no cookie.
    // The browser spec forbids AllowAnyOrigin() together with credentials, so the origin must be
    // an explicit value. Reading it from config means no code change is needed when moving
    // between environments. In development the Vite proxy makes requests same-origin, so CORS is
    // only exercised when the frontend and API are served from different origins.
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
    // AddOpenBehavior registers a behaviour for every request type in one line. Behaviours run in
    // registration order, so the first one registered is the outermost. Tracing is first so its span
    // also covers validation; validation comes before any handler logic.
    builder.Services.AddMediatR(cfg =>
    {
        cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
        cfg.AddOpenBehavior(typeof(TracingBehaviour<,>));
        cfg.AddOpenBehavior(typeof(ValidationBehaviour<,>));
    });

    // ##### Validation #####
    // Registers all AbstractValidator<T> classes found in the assembly.
    builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);

    // ##### Database #####
    // EF Core logs a failed command and a failed save at Error, and then rethrows to the caller. Some
    // callers expect the failure and recover (two requests creating the same direct chat, one losing to
    // the unique index), which would put errors in the log for an ordinary outcome. At Warning the pair
    // is still visible, and a failure nobody handled is logged at Error by GlobalExceptionHandler, with
    // the exception and the request.
    builder.Services.AddDbContext<AppDbContext>(options =>
        options
            .UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
            .ConfigureWarnings(warnings => warnings.Log(
                (RelationalEventId.CommandError, LogLevel.Warning),
                (CoreEventId.SaveChangesFailed, LogLevel.Warning))));

    // ##### Identity #####
    // AddIdentityCore registers UserManager, RoleManager, and password hashing without
    // adding cookie authentication. This is the right choice for a JWT API; AddIdentity
    // (the alternative) would configure cookie auth that we don't use and can't easily disable.
    builder.Services.AddIdentityCore<AppUser>(options =>
    {
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = false;
        options.User.RequireUniqueEmail = true;

        // Five wrong passwords in a row lock the account for 15 minutes. The window is short so
        // that someone locking a victim out on purpose only ever costs them 15 minutes, and the
        // per-address rate limit on /api/auth/* makes doing that repeatedly impractical.
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

    // ##### Auth #####
    // JwtBearer middleware validates the token on every request: it decodes the JWT,
    // verifies the signature against our signing key, checks expiry, issuer, and audience,
    // then populates HttpContext.User with the claims inside the token. Endpoints marked
    // [Authorize] are gated on that user being populated correctly.
    //
    // The JWT settings are bound to JwtOptions and validated when the host starts, so a missing
    // or short signing key stops the app immediately with a clear message. Both token creation
    // (JwtTokenService) and validation below read the same object.
    builder.Services.AddOptions<JwtOptions>()
        .BindConfiguration(JwtOptions.SectionName)
        .ValidateOnStart();
    builder.Services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer();

    // Configured through the options system (instead of inside AddJwtBearer) so it can use the
    // validated JwtOptions once the service provider exists.
    builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
        .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
        {
            var jwt = jwtOptions.Value;

            bearer.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,

                // The default tolerance for clock differences is five minutes, which would keep an expired
                // token working for five more. One server issues and checks these tokens, so only a small
                // allowance is needed.
                ClockSkew = TimeSpan.FromSeconds(30),
                ValidIssuer = jwt.Issuer,
                ValidAudience = jwt.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey))
            };

            // Browsers cannot set an Authorization header on a WebSocket upgrade request, so the
            // SignalR client sends the token as ?access_token=. It is accepted only on the hub
            // path, which keeps tokens out of query strings on every other endpoint.
            bearer.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];

                    if (!string.IsNullOrEmpty(accessToken)
                        && context.HttpContext.Request.Path.StartsWithSegments("/hubs/chat"))
                    {
                        context.Token = accessToken;
                    }

                    return Task.CompletedTask;
                }
            };
        });

    builder.Services.AddAuthorization();

    // ##### SignalR #####
    // The Redis backplane makes group and user sends work across server instances: when one
    // instance sends to a group it publishes to Redis, and every instance delivers to the
    // connections it holds. Without it, a message only reaches clients on the same instance
    // as the sender. The prefix keeps this app's channels apart from anything else in Redis.
    builder.Services.AddSignalR(options => options.AddFilter<RefusedCallLoggingFilter>())
        .AddJsonProtocol(options =>
            options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
        .AddStackExchangeRedis(
            builder.Configuration.GetConnectionString("Redis")
                ?? throw new InvalidOperationException("ConnectionStrings:Redis is not configured."),
            options => options.Configuration.ChannelPrefix = RedisChannel.Literal("chatnet"));

    // ##### Services #####
    builder.Services.AddScoped<JwtTokenService>();
    builder.Services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
    builder.Services.AddScoped<RefreshTokenIssuer>();
    builder.Services.AddHostedService<RefreshTokenCleanupService>();
    // The clock the application's own logic reads, so a test can move it. The access token itself is
    // issued and checked against the system clock.
    builder.Services.TryAddSingleton(TimeProvider.System);

    builder.Services.AddScoped<IConversationRepository, ConversationRepository>();
    builder.Services.AddScoped<IMessageRepository, MessageRepository>();
    builder.Services.AddScoped<ConversationNotifier>();

    // ##### Presence #####
    // Who is online lives in Redis (see PresenceScripts). The settings are validated when the host starts,
    // and one Redis connection is shared by everything that needs presence.
    builder.Services.AddOptions<PresenceOptions>()
        .BindConfiguration(PresenceOptions.SectionName)
        .ValidateOnStart();
    builder.Services.AddSingleton<IValidateOptions<PresenceOptions>, PresenceOptionsValidator>();
    builder.Services.AddSingleton(provider => new RedisConnection(
        provider.GetRequiredService<IConfiguration>().GetConnectionString("Redis")
            ?? throw new InvalidOperationException("ConnectionStrings:Redis is not configured.")));
    builder.Services.AddSingleton<IPresenceStore, PresenceStore>();
    builder.Services.AddSingleton<PresenceService>();
    builder.Services.AddSingleton<PresenceRefreshService>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<PresenceRefreshService>());
    builder.Services.AddScoped<IPresenceRepository, PresenceRepository>();

    // ##### Rate limiting #####
    // Login and registration are limited per client address to slow down password guessing and
    // mass account creation. The limits come from configuration (RateLimiting:Auth) and are
    // validated at startup; the policy itself is defined in AuthRateLimiterSetup.
    builder.Services.AddOptions<AuthRateLimitOptions>()
        .BindConfiguration(AuthRateLimitOptions.SectionName)
        .ValidateDataAnnotations()
        .ValidateOnStart();
    builder.Services.AddOptions<RefreshRateLimitOptions>()
        .BindConfiguration(RefreshRateLimitOptions.SectionName)
        .ValidateDataAnnotations()
        .ValidateOnStart();
    builder.Services.AddRateLimiter(_ => { });
    builder.Services.AddSingleton<IConfigureOptions<RateLimiterOptions>, AuthRateLimiterSetup>();

    // ##### Health #####
    // /health reports whether this instance can reach what it depends on. Each check is cut off after
    // three seconds so a dependency that hangs reads as down instead of hanging the probe.
    builder.Services.AddSingleton(provider =>
        new RedisHealthCheck(provider.GetRequiredService<IConfiguration>().GetConnectionString("Redis")!));
    builder.Services.AddHealthChecks()
        .AddCheck<PostgresHealthCheck>("postgres", timeout: TimeSpan.FromSeconds(3))
        .AddCheck<RedisHealthCheck>("redis", timeout: TimeSpan.FromSeconds(3));

    // ##### Exception handling #####
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddProblemDetails();

    // By default a request that cannot be bound (bad JSON, missing parameter) throws only in
    // Development; in Production the framework quietly writes an empty 400. Throwing in every
    // environment sends these requests through GlobalExceptionHandler, so Development and
    // Production return the same 400 ProblemDetails.
    builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

    // ##### Request limits and HSTS #####
    // Kestrel allows 30 MB request bodies by default. The largest legitimate request here is a few
    // kilobytes (a message is at most 4,000 characters), so a larger body is refused before it is read.
    builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1_048_576);

    // Tells browsers to use https for a year once they have seen one https response. Added only
    // outside development (see UseHsts below).
    builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(365));

    var app = builder.Build();

    // Middleware order is a hard constraint, not a style choice. Each middleware wraps
    // everything registered after it, so:
    //   - The security headers go on first, so they are on every response, and the exception handler
    //     comes next so it catches errors from every layer below.
    //   - CORS must precede auth so browser preflight OPTIONS requests are answered before
    //     the JWT middleware rejects them as unauthenticated.
    //   - Authentication must precede Authorization so HttpContext.User is populated before
    //     policy checks run against it.
    // This ordering is the chain-of-responsibility pattern applied to the HTTP pipeline.

    // First, so the request line also covers the work every other middleware does.
    app.UseChatNetRequestLogging();

    app.UseMiddleware<SecurityHeadersMiddleware>();
    app.UseExceptionHandler();

    // HSTS is skipped in development: a browser that remembered it for localhost would refuse every
    // other local project that does not use https. The middleware also does nothing over plain http.
    if (!app.Environment.IsDevelopment())
        app.UseHsts();

    // Gives a ProblemDetails body to responses that only set a status code and nothing else:
    // 401 from authentication, 404 for an unknown route, 405, 415. It leaves responses that
    // already have a body (everything the exception handler writes) alone.
    app.UseStatusCodePages();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference();
    }

    app.UseHttpsRedirection();
    app.UseCors("AllowFrontend");

    // After CORS so browser preflight requests are answered without being counted, and before
    // authentication so over-limit requests are rejected before any password work happens.
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();

    // ##### Endpoints #####
    app.MapHealthEndpoints();

    app.MapAuthEndpoints();
    app.MapUserEndpoints();
    app.MapConversationEndpoints();
    app.MapMessagingEndpoints();
    app.MapPresenceEndpoints();

    // A token is checked once, when the connection is made. Without this an open connection would keep
    // receiving messages long after its token expired; with it the server closes the connection at the
    // token's expiry and the client has to reconnect with a valid one.
    app.MapHub<ChatHub>("/hubs/chat", options => options.CloseOnAuthenticationExpiration = true);

    app.Run();
}
catch (Exception exception) when (exception is not HostAbortedException)
{
    // Logged first so a failed start leaves a line in the log, then rethrown so the process still ends
    // with a failure and a test host sees the real cause.
    Log.Fatal(exception, "ChatNET.API terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// Makes Program visible to WebApplicationFactory<Program> in integration tests without
// requiring a separate test-specific entry point class.
public partial class Program { }
