# Calls

One-to-one audio and video calls in a direct conversation. The media goes between the two browsers (or through the relay, see [deployment](../operations/deployment.md#calls-the-turn-server)); the server decides who may call whom, keeps the state of a call that is ringing or running, relays the signals the two sides use to set the media up, ends the call when it must, and writes the call into the conversation when it is over. This page is the server's side: the methods and events a client uses and the rules behind them.

## The life of a call

1. **Start.** `StartCall(conversationId, media)` in a direct conversation. The caller gets the call's id, a rejoin token, the time the ring ends and the servers to build the peer connection with (`iceServers`, with a short-lived credential for the relay when there is one). The caller does **not** make an offer yet.
2. **Ring.** Every open connection of the other person gets `IncomingCall`, and so does a connection that opens while the call is still ringing. A person with no connection at all is alerted by push, if they have allowed it and have not muted the conversation. The ring lasts 30 seconds (`Calls:RingTimeout`), counted by the server: a caller who closed the tab still ends in a missed call.
3. **Answer.** `AcceptCall(callId)` from any one of the callee's connections. The first to arrive gets the call; the others are refused with `call-not-ringing`. Everyone, both people's connections, gets `CallAnswered`: the caller's connection then makes its offer, and a connection of the callee that is still ringing stops, because another one answered.
4. **Signal.** The sides exchange `SendCallSignal(callId, type, payload)` with `type` one of `Offer`, `Answer` and `Candidate`. The server hands each to the other side's connection (`CallSignal`) and reads none of it. Only the two connections that are the call's sides may send, only while the call is active, and a signal is at most 16 KiB (`Calls:MaxSignalBytes`); a call carries at most 600 (`Calls:MaxSignalsPerCall`). `SendCallSignal` answers true when the signal was handed on and false when it was not: the call is not active (not answered yet, or already over) or the other side is away (see below).
5. **End.** `EndCall(callId, failed)` from either person, or the server when the call is due. Every connection of both people gets `CallEnded` with the reason. The call is recorded in the conversation, see [messaging](messaging.md#calls-in-the-timeline).

A call that does not exist, or that somebody else is already ending, is not an error for `EndCall`: a hang-up that arrives twice is the same hang-up.

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

A call that was missed or cancelled, ended on a busy line, or failed before it was answered counts as unread for the callee and alerts them if they were away (`isMissed` in the call, see messaging). A `Failed` call is shown as "Dropped"; its end time is when the server noticed, which for a lost side can be up to the lease later (`Calls:Lease`).

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
| `StartCall` | `conversationId`, `media` (`Audio` or `Video`) | `{ callId, rejoinToken, ringsUntil, iceServers }` | `not-a-member`, `not-a-direct-conversation`, `peer-unavailable` (an account was deactivated), `already-in-a-call`, `callee-busy`, `rate-limited`, `calls-unavailable` |
| `AcceptCall` | `callId` | `{ rejoinToken, media, callerId, iceServers }` | `call-not-found`, `call-not-ringing` (answered by another connection, ended, or the ring ran out), `not-the-callee` |
| `EndCall` | `callId`, `failed` | nothing | `call-not-found` (a call you are not in) |
| `SendCallSignal` | `callId`, `type`, `payload` | whether it was handed on | `signal-too-large`, `signal-limit`, `not-in-the-call` (this connection is not a side of the call) |
| `RejoinCall` | `callId`, `rejoinToken` | `{ state, media, iceServers }` | `call-not-found`, `call-ended`, `not-in-the-call`, `bad-token` |

The refusal's code is the message of the `HubException`. `calls-unavailable` is any method when the live state could not be reached.

| Event | Arguments | Sent to |
|---|---|---|
| `IncomingCall` | `{ callId, conversationId, callerId, callerDisplayName, media, ringsUntil }` | every connection of the callee, and a connection of theirs that opens while the call rings |
| `CallAnswered` | `callId` | every connection of both people |
| `CallSignal` | `callId`, `type`, `payload` | the other side's connection |
| `CallEnded` | `{ callId, reason, endedAt }` | every connection of both people |

`iceServers` is an array of `{ urls, username?, credential? }` as `RTCPeerConnection` takes them; `username` and `credential` are left out of a STUN entry. It is empty when no relay is configured.

## Limits

- A call is between two people in a direct conversation. There are no group calls.
- Nothing about a call is kept after it ends but the row in the conversation: not the signals, not the relay credentials.
- The server never logs or stores what a signal says (a session description and a candidate contain network addresses); its logs carry ids.
- If Redis cannot be reached, a call cannot be started, answered, ended, signalled or rejoined through the hub (`calls-unavailable`).
