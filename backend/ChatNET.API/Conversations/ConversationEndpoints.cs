using ChatNET.API.Common.Auth;
using ChatNET.API.Common.OpenApi;
using ChatNET.API.Conversations.AddMembers;
using ChatNET.API.Conversations.ChangeMemberRole;
using ChatNET.API.Conversations.CreateDirect;
using ChatNET.API.Conversations.CreateGroup;
using ChatNET.API.Conversations.CreateKeyEpoch;
using ChatNET.API.Conversations.GetConversation;
using ChatNET.API.Conversations.GetKeyEpochs;
using ChatNET.API.Conversations.GetMembers;
using ChatNET.API.Conversations.KeyEpochs;
using ChatNET.API.Conversations.GetMyConversations;
using ChatNET.API.Conversations.LeaveConversation;
using ChatNET.API.Conversations.MarkRead;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.RemoveMember;
using ChatNET.API.Conversations.RenameConversation;
using ChatNET.API.Conversations.SetMuted;
using ChatNET.API.RateLimiting;
using MediatR;

namespace ChatNET.API.Conversations;

public record CreateDirectRequest(Guid? Id, Guid OtherUserId, EpochRequest? Epoch);

public record CreateGroupRequest(Guid? Id, string Name, List<Guid> MemberIds, EpochRequest? Epoch);

public record MarkReadRequest(Guid MessageId);

public record SetMutedRequest(bool? IsMuted);

public record AddMembersRequest(List<Guid>? UserIds, EpochRequest? Epoch);

public record ChangeMemberRoleRequest(MemberRole? Role);

public record RenameConversationRequest(string? Name);

