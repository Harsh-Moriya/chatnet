import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr'
import { session } from '../features/auth/session'

// Same idea as the API base URL in api.ts: relative in development (Vite proxies /hubs to the
// backend, with WebSocket support), overridable for a build that talks to another origin.
const HUB_URL = import.meta.env.VITE_HUB_URL ?? '/hubs/chat'

// How long to wait before the next attempt, given how many have already failed: at once, then
// 2s, 5s and 10s, then every 30s for ever. The client never gives up, so a laptop that was closed
// overnight reconnects when it is opened; the ceiling keeps a struggling server from being hammered.
export function reconnectDelay(failedAttempts: number): number {
  return [0, 2000, 5000, 10000][failedAttempts] ?? 30000
}

// Whether the server answered a connection attempt with 401: it does not accept the token it was given.
//
// The library does not hand over an error with a status. When the negotiate request is refused it turns the
// HTTP error into a plain Error whose message ends in "Status code '401'" (checked against the real library:
// the error is a FailedToNegotiateWithServerError with no statusCode, and nothing else on it names the
// status). Matching that text is the only way to tell a refused token from a server that cannot be reached
// or one that is failing (a 503 says "Status code '503'"). An error that does carry a statusCode is
// recognised too.
export function isUnauthorized(error: unknown): boolean {
  if (typeof error !== 'object' || error === null) return false

  const { statusCode, message } = error as { statusCode?: unknown; message?: unknown }

  return statusCode === 401 || (typeof message === 'string' && message.includes("Status code '401'"))
}

// The delay before the next reconnect attempt. When the last attempt was refused with a 401, the token the
// client holds is one the server will not take, although the client thinks it is good: its clock is behind,
// or the server's signing key was replaced. Retrying with it can never work, and nothing else on the page
// is guaranteed to notice, so the session is renewed now and the next attempt (after the delay) uses the
// new token.
export function nextReconnectDelay(failedAttempts: number, lastError: unknown, renewSession: () => void): number {
  if (isUnauthorized(lastError)) renewSession()

  return reconnectDelay(failedAttempts)
}

// Builds the connection without starting it, so the caller can register its event handlers first.
// A handler registered after start() can miss an event that arrives straight away.
export function createChatConnection(): HubConnection {
  return (
    new HubConnectionBuilder()
      // Called for the negotiate request and again for the WebSocket, where the token travels in
      // the query string because a browser cannot set headers on a WebSocket upgrade. It asks the
      // session manager every time, so a connection made after the access token ran out (the server
      // closes a connection at its token's expiry) gets a renewed one. When the session cannot be
      // renewed it is ended, the user is signed out and the chat, with this connection, goes away.
      .withUrl(HUB_URL, { accessTokenFactory: async () => (await session.getAccessToken()) ?? '' })
      // Applies after a connection that worked has dropped. The first connect is retried by hand.
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (context) =>
          nextReconnectDelay(context.previousRetryCount, context.retryReason, () => void session.refresh()),
      })
      .configureLogging(LogLevel.Warning)
      .build()
  )
}
