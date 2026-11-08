using ChatNET.API.Common.Exceptions;
using ChatNET.API.Common.Time;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;
using MediatR;

namespace ChatNET.API.Conversations.GetKeyEpochs;

public class GetKeyEpochsHandler : IRequestHandler<GetKeyEpochsQuery, KeyEpochPageDto>
{
    private readonly IConversationRepository _conversations;
    private readonly IKeyEpochRepository _epochs;

    public GetKeyEpochsHandler(IConversationRepository conversations, IKeyEpochRepository epochs)
    {
        _conversations = conversations;
        _epochs = epochs;
    }

    // A member, or someone who was one: a person who left keeps the keys of the periods they were in, so what they could read
    // stays readable to them. Which epochs those are is not decided here but by the envelopes: a person has one only for the
    // epochs that were made for them.
    public async Task<KeyEpochPageDto> Handle(GetKeyEpochsQuery request, CancellationToken cancellationToken)
    {
        if (await _conversations.GetMemberWindowAsync(request.ConversationId, request.UserId, cancellationToken) is null)
            throw new ForbiddenException(ConversationErrors.NotAMember);

        var epochs = await _epochs.GetForMemberAsync(
            request.ConversationId, request.UserId, request.From, request.Limit + 1, cancellationToken);

        return new KeyEpochPageDto(
            epochs.Take(request.Limit).Select(e => e with { CreatedAt = e.CreatedAt.TruncateToMicroseconds() }).ToList(),
            epochs.Count > request.Limit);
    }
}
