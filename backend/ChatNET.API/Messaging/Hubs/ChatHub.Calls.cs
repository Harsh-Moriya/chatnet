using ChatNET.API.Calls;
using ChatNET.API.Calls.Models;
using ChatNET.API.Common.Auth;

namespace ChatNET.API.Messaging.Hubs;

// The methods of a call. A person starts one in a direct conversation, the other person's tabs ring, one of them answers, the two sides
// exchange signals (an offer, an answer, ICE candidates) that the server relays without reading, and either hangs up. The rules are in
// CallService; this only says who is asking: always the person in the token and this connection, never an argument.
//
// A refused method throws a HubException whose message is one of CallErrors. A method has no optional parameters (SignalR does not support
// them), so a client passes every argument.
public partial class ChatHub
{
    // Starts a call to the other person of a direct conversation. The caller's tab does not make an offer yet: that waits for CallAnswered,
    // so nothing is captured and no address is sent to somebody who never answers. Keep the rejoin token: it is shown once.
    public async Task<CallStartedDto> StartCall(Guid conversationId, CallMedia media)
    {
        var started = await _calls.StartAsync(
            Context.User!.GetUserId(), Context.ConnectionId, conversationId, media, Context.ConnectionAborted);

        ConnectionState.SetCall(Context.Items, started.CallId);
        return started;
    }

    // Answers a call that is ringing for this person. Of several tabs answering at once, one gets the call and the others are refused with
    // call-not-ringing.
    public async Task<CallAcceptedDto> AcceptCall(Guid callId)
    {
        var accepted = await _calls.AcceptAsync(Context.User!.GetUserId(), Context.ConnectionId, callId, Context.ConnectionAborted);

        ConnectionState.SetCall(Context.Items, callId);
        return accepted;
    }

    // Hangs up, cancels or declines: the server works out which from the state of the call and who asks. `failed` says the media could not
    // be connected or broke, which is recorded as a failed call. Ending a call that is already over is not an error.
    public async Task EndCall(Guid callId, bool failed)
    {
        await _calls.EndAsync(Context.User!.GetUserId(), callId, failed, Context.ConnectionAborted);

        if (ConnectionState.CallOf(Context.Items) == callId)
            ConnectionState.SetCall(Context.Items, null);
    }

    // Hands a signal to the other side of the call, if this connection is a side of it and the call is active. True when it was handed on;
    // false when the other side is away (a network change), which starts again once it rejoins.
    public Task<bool> SendCallSignal(Guid callId, CallSignalType type, string payload)
        => _calls.RouteSignalAsync(Context.User!.GetUserId(), Context.ConnectionId, callId, type, payload, Context.ConnectionAborted);

    // A new connection of a person whose side of a call dropped takes the side back with the token it was given, and gets new credentials for
    // the relay. Whatever the media does after that (an ICE restart) is for the two sides to settle through signals.
    public async Task<CallRejoinedDto> RejoinCall(Guid callId, string rejoinToken)
    {
        var rejoined = await _calls.RejoinAsync(
            Context.User!.GetUserId(), Context.ConnectionId, callId, rejoinToken, Context.ConnectionAborted);

        ConnectionState.SetCall(Context.Items, callId);
        return rejoined;
    }
}
