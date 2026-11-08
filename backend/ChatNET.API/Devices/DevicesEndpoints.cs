using ChatNET.API.Common.Auth;
using ChatNET.API.Common.OpenApi;
using ChatNET.API.Devices.Linking.ApproveLinkRequest;
using ChatNET.API.Devices.Linking.CreateLinkRequest;
using ChatNET.API.Devices.Linking.FindLinkRequest;
using ChatNET.API.Devices.Linking.GetLinkRequest;
using ChatNET.API.Devices.ListDevices;
using ChatNET.API.Devices.Models;
using ChatNET.API.Devices.RegisterDevice;
using ChatNET.API.Devices.RemoveDevice;
using ChatNET.API.RateLimiting;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace ChatNET.API.Devices;

public record RegisterDeviceRequest(string? Kind, string? Name);

public record CreateLinkRequestBody(string? TemporaryPublicKey, string? Code, string? Kind, string? Name);

public record ApproveLinkRequestBody(string? Payload);

public static class DevicesEndpoints
{
    public static IEndpointRouteBuilder MapDevicesEndpoints(this IEndpointRouteBuilder app)
    {
        // Everything here is about the caller's own devices and link requests: the id comes from the token, and an id of someone
        // else's is a 404.
        var devices = app.MapGroup("/api/devices")
            .WithTags("Devices")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.Keys);

        // A device that holds the keys says what it is. 409 before encryption is set up and at the limit of devices.
        devices.MapPost("/", async (RegisterDeviceRequest body, HttpContext http, IMediator mediator) =>
        {
            var device = await mediator.Send(new RegisterDeviceCommand(http.User.GetUserId(), body.Kind, body.Name));
            return Results.Created($"/api/devices/{device.Id}", device);
        })
        .Returns<DeviceDto>(
            StatusCodes.Status201Created, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized,
            StatusCodes.Status409Conflict, StatusCodes.Status429TooManyRequests);

        devices.MapGet("/", async (HttpContext http, IMediator mediator) =>
            Results.Ok(await mediator.Send(new ListDevicesQuery(http.User.GetUserId()))))
        .Returns<List<DeviceDto>>(StatusCodes.Status200OK, StatusCodes.Status401Unauthorized, StatusCodes.Status429TooManyRequests);

        // Removes the device and ends the sessions that were signed in on it. This device signing out calls this too.
        devices.MapDelete("/{deviceId:guid}", async (Guid deviceId, HttpContext http, IMediator mediator) =>
        {
            await mediator.Send(new RemoveDeviceCommand(http.User.GetUserId(), deviceId));
            return Results.NoContent();
        })
        .ReturnsNoContent(StatusCodes.Status401Unauthorized, StatusCodes.Status404NotFound, StatusCodes.Status429TooManyRequests);

        // ##### Linking a new device #####

        // The new device, signed in but holding no keys, opens a request and shows the code.
        devices.MapPost("/link-requests", async (CreateLinkRequestBody body, HttpContext http, IMediator mediator) =>
        {
            var created = await mediator.Send(new CreateLinkRequestCommand(
                http.User.GetUserId(), body.TemporaryPublicKey, body.Code, body.Kind, body.Name));
            return Results.Created($"/api/devices/link-requests/{created.Id}", created);
        })
        .Returns<LinkRequestCreatedDto>(
            StatusCodes.Status201Created, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized,
            StatusCodes.Status409Conflict, StatusCodes.Status429TooManyRequests);

        // A device that is signed in finds the request by the code the person typed. The code is in the query, which the request
        // log leaves out.
        devices.MapGet("/link-requests", async ([FromQuery] string? code, HttpContext http, IMediator mediator) =>
            Results.Ok(await mediator.Send(new FindLinkRequestQuery(http.User.GetUserId(), code))))
        .Returns<LinkRequestDto>(
            StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized,
            StatusCodes.Status404NotFound, StatusCodes.Status429TooManyRequests);

        // The new device asks how its request is doing. After approval the first answer carries the sealed secrets and clears them.
        devices.MapGet("/link-requests/{requestId:guid}", async (Guid requestId, HttpContext http, IMediator mediator) =>
            Results.Ok(await mediator.Send(new GetLinkRequestQuery(http.User.GetUserId(), requestId))))
        .Returns<LinkRequestStatusDto>(
            StatusCodes.Status200OK, StatusCodes.Status401Unauthorized, StatusCodes.Status404NotFound, StatusCodes.Status429TooManyRequests);

        // The signed-in device sends the secrets sealed to the key of the request. 409 when the request is no longer open.
        devices.MapPost("/link-requests/{requestId:guid}/approve", async (
            Guid requestId, ApproveLinkRequestBody body, HttpContext http, IMediator mediator) =>
        {
            await mediator.Send(new ApproveLinkRequestCommand(http.User.GetUserId(), requestId, body.Payload));
            return Results.NoContent();
        })
        .ReturnsNoContent(
            StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status404NotFound,
            StatusCodes.Status409Conflict, StatusCodes.Status429TooManyRequests);

        return app;
    }
}
