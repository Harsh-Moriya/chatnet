using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Search.Models;
using ChatNET.API.Search.Persistence;
using FluentValidation;
using FluentValidation.Results;
using MediatR;

namespace ChatNET.API.Search.SearchMessages;

public class SearchMessagesHandler : IRequestHandler<SearchMessagesQuery, List<SearchHitDto>>
{
    public const int PageSize = 20;
    public const string UnknownCursor = "Unknown search cursor.";

    private readonly IConversationRepository _conversations;
    private readonly ISearchRepository _search;
    private readonly ILogger<SearchMessagesHandler> _logger;

    public SearchMessagesHandler(
        IConversationRepository conversations, ISearchRepository search, ILogger<SearchMessagesHandler> logger)
    {
        _conversations = conversations;
        _search = search;
        _logger = logger;
    }

    public async Task<List<SearchHitDto>> Handle(SearchMessagesQuery request, CancellationToken cancellationToken)
    {
        // Naming a conversation is a claim to have been in it. Someone who left may search what was said while they were in it,
        // as they may read it (the window the search applies), and anyone else gets the answer every endpoint gives.
        if (request.ConversationId is { } conversationId
            && await _conversations.GetMemberWindowAsync(conversationId, request.UserId, cancellationToken) is null)
        {
            _logger.LogWarning(
                "User {UserId} tried to search conversation {ConversationId} without being a member",
                request.UserId, conversationId);

            throw new ForbiddenException(ConversationErrors.NotAMember);
        }

        MessageCursor? after = null;
        if (request.Before is { } beforeId)
        {
            after = await _search.FindCursorAsync(request.UserId, request.ConversationId, beforeId, cancellationToken);

            if (after is null)
                throw new ValidationException([new ValidationFailure(nameof(request.Before), UnknownCursor)]);
        }

        var text = SearchQueryText.From(request.Q!);
        if (!text.HasTerms)
            return [];

        var hits = await _search.SearchAsync(
            request.UserId, request.ConversationId, text, after, PageSize, cancellationToken);

        // The count and who asked, never what was asked: the text of a search is as private as a message.
        _logger.LogInformation("User {UserId} searched and found {HitCount} messages", request.UserId, hits.Count);

        return hits;
    }
}
