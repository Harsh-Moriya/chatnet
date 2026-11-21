# Calls

One-to-one audio and video calls in a direct conversation. The media goes between the two browsers (or through the relay, see [deployment](../operations/deployment.md#calls-the-turn-server)); the server decides who may call whom, keeps the state of a call that is ringing or running, relays the signals the two sides use to set the media up, ends the call when it must, and writes the call into the conversation when it is over. This page covers the server's side (the methods and events a client uses and the rules behind them) and what a client has to do for a call to be safe and to work; the web client is the reference implementation, in `frontend/src/features/calls`.

## The life of a call

1. **Start.** `StartCall(conversationId, media)` in a direct conversation. The caller gets the call's id, a rejoin token, the time the ring ends and the servers to build the peer connection with (`iceServers`, with a short-lived credential for the relay when there is one). The caller does **not** make an offer yet.
2. **Ring.** Every open connection of the other person gets `IncomingCall`, and so does a connection that opens while the call is still ringing. A person with no connection at all is alerted by push, if they have allowed it and have not muted the conversation. The ring lasts 30 seconds (`Calls:RingTimeout`), counted by the server: a caller who closed the tab still ends the call, which is recorded as cancelled and which the callee sees as a missed call.
3. **Answer.** `AcceptCall(callId)` from any one of the callee's connections. The first to arrive gets the call; the others are refused with `call-not-ringing`. Everyone, both people's connections, gets `CallAnswered`: the caller's connection then makes its offer, and a connection of the callee that is still ringing stops, because another one answered.
4. **Signal.** The sides exchange `SendCallSignal(callId, type, payload)` with `type` one of `Offer`, `Answer` and `Candidate`. The server hands each to the other side's connection (`CallSignal`) and reads none of it. Only the two connections that are the call's sides may send, only while the call is active, and a signal is at most 16 KiB (`Calls:MaxSignalBytes`); a call carries at most 600 (`Calls:MaxSignalsPerCall`). `SendCallSignal` answers true when the signal was handed on and false when it was not: the call is not active (not answered yet, or already over) or the other side is away (see below).
5. **End.** `EndCall(callId, failed)` from either person, or the server when the call is due. Every connection of both people gets `CallEnded` with the reason. The call is recorded in the conversation, see [messaging](messaging.md#calls-in-the-timeline).

A call that does not exist, or that somebody else is already ending, is not an error for `EndCall`: a hang-up that arrives twice is the same hang-up.

## What a client does

The server relays signals and trusts nobody's media. The client is what makes the call private, so these steps are part of the contract and not only of the web client.

**Before it asks.** A caller first makes sure it can call: it holds, or can fetch, the identity key of the other person (a person who has not set up encryption cannot be called, and a changed key must be accepted after comparing the safety number), and it opens the microphone (and the camera for a video call). A refused permission starts no call, so nobody rings for a call that could not carry sound. The callee does the same before `AcceptCall`. A video call can be answered as audio only.

**The caller** calls `StartCall`, plays a ringing tone, and waits. The offer is made only when `CallAnswered` arrives, so no session description, and with it no network address, reaches somebody who did not answer. The caller builds the peer connection from `iceServers`, adds its tracks, creates the offer, signs the fingerprints (see [the call signature](../architecture/encryption-format.md#call-signature)) and sends `Offer` with `{"sdp","signature"}`.

**The callee** calls `AcceptCall`, builds the peer connection from the `iceServers` it returned and waits for the `Offer`. The offer can arrive before `AcceptCall` has returned, and `CallAnswered` can arrive at the answering connection itself before then (it goes to every connection of both people): a client holds early signals and handles them in order, and does not take its own `CallAnswered` as "answered elsewhere". On the offer it verifies, sets the remote description, creates the answer, signs it as the callee and sends `Answer`.

**Verifying.** The receiver of an `Offer` or `Answer` checks the signature with the identity key of the other person, with the call, the conversation, both people and the role as the signed input. If the key is missing, has changed, or the signature does not verify, the client ends the call with `EndCall(callId, failed: true)` and tells the person that the call could not be verified. Nothing is played before the check passes.

**Candidates** are sent as `Candidate` with the JSON of the ICE candidate (`candidate`, `sdpMid`, `sdpMLineIndex`, `usernameFragment`) as it is found, but only after the `Offer` or `Answer` they belong to has been sent: a side that restarts ICE gathers candidates with new credentials as soon as it sets its offer, and the other side must not get them for a description it does not have yet. The server relays a connection's signals in order, so a receiver can add each candidate as it arrives. A `Candidate` that does not decode is ignored, an `Offer` or `Answer` that does not decode counts as one that could not be verified, and a `Candidate` is never signed.

**While it runs.** Muting and turning the camera off only disable the track, so the other side hears silence or sees a still frame and the connection is untouched; changing the microphone or camera replaces the track being sent. `CallEnded` ends everything: tracks stopped, the connection closed, the ring stopped.

**When the media drops.** A network change breaks the media path while the hub connection may stay up or drop too. Only the **caller** repairs it, so the two sides never offer at once. When the connection reports `disconnected` the caller waits 2 seconds (a short drop often heals by itself) and then restarts ICE; on `failed` it restarts at once; it tries again every 4 seconds until the media is back, at most 20 times in a call. A restart is a new `Offer` made with the ICE restart option and the servers from the latest `iceServers`, signed and verified exactly like the first, and the callee answers it like any offer. The call screen says it is reconnecting. If the media is not up 15 seconds after it went down, the client ends the call with `failed: true`.

**While the call is being set up.** The caller also offers again every 4 seconds until the answer has been applied: `SendCallSignal` answering true means the signal was handed to the other side's connection, not that it arrived, and a signal handed to a connection that was just closing is lost. A call that is not connected 15 seconds after it was answered is ended with `failed: true` by either side, whatever state its connection reports.

**After a hub reconnect.** A client in a call calls `RejoinCall` with its token. The answer carries the call's `state`: `Ringing`, `Active` or `Ending`. The refusals `call-not-found`, `call-ended`, `not-in-the-call` and `bad-token` end the call on the client's side; any other (`calls-unavailable`) does not, and the client may try again. Signals sent while a side was away did not arrive: a caller that was still ringing and is told the call is `Active` missed `CallAnswered`, so it takes the call as answered and makes its offer; a caller whose media is not up offers again, with the `iceServers` the rejoin returned; a call whose media stayed up through the drop needs nothing. A signal sent from the new connection before `RejoinCall` is refused with `not-in-the-call`, not answered false.

**When the page closes.** A page that is closing or reloading sends `EndCall` for a call its person made or answered, so the other person is told at once instead of after the 10-second grace. The server finishes ending the call even though the connection closes right after the method arrives. A callee's page that is only ringing is left alone (nothing was answered, and the page that opens next is told about it); a caller's call that is still ringing is ended, which cancels it. Nothing about a call survives a reload: the rejoin token is kept in memory only.

**When a microphone or camera goes away.** A track that ends by itself (a headset unplugged) is replaced by the default device of its kind, a muted microphone stays muted, and the person is told; with no device left the call goes on without it, and a lost camera turns the camera off. A device plugged in later is not switched to.

**Several devices.** `IncomingCall` reaches each device the callee has open. A device that sees `CallAnswered` for a call it is still ringing for, without having accepted it, stops ringing; it does not take part in the call.

**Showing the call.** The web client shows a ringing call as a dialog that is answered with its own buttons (Escape and a click outside do nothing, and no button has the focus when it opens, so a key press meant for something else cannot decline or answer it), and a call in progress full screen, which a minimise button puts away as a bar above the conversations (who it is with, the time, mute and hang up; the name brings the full screen back), so messages can be read and sent during the call. Escape does not minimise or end a call. Accept is green, and decline, cancel and hang up are red. A page that is open but hidden raises an operating system notification for an incoming call, and one that is closed is reached by the push alert described above; both stay until they are used or dismissed.

## How it ended

The server decides the reason, from the state the call is in when it ends:

| Reason | When |
|---|---|
| `Cancelled` | the caller ended it while it rang, or the caller's connection dropped while it rang and did not come back |
| `Declined` | the callee ended it while it rang (from any connection) |
| `Missed` | the ring ran out |
| `Busy` | the callee was in another call when this one was started: the call never rings, and the caller is refused with `callee-busy` |
| `Completed` | either person hung up an answered call |
| `Failed` | a person said the media failed (`failed: true`), or a side of an answered call was lost: its connection dropped and did not come back, or the API instance holding it stopped |

A call that was missed or cancelled, ended on a busy line, or failed before it was answered counts as unread for the callee and alerts them if they were away (`isMissed` in the call, see messaging). A `Failed` call that was answered is shown as "Dropped"; its end time is when the server noticed, which for a lost side can be up to the lease later (`Calls:Lease`).

## One call at a time

A person is in at most one call, on any of their devices. Starting a call while in one is refused with `already-in-a-call`. Calling somebody who is in a call is refused with `callee-busy` and leaves a `Busy` call in the conversation, so they see that somebody tried. A person may start 10 calls a minute (`rate-limited`).

## When the connection drops

A network change usually drops the hub connection as well as the media path, and the reconnect has a new connection id. A side whose connection dropped is *away*: it has 10 seconds (`Calls:ReconnectGrace`) to take the call back with `RejoinCall(callId, rejoinToken)` from its new connection, which also returns fresh relay credentials. While a side is away the other side's signals are not relayed (`SendCallSignal` answers false); the side that comes back starts again. If nobody has come back when the grace ends, the call ends. The token is given once, to the connection that started or answered, and the server keeps only a hash of it; a rejoin with it replaces that side's connection.

## Several API instances

The state of a call (who is calling whom, who answered, which connection is each side, the leases) is in Redis, so it is the same on every instance, and a signal reaches the other side's connection wherever it is held. Each instance renews, every 10 seconds, the sides whose connections it holds; a side that is not renewed for 30 seconds is lost, which is how a call whose instance died ends (as `Failed`). Every instance looks once a second for calls that are due and ends them; Redis lets exactly one of them end a given call, and only that one records it and tells the people.

## Methods and events

Methods (a method takes every argument: SignalR has no optional ones):

| Method | Arguments | Returns | Refused with |
|---|---|---|---|
| `StartCall` | `conversationId`, `media` (`Audio` or `Video`) | `{ callId, rejoinToken, ringsUntil, iceServers }` | `not-a-member`, `not-a-direct-conversation`, `peer-unavailable` (an account was deactivated), `already-in-a-call`, `callee-busy`, `rate-limited`, `invalid-request` (a media kind that does not exist), `calls-unavailable` |
| `AcceptCall` | `callId` | `{ rejoinToken, media, callerId, iceServers }` | `call-not-found`, `call-not-ringing` (answered by another connection, ended, or the ring ran out), `not-the-callee` |
| `EndCall` | `callId`, `failed` | nothing | `call-not-found` (a call you are not in) |
| `SendCallSignal` | `callId`, `type`, `payload` | whether it was handed on | `signal-too-large`, `signal-limit`, `invalid-request` (a type that does not exist, or no payload), `not-in-the-call` (this connection is not a side of the call) |
| `RejoinCall` | `callId`, `rejoinToken` | `{ state, media, iceServers }` | `call-not-found`, `call-ended`, `not-in-the-call`, `bad-token`, `invalid-request` (no token, or one far longer than any) |

The refusal's code is the message of the `HubException`. `calls-unavailable` is any method when the live state could not be reached, and `invalid-request` is any method that is given an argument it cannot take.

| Event | Arguments | Sent to |
|---|---|---|
| `IncomingCall` | `{ callId, conversationId, callerId, callerDisplayName, media, ringsUntil }` | every connection of the callee, and a connection of theirs that opens while the call rings |
| `CallAnswered` | `callId` | every connection of both people |
| `CallSignal` | `callId`, `type`, `payload` | the other side's connection |
| `CallEnded` | `{ callId, reason, endedAt }` | every connection of both people |

`iceServers` is an array of `{ urls, username?, credential? }` as `RTCPeerConnection` takes them; `username` and `credential` are left out of a STUN entry. It is empty when no relay is configured.

## For a client that is not the web client

The web client is the reference, but a native client has to know the following, which the sections above only imply.

- **Formats.** JSON enums are their names (`Audio`, `Video`, `Offer`, `Answer`, `Candidate`), times are ISO 8601 UTC, property names are camelCase. An offer or an answer is the payload `{"sdp":"...","signature":"..."}` with the signature as unpadded base64url (64 bytes); a candidate is the JSON of `candidate`, `sdpMid`, `sdpMLineIndex` and `usernameFragment`.
- **Roles.** Only the callee acts on an `Offer` and only the caller on an `Answer`. The web client refuses a session description over 12,000 characters and a candidate over 2,000 characters (well under the server's 16 KiB) as a call that could not be verified, and it never sends an end-of-candidates marker.
- **Reading a refusal.** A refused method raises the SignalR library's invocation error, and the code is in its message (the web client's library words it `An unexpected error occurred invoking 'AcceptCall' on the server. HubException: call-not-ringing`). Look for the code, a lowercase word with hyphens, in the message.
- **The other person's key.** The signature is checked against the other person's identity key, which comes from `POST /api/keys/lookup` and is remembered on first use; see [encryption keys](encryption-keys.md) and [safety numbers](safety-numbers.md). A changed key is not accepted until the person has compared the safety number.
- **A long call.** The hub closes the connection when the access token ends (15 minutes by default). A client keeps the call by renewing the token, connecting again and calling `RejoinCall` within the 10-second grace, which the web client does.
- **Alerts.** The server's push is Web Push only. There is no APNs or FCM path, so a native app that is closed is not rung; it has to be connected to the hub to hear `IncomingCall`.
- **After a reconnect, in order.** Connect, `RejoinCall`, then signal. The new connection is not a side of the call until the rejoin has been accepted.

## Limits

- A call is between two people in a direct conversation. There are no group calls.
- Nothing about a call is kept after it ends but its row in the calls table and in the conversation (and, for a day, the alert row of a push that was queued): not the signals, not the relay credentials. The live state of a call expires after six hours, so a call is not meant to run longer.
- Any member of a direct conversation can ring the other, up to 10 starts a minute: there is no block, and a muted conversation silences the push but not the ring in an open page.
- A browser keeps sound off in a tab that has had no click or key press yet, so the ring can be silent there; the call screen and the operating system alert still show the call.
- A call needs a secure context (https, or `localhost`) to open the microphone: it cannot work from a LAN address over plain http.
- The server never logs or stores what a signal says (a session description and a candidate contain network addresses); its logs carry ids.
- If Redis cannot be reached, a call cannot be started, answered, ended, signalled or rejoined through the hub (`calls-unavailable`).
