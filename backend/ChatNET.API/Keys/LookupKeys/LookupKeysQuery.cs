using ChatNET.API.Keys.Models;
using MediatR;

namespace ChatNET.API.Keys.LookupKeys;

// The people whose current public keys are wanted: the members a conversation key is about to be sealed for.
public record LookupKeysQuery(IReadOnlyList<Guid> UserIds) : IRequest<LookedUpKeysDto>;
