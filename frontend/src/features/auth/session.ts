import axios from 'axios'
import { createSessionManager, type RefreshOutcome } from './sessionManager'
import { useAuthStore } from './store/authStore'
import type { AuthResponse } from './types'

// The real session manager: the pure logic in sessionManager.ts joined to the network, the store and
// the browser.

const BASE_URL = import.meta.env.VITE_API_URL ?? '/api'

// The refresh and sign-out endpoints refuse a request without this header (see RefreshCookie on the
// server). A page on another site cannot add it to a request without the server's permission.
const CLIENT_HEADER = { 'X-Requested-With': 'ChatNET' }

// A separate client from the one in lib/api.ts, which would try to attach and renew a token for these
// calls too. withCredentials sends the cookie when the API is on another origin than the page.
const sessionApi = axios.create({ baseURL: BASE_URL, withCredentials: true, headers: CLIENT_HEADER })

async function requestRefresh(): Promise<RefreshOutcome> {
  try {
    const response = await sessionApi.post<AuthResponse>('/auth/refresh')
    return { kind: 'renewed', response: response.data }
  } catch (error) {
    if (!axios.isAxiosError(error) || !error.response) return { kind: 'unavailable' }

    switch (error.response.status) {
      case 401:
      case 403:
        return { kind: 'rejected' }
      case 409:
        return { kind: 'busy' }
      default:
        // 429, 5xx: the server is there but cannot do it now.
        return { kind: 'unavailable' }
    }
  }
}

// Tabs of one browser share the cookie but not memory. A lock held by name across all of them lets
// one refresh at a time happen; the next tab to get the lock sends the cookie the first one just
// received. Where the browser has no locks the server's tolerance for a token that was replaced a moment
// ago covers the same case.
function runExclusively<T>(work: () => Promise<T>): Promise<T> {
  return typeof navigator !== 'undefined' && navigator.locks
    ? navigator.locks.request('chatnet-session', work)
    : work()
}

const SIGNED_OUT = 'signed-out'

// Created when the session starts and not when this file is loaded, so importing it (in a test, say) opens
// nothing. Null where the browser has no BroadcastChannel; tabs then simply do not hear about each other.
let channel: BroadcastChannel | null = null

export const session = createSessionManager({
  requestRefresh,
  requestLogout: async () => {
    await sessionApi.post('/auth/logout')
  },
  getAuth: () => {
    const { status, token, tokenExpiresAt } = useAuthStore.getState()
    return { status, token, tokenExpiresAt }
  },
  applyAuth: (response) => useAuthStore.getState().setAuth(response),
  clearAuth: () => useAuthStore.getState().clearAuth(),
  now: () => Date.now(),
  runExclusively,
  wait: (ms) => new Promise((resolve) => setTimeout(resolve, ms)),
  announceSignedOut: () => channel?.postMessage(SIGNED_OUT),
})

// Called once, before the app renders. Sessions used to be kept in localStorage; the old entry is removed
// so a token from before the change does not stay behind in the browser.
export function startSession(): Promise<void> {
  try {
    localStorage.removeItem('chatnet-auth')
  } catch {
    // Storage can be unavailable (private windows, blocked site data). There is nothing to clean up then.
  }

  if (channel === null && typeof BroadcastChannel !== 'undefined') {
    channel = new BroadcastChannel('chatnet-auth')
    channel.addEventListener('message', (event: MessageEvent<unknown>) => {
      if (event.data === SIGNED_OUT) session.handleRemoteSignOut()
    })
  }

  return session.start()
}
