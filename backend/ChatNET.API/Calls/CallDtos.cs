using ChatNET.API.Calls.Ice;
using ChatNET.API.Calls.Models;

namespace ChatNET.API.Calls;

// What a signal is: the offer and the answer carry a session description (SDP), a candidate one ICE candidate. The server relays the text
// and reads none of it.
public enum CallSignalType
{
    Offer,
    Answer,
    Candidate,
}

// The answer to StartCall: the call, the token this connection rejoins with after a reconnect (shown once; the server keeps a hash), when
// the ring ends, and the servers to build the peer connection with.
public sealed record CallStartedDto(Guid CallId, string RejoinToken, DateTime RingsUntil, IReadOnlyList<IceServerDto> IceServers);

// The answer to AcceptCall.
public sealed record CallAcceptedDto(string RejoinToken, CallMedia Media, Guid CallerId, IReadOnlyList<IceServerDto> IceServers);

// The answer to RejoinCall: where the call stands, and new credentials for the relay (the old ones may have run out).
public sealed record CallRejoinedDto(CallState State, CallMedia Media, IReadOnlyList<IceServerDto> IceServers);

// Pushed to every connection of the person being called, and to a connection of theirs that opens while the call rings.
public sealed record IncomingCallDto(
    Guid CallId, Guid ConversationId, Guid CallerId, string CallerDisplayName, CallMedia Media, DateTime RingsUntil);

// Pushed to every connection of both people when a call ends, whoever ended it. The reason is the one recorded in the call log.
public sealed record CallEndedDto(Guid CallId, CallEndReason Reason, DateTime EndedAt);
