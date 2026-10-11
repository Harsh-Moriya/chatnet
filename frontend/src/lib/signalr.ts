import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr'
import { useAuthStore } from '../features/auth/store/authStore'
import { isTokenExpired } from './jwt'

// Same idea as the API base URL in api.ts: relative in development (Vite proxies /hubs to the
// backend, with WebSocket support), overridable for a build that talks to another origin.
const HUB_URL = import.meta.env.VITE_HUB_URL ?? '/hubs/chat'

// How long to wait before the next attempt, given how many have already failed: at once, then
// 2s, 5s and 10s, then every 30s for ever. The client never gives up, so a laptop that was closed
// overnight reconnects when it is opened; the ceiling keeps a struggling server from being hammered.
export function reconnectDelay(failedAttempts: number): number {
  return [0, 2000, 5000, 10000][failedAttempts] ?? 30000
}

// The delay before the next reconnect attempt, or null to stop. Once the token has expired the server
// will refuse every attempt, so retrying only fills the log and leaves the user looking at a banner;
// stopping hands the decision to the caller, which signs the user out.
export function nextRetryDelay(failedAttempts: number, sessionExpired: boolean): number | null {
  return sessionExpired ? null : reconnectDelay(failedAttempts)
}

// Builds the connection without starting it, so the caller can register its event handlers first.
// A handler registered after start() can miss an event that arrives straight away.
export function createChatConnection(): HubConnection {
  return (
    new HubConnectionBuilder()
      // Called for the negotiate request and again for the WebSocket, where the token travels in
      // the query string because a browser cannot set headers on a WebSocket upgrade. It reads the
      // store every time, so a reconnect after a new login uses the new token.
      .withUrl(HUB_URL, { accessTokenFactory: () => useAuthStore.getState().token ?? '' })
      // Applies after a connection that worked has dropped. The first connect is retried by hand.
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (context) =>
          nextRetryDelay(context.previousRetryCount, isTokenExpired(useAuthStore.getState().token)),
      })
      .configureLogging(LogLevel.Warning)
      .build()
  )
}
