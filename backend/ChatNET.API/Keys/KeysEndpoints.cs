using ChatNET.API.Common.Auth;
using ChatNET.API.Common.OpenApi;
using ChatNET.API.Keys.AddKeyGeneration;
using ChatNET.API.Keys.GetKeyBackup;
using ChatNET.API.Keys.GetKeyStatus;
using ChatNET.API.Keys.LookupKeys;
using ChatNET.API.Keys.Models;
using ChatNET.API.Keys.ReplaceKeyBackup;
using ChatNET.API.Keys.ResetKeys;
using ChatNET.API.Keys.SetUpKeys;
using ChatNET.API.RateLimiting;
using MediatR;

namespace ChatNET.API.Keys;

public record SetUpKeysRequest(string? Bundle, string? Backup);

public record ReplaceKeyBackupRequest(string? Blob, int? ExpectedVersion);

public record AddKeyGenerationRequest(string? Bundle, string? Backup, int? ExpectedBackupVersion, Guid? RemoveDeviceId);

public record ResetKeysRequest(string? Bundle, string? Backup, string? Password);

public record LookupKeysRequest(List<Guid>? UserIds);

public static class KeysEndpoints
{
    public static IEndpointRouteBuilder MapKeysEndpoints(this IEndpointRouteBuilder app)
    {
        // Everything here is about the caller's own keys: the id comes from the token and no route takes another person's.
        var keys = app.MapGroup("/api/keys/me")
            .WithTags("Keys")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.Keys);

        // Whether encryption is set up, the public keys of every generation, and the version of the backup. A device that holds no
        // keys asks this to know whether to set up or to unlock.
        keys.MapGet("/", async (HttpContext http, IMediator mediator) =>
            Results.Ok(await mediator.Send(new GetKeyStatusQuery(http.User.GetUserId()))))
        .Returns<KeyStatusDto>(StatusCodes.Status200OK, StatusCodes.Status401Unauthorized, StatusCodes.Status429TooManyRequests);

        // The bundle of generation 1 and the first backup, together. 409 when the account already has keys.
        keys.MapPost("/", async (SetUpKeysRequest body, HttpContext http, IMediator mediator) =>
        {
            var status = await mediator.Send(new SetUpKeysCommand(http.User.GetUserId(), body.Bundle, body.Backup));
            return Results.Created("/api/keys/me", status);
        })
        .Returns<KeyStatusDto>(
            StatusCodes.Status201Created, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized,
            StatusCodes.Status409Conflict, StatusCodes.Status429TooManyRequests);

        // The encrypted backup. Only ever returned to its owner: it opens with a passphrase, and whoever holds it can try them.
        keys.MapGet("/backup", async (HttpContext http, IMediator mediator) =>
            Results.Ok(await mediator.Send(new GetKeyBackupQuery(http.User.GetUserId()))))
        .Returns<KeyBackupDto>(
            StatusCodes.Status200OK, StatusCodes.Status401Unauthorized, StatusCodes.Status404NotFound, StatusCodes.Status429TooManyRequests);

        // A new backup of the same secrets, for a new passphrase. 409 when it was changed from another device since the version the
        // caller read.
        keys.MapPut("/backup", async (ReplaceKeyBackupRequest body, HttpContext http, IMediator mediator) =>
            Results.Ok(await mediator.Send(new ReplaceKeyBackupCommand(http.User.GetUserId(), body.Blob, body.ExpectedVersion))))
        .Returns<BackupInfoDto>(
            StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status404NotFound,
            StatusCodes.Status409Conflict, StatusCodes.Status429TooManyRequests);

        // The next generation of the account key with the backup that holds it, and the device that was lost, if that is why: the
        // keys change, the device goes and the conversations are marked for a new key period in one step. 404 for a device that is
        // not the caller's. 409 with a code when the generation is not the next one or the backup moved on.
        keys.MapPost("/generations", async (AddKeyGenerationRequest body, HttpContext http, IMediator mediator) =>
            Results.Ok(await mediator.Send(new AddKeyGenerationCommand(
                http.User.GetUserId(), body.Bundle, body.Backup, body.ExpectedBackupVersion, body.RemoveDeviceId))))
        .Returns<KeyStatusDto>(
            StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status404NotFound,
            StatusCodes.Status409Conflict, StatusCodes.Status429TooManyRequests);

        // Starts the keys over for a person who has neither a device nor the passphrase: the next generation from new keys and a new
        // backup, every device removed and the conversations marked for a new key period. The account password is asked again: 403 with
        // the code wrong-password when it is not right, 429 while the account is locked.
        keys.MapPost("/reset", async (ResetKeysRequest body, HttpContext http, IMediator mediator) =>
            Results.Ok(await mediator.Send(new ResetKeysCommand(http.User.GetUserId(), body.Bundle, body.Backup, body.Password))))
        .Returns<KeyStatusDto>(
            StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden,
            StatusCodes.Status404NotFound, StatusCodes.Status409Conflict, StatusCodes.Status429TooManyRequests);

        // The public keys of other people, which a browser seals a conversation key to. Only keys that are made to be shared.
        var lookup = app.MapGroup("/api/keys")
            .WithTags("Keys")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.Keys);

        // The current bundle of each person named who has set up encryption; people who have not are left out.
        lookup.MapPost("/lookup", async (LookupKeysRequest body, IMediator mediator) =>
            Results.Ok(await mediator.Send(new LookupKeysQuery(body.UserIds ?? []))))
        .Returns<LookedUpKeysDto>(
            StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status429TooManyRequests);

        return app;
    }
}
