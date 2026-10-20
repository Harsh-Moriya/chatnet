import type { AuthResponse } from './types'

// Keeps the signed-in session alive. The access token lives for minutes and exists only in memory; the
// refresh token behind it is an httpOnly cookie that scripts cannot read, so everything here works by
// asking the server to renew the session and getting a new access token back.
//
// All of it is plain TypeScript with the outside world passed in, so it can be tested with fakes: no
// network, no browser, no clock.

// What asking the server to renew the session can come to.
export type RefreshOutcome =
  // A new access token.
  | { kind: 'renewed'; response: AuthResponse }
  // The server will not renew it: no cookie, or an expired, revoked or stolen token. The session is over.
  | { kind: 'rejected' }
  // Another tab renewed the session at the same moment and its new cookie is already in the browser.
  // Trying again will work.
  | { kind: 'busy' }
  // The server could not be reached or failed. The session may be perfectly good.
  | { kind: 'unavailable' }

export type RefreshResult = 'renewed' | 'ended' | 'unavailable'

export type SessionStatus = 'unknown' | 'authenticated' | 'anonymous'

export interface SessionDeps {
  requestRefresh: () => Promise<RefreshOutcome>
  requestLogout: () => Promise<void>
  getAuth: () => { status: SessionStatus; token: string | null; tokenExpiresAt: number | null }
  applyAuth: (response: AuthResponse) => void
  clearAuth: () => void
  now: () => number
  // Runs the work while no other tab is doing the same. Two tabs that refresh at once send the same
  // cookie, and only one of them can win.
  runExclusively: <T>(work: () => Promise<T>) => Promise<T>
  wait: (ms: number) => Promise<void>
  // Tells the other tabs of this browser that the user signed out.
  announceSignedOut: () => void
}

// A token this close to its end is renewed before it is used, so a request does not set off with a token
// that expires on the way. Well above the difference between this clock and the server's.
export const RENEW_MARGIN_MS = 60_000

// How many times, and how long to wait between them, when another tab renewed the session first.
export const BUSY_RETRIES = 3
export const BUSY_WAIT_MS = 250

// Sign-out asks the server to end the session, but does not make the person wait for it for long.
export const LOGOUT_WAIT_MS = 3000

export function createSessionManager(deps: SessionDeps) {
  let inFlight: Promise<RefreshResult> | null = null
  let started: Promise<void> | null = null

  // One renewal at a time in this tab, however many requests find the token expired together.
  function refresh(): Promise<RefreshResult> {
    inFlight ??= deps
      .runExclusively(renewWithRetries)
      .finally(() => {
        inFlight = null
      })

    return inFlight
  }

  async function renewWithRetries(): Promise<RefreshResult> {
    for (let attempt = 0; attempt <= BUSY_RETRIES; attempt++) {
      const outcome = await deps.requestRefresh()

      switch (outcome.kind) {
        case 'renewed':
          deps.applyAuth(outcome.response)
          return 'renewed'

        case 'rejected':
          deps.clearAuth()
          return 'ended'

        case 'unavailable':
          return 'unavailable'

        case 'busy':
          await deps.wait(BUSY_WAIT_MS * (attempt + 1))
      }
    }

    // Still being renewed by someone else after every try. Not a verdict on the session.
    return 'unavailable'
  }

  // Run once when the page loads. The access token does not survive a reload, but the cookie does, so
  // the first thing to do is ask for a new one. Until that is answered nobody knows whether the person is
  // signed in, which is why there is a third status besides signed in and signed out.
  function start(): Promise<void> {
    started ??= refresh().then((result) => {
      // The server cannot be reached: show the sign-in page. The cookie is untouched, so a reload once
      // the server is back signs the person in again.
      if (result === 'unavailable') deps.clearAuth()
    })

    return started
  }

  // A token that is good to use right now, or null when nobody is signed in. Renews the session first
  // when the token is close to its end. Throws only when it needs a new token and the server cannot be
  // reached, which a caller treats like any other network failure.
  async function getAccessToken(): Promise<string | null> {
    let auth = deps.getAuth()

    if (auth.status === 'unknown') {
      await start()
      auth = deps.getAuth()
    }

    if (auth.status !== 'authenticated' || auth.token === null) return null

    if (auth.tokenExpiresAt !== null && auth.tokenExpiresAt - deps.now() > RENEW_MARGIN_MS) return auth.token

    const result = await refresh()
    if (result === 'ended') return null

    const current = deps.getAuth()
    if (result === 'renewed') return current.token

    // The server could not be reached. A token that still has some life is better than nothing.
    if (current.token !== null && current.tokenExpiresAt !== null && current.tokenExpiresAt > deps.now()) {
      return current.token
    }

    throw new Error('The session could not be renewed.')
  }

  // Ends the session on the server and here. The local state is cleared even when the server cannot be
  // reached: the person asked to be signed out, and staying on screen would be the wrong answer to that.
  async function signOut(): Promise<void> {
    await Promise.race([deps.requestLogout().catch(() => {}), deps.wait(LOGOUT_WAIT_MS)])

    deps.clearAuth()
    deps.announceSignedOut()
  }

  // Another tab signed out. The server has already ended the session; only this tab's memory is left.
  function handleRemoteSignOut() {
    deps.clearAuth()
  }

  return { start, refresh, getAccessToken, signOut, handleRemoteSignOut }
}

export type SessionManager = ReturnType<typeof createSessionManager>
