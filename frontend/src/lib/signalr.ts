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
        nextRetryDelayInMilliseconds: (context) => reconnectDelay(context.previousRetryCount),
      })
      .configureLogging(LogLevel.Warning)
      .build()
  )
}