public static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        var conversations = app.MapGroup("/api/conversations")
            .WithTags("Conversations")
            .RequireAuthorization();

        conversations.MapGet("/", async (HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(new GetMyConversationsQuery(http.User.GetUserId()));
            return Results.Ok(result);
        })
        .Returns<List<ConversationDto>>(StatusCodes.Status200OK, StatusCodes.Status401Unauthorized);

        conversations.MapGet("/{conversationId:guid}", async (Guid conversationId, HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(new GetConversationQuery(conversationId, http.User.GetUserId()));
            return Results.Ok(result);
        })
        .Returns<ConversationDto>(StatusCodes.Status200OK, StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden);

        conversations.MapPost("/direct", async (CreateDirectRequest body, HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(new CreateDirectCommand(
                http.User.GetUserId(), body.Id ?? Guid.Empty, body.OtherUserId, body.Epoch!));
            return Results.Ok(result);
        })
        .Returns<ConversationDto>(
            StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status404NotFound,
            StatusCodes.Status409Conflict);

        conversations.MapPost("/group", async (CreateGroupRequest body, HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(
                new CreateGroupCommand(http.User.GetUserId(), body.Id ?? Guid.Empty, body.Name, body.MemberIds ?? [], body.Epoch!));
            return Results.Created($"/api/conversations/{result.Id}", result);
        })
        .Returns<ConversationDto>(
            StatusCodes.Status201Created, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized,
            StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        // The member says they have read up to and including this message. 204 whether or not the
        // position moved: an older message than the one already read is not an error.
        conversations.MapPost("/{conversationId:guid}/read", async (
            Guid conversationId, MarkReadRequest body, HttpContext http, IMediator mediator) =>
        {
            await mediator.Send(new MarkReadCommand(conversationId, http.User.GetUserId(), body.MessageId));
            return Results.NoContent();
        })
        .ReturnsNoContent(
            StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden);

        // Mutes or unmutes the conversation for the caller only. 204 whether or not the value changed.
        conversations.MapPut("/{conversationId:guid}/mute", async (
            Guid conversationId, SetMutedRequest body, HttpContext http, IMediator mediator) =>
        {
            await mediator.Send(new SetMutedCommand(conversationId, http.User.GetUserId(), body.IsMuted));
            return Results.NoContent();
        })
        .ReturnsNoContent(
            StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden);

        // ##### Keys #####
        // The keys of a conversation are made in a member's browser and sealed for each member; the server stores and hands them out
        // and checks that every member has one. They share the allowance of the other key endpoints.

        // The epochs the caller has an envelope for, oldest first. A member who left can still ask: they keep the keys of the periods
        // they were in.
        conversations.MapGet("/{conversationId:guid}/key-epochs", async (
            Guid conversationId, int? from, int? limit, HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(new GetKeyEpochsQuery(
                conversationId, http.User.GetUserId(), from ?? 1, limit ?? GetKeyEpochsQueryValidator.DefaultLimit));
            return Results.Ok(result);
        })
        .RequireRateLimiting(RateLimitPolicies.Keys)
        .Returns<KeyEpochPageDto>(
            StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized,
            StatusCodes.Status403Forbidden, StatusCodes.Status429TooManyRequests);

        // The next epoch, for everyone who is in the conversation now. 409 with a "code" when what it was made for is out of date.
        conversations.MapPost("/{conversationId:guid}/key-epochs", async (
            Guid conversationId, EpochRequest body, HttpContext http, IMediator mediator) =>
        {
            await mediator.Send(new CreateKeyEpochCommand(conversationId, http.User.GetUserId(), body));
            return Results.NoContent();
        })
        .RequireRateLimiting(RateLimitPolicies.Keys)
        .ReturnsNoContent(
            StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden,
            StatusCodes.Status409Conflict, StatusCodes.Status429TooManyRequests);

        conversations.MapGet("/{conversationId:guid}/members", async (
            Guid conversationId, HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(new GetMembersQuery(conversationId, http.User.GetUserId()));
            return Results.Ok(result);
        })
        .Returns<List<MemberDto>>(StatusCodes.Status200OK, StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden);

        // ##### Managing a group #####
        // Direct chats have no members to manage: every one of these answers 409 for them. A caller who is not in the
        // conversation gets the 403 of every other endpoint, whatever the conversation is. Each answers with what it stored.

        conversations.MapPost("/{conversationId:guid}/members", async (
            Guid conversationId, AddMembersRequest body, HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(new AddMembersCommand(conversationId, http.User.GetUserId(), body.UserIds ?? [], body.Epoch!));
            return Results.Ok(result);
        })
        .Returns<List<MemberDto>>(
            StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized,
            StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        conversations.MapDelete("/{conversationId:guid}/members/{userId:guid}", async (
            Guid conversationId, Guid userId, HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(new RemoveMemberCommand(conversationId, http.User.GetUserId(), userId));
            return Results.Ok(result);
        })
        .Returns<List<MemberDto>>(
            StatusCodes.Status200OK, StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden,
            StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        conversations.MapPut("/{conversationId:guid}/members/{userId:guid}/role", async (
            Guid conversationId, Guid userId, ChangeMemberRoleRequest body, HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(
                new ChangeMemberRoleCommand(conversationId, http.User.GetUserId(), userId, body.Role));
            return Results.Ok(result);
        })
        .Returns<List<MemberDto>>(
            StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized,
            StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        conversations.MapPost("/{conversationId:guid}/leave", async (
            Guid conversationId, HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(new LeaveConversationCommand(conversationId, http.User.GetUserId()));
            return Results.Ok(result);
        })
        .Returns<LeaveResultDto>(
            StatusCodes.Status200OK, StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden,
            StatusCodes.Status409Conflict);

        conversations.MapPatch("/{conversationId:guid}", async (
            Guid conversationId, RenameConversationRequest body, HttpContext http, IMediator mediator) =>
        {
            var result = await mediator.Send(
                new RenameConversationCommand(conversationId, http.User.GetUserId(), body.Name));
            return Results.Ok(result);
        })
        .Returns<ConversationUpdateDto>(
            StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized,
            StatusCodes.Status403Forbidden, StatusCodes.Status409Conflict);

        return app;
    }
}
