import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  BUSY_RETRIES,
  BUSY_WAIT_MS,
  createSessionManager,
  LOGOUT_WAIT_MS,
  RENEW_MARGIN_MS,
  type RefreshOutcome,
  type SessionStatus,
} from './sessionManager'
import type { AuthResponse } from './types'

// ##### A stand-in for the network, the store and the browser #####
// The session manager is given everything it touches, so these tests run without a server, a store or a
// clock. They record what it asked for and let each test decide what the server answers.

const T0 = 1_000_000_000_000

function response(token: string, expiresInMs = 15 * 60_000, userId = 'u1'): AuthResponse {
  return {
    token,
    userId,
    userName: 'alice',
    displayName: 'Alice',
    accessTokenExpiresAt: new Date(T0 + expiresInMs).toISOString(),
  }
}

function setup(initial: { status?: SessionStatus; token?: string | null; expiresInMs?: number; userId?: string } = {}) {
  let status: SessionStatus = initial.status ?? 'unknown'
  let token: string | null = initial.token ?? null
  let userId: string | null = token ? (initial.userId ?? 'u1') : null
  let tokenExpiresAt: number | null = token ? T0 + (initial.expiresInMs ?? 15 * 60_000) : null
  let now = T0

  const outcomes: RefreshOutcome[] = []
  const calls: string[] = []
  let logout: () => Promise<void> = () => Promise.resolve()
  let lockHeld = false

  const deps = {
    requestRefresh: vi.fn(async (): Promise<RefreshOutcome> => {
      calls.push(lockHeld ? 'refresh:locked' : 'refresh:unlocked')
      return outcomes.shift() ?? { kind: 'unavailable' }
    }),
    requestLogout: vi.fn(() => logout()),
    getAuth: () => ({ status, token, tokenExpiresAt, userId }),
    applyAuth: vi.fn((r: AuthResponse) => {
      status = 'authenticated'
      token = r.token
      userId = r.userId
      tokenExpiresAt = Date.parse(r.accessTokenExpiresAt)
    }),
    clearAuth: vi.fn(() => {
      status = 'anonymous'
      token = null
      userId = null
      tokenExpiresAt = null
    }),
    now: () => now,
    runExclusively: async <T,>(work: () => Promise<T>): Promise<T> => {
      calls.push('lock')
      lockHeld = true
      try {
        return await work()
      } finally {
        lockHeld = false
      }
    },
    wait: vi.fn(() => Promise.resolve()),
    announceSignedOut: vi.fn(),
  }

  return {
    deps,
    manager: createSessionManager(deps),
    calls,
    answer: (...next: RefreshOutcome[]) => outcomes.push(...next),
    advance: (ms: number) => {
      now += ms
    },
    setLogout: (impl: () => Promise<void>) => {
      logout = impl
    },
    state: () => ({ status, token, tokenExpiresAt }),
  }
}

const renewed = (token: string, expiresInMs?: number, userId?: string): RefreshOutcome => ({
  kind: 'renewed',
  response: response(token, expiresInMs, userId),
})

beforeEach(() => {
  vi.clearAllMocks()
})

// ##### Starting #####

describe('starting', () => {
  it('signs the person in when the browser still has a working session', async () => {
    const { manager, answer, state } = setup()
    answer(renewed('t1'))

    await manager.start()

    expect(state()).toMatchObject({ status: 'authenticated', token: 't1' })
  })

  it('shows the sign-in page when there is no session to restore', async () => {
    const { manager, answer, state, deps } = setup()
    answer({ kind: 'rejected' })

    await manager.start()

    expect(state().status).toBe('anonymous')
    expect(deps.clearAuth).toHaveBeenCalledTimes(1)
  })

  it('shows the sign-in page when the server cannot be reached, without touching the cookie', async () => {
    const { manager, answer, state } = setup()
    answer({ kind: 'unavailable' })

    await manager.start()

    expect(state().status).toBe('anonymous')
  })

  it('asks the server once however many times it is started', async () => {
    const { manager, answer, deps } = setup()
    answer(renewed('t1'))

    await Promise.all([manager.start(), manager.start(), manager.start()])

    expect(deps.requestRefresh).toHaveBeenCalledTimes(1)
  })
})

// ##### Getting a token to use #####

describe('getAccessToken', () => {
  it('hands out the current token without asking the server while it has plenty of time left', async () => {
    const { manager, deps } = setup({ status: 'authenticated', token: 't1' })

    expect(await manager.getAccessToken()).toBe('t1')
    expect(deps.requestRefresh).not.toHaveBeenCalled()
  })

  it('renews the session first when the token is about to end', async () => {
    const { manager, answer, advance, deps } = setup({ status: 'authenticated', token: 't1' })
    advance(15 * 60_000 - RENEW_MARGIN_MS + 1)
    answer(renewed('t2'))

    expect(await manager.getAccessToken()).toBe('t2')
    expect(deps.requestRefresh).toHaveBeenCalledTimes(1)
  })

  it('renews it when the token has already ended', async () => {
    const { manager, answer, advance } = setup({ status: 'authenticated', token: 't1' })
    advance(60 * 60_000)
    answer(renewed('t2'))

    expect(await manager.getAccessToken()).toBe('t2')
  })

  it('does not renew a token that is just outside the margin', async () => {
    const { manager, advance, deps } = setup({ status: 'authenticated', token: 't1' })
    advance(15 * 60_000 - RENEW_MARGIN_MS - 1)

    expect(await manager.getAccessToken()).toBe('t1')
    expect(deps.requestRefresh).not.toHaveBeenCalled()
  })

  it('renews once when many requests find the token ended together', async () => {
    const { manager, answer, advance, deps } = setup({ status: 'authenticated', token: 't1' })
    advance(60 * 60_000)
    answer(renewed('t2'))

    const tokens = await Promise.all(Array.from({ length: 8 }, () => manager.getAccessToken()))

    expect(tokens).toEqual(Array(8).fill('t2'))
    expect(deps.requestRefresh).toHaveBeenCalledTimes(1)
  })

  it('returns nothing when nobody is signed in, and does not ask the server', async () => {
    const { manager, deps } = setup({ status: 'anonymous' })

    expect(await manager.getAccessToken()).toBeNull()
    expect(deps.requestRefresh).not.toHaveBeenCalled()
  })

  it('waits for the start-up answer when it is not known yet whether anyone is signed in', async () => {
    const { manager, answer, deps } = setup()
    answer(renewed('t1'))

    const token = manager.getAccessToken()
    await manager.start()

    expect(await token).toBe('t1')
    expect(deps.requestRefresh).toHaveBeenCalledTimes(1)
  })

  it('returns nothing, and signs the person out, when the server will not renew the session', async () => {
    const { manager, answer, advance, state } = setup({ status: 'authenticated', token: 't1' })
    advance(60 * 60_000)
    answer({ kind: 'rejected' })

    expect(await manager.getAccessToken()).toBeNull()
    expect(state().status).toBe('anonymous')
  })

  // The session may be perfectly good; the network is not. Throwing away the sign-in would turn a
  // dropped connection into a sign-out.
  it('keeps the session and still hands out a token that has not ended when the server cannot be reached', async () => {
    const { manager, answer, advance, state } = setup({ status: 'authenticated', token: 't1' })
    advance(15 * 60_000 - RENEW_MARGIN_MS + 1)
    answer({ kind: 'unavailable' })

    expect(await manager.getAccessToken()).toBe('t1')
    expect(state().status).toBe('authenticated')
  })

  it('fails, but keeps the session, when it needs a new token and the server cannot be reached', async () => {
    const { manager, answer, advance, state } = setup({ status: 'authenticated', token: 't1' })
    advance(60 * 60_000)
    answer({ kind: 'unavailable' })

    await expect(manager.getAccessToken()).rejects.toThrow()
    expect(state().status).toBe('authenticated')
  })

  it('asks again on the next call after a failure, instead of remembering the failure', async () => {
    const { manager, answer, advance } = setup({ status: 'authenticated', token: 't1' })
    advance(60 * 60_000)
    answer({ kind: 'unavailable' }, renewed('t2'))

    await expect(manager.getAccessToken()).rejects.toThrow()

    expect(await manager.getAccessToken()).toBe('t2')
  })
})

// ##### Another tab renewed the session first #####

describe('when another tab renewed the session at the same moment', () => {
  it('waits and tries again, because the new cookie is already in the browser', async () => {
    const { manager, answer, deps, state } = setup()
    answer({ kind: 'busy' }, renewed('t2'))

    expect(await manager.refresh()).toBe('renewed')

    expect(deps.requestRefresh).toHaveBeenCalledTimes(2)
    expect(deps.wait).toHaveBeenCalledWith(BUSY_WAIT_MS)
    expect(state().token).toBe('t2')
  })

  it('waits a little longer each time', async () => {
    const { manager, answer, deps } = setup()
    answer({ kind: 'busy' }, { kind: 'busy' }, { kind: 'busy' }, renewed('t2'))

    await manager.refresh()

    expect(deps.wait.mock.calls.map((c) => (c as unknown[])[0])).toEqual([BUSY_WAIT_MS, BUSY_WAIT_MS * 2, BUSY_WAIT_MS * 3])
  })

  it('gives up after a few tries without calling the session over', async () => {
    const { manager, answer, deps, state } = setup({ status: 'authenticated', token: 't1' })
    answer(...Array<RefreshOutcome>(BUSY_RETRIES + 1).fill({ kind: 'busy' }))

    expect(await manager.refresh()).toBe('unavailable')

    expect(deps.requestRefresh).toHaveBeenCalledTimes(BUSY_RETRIES + 1)
    expect(deps.clearAuth).not.toHaveBeenCalled()
    expect(state().status).toBe('authenticated')
  })
})

// ##### Tabs taking turns #####

describe('refreshing', () => {
  it('sends the request while holding the lock, so tabs of one browser take turns', async () => {
    const { manager, answer, calls } = setup()
    answer(renewed('t1'))

    await manager.refresh()

    expect(calls).toEqual(['lock', 'refresh:locked'])
  })

  it('holds the lock across the retries after a busy answer', async () => {
    const { manager, answer, calls } = setup()
    answer({ kind: 'busy' }, renewed('t1'))

    await manager.refresh()

    expect(calls).toEqual(['lock', 'refresh:locked', 'refresh:locked'])
  })

  it('reports an ended session once it was rejected, and a later call starts afresh', async () => {
    const { manager, answer } = setup({ status: 'authenticated', token: 't1' })
    answer({ kind: 'rejected' }, renewed('t2'))

    expect(await manager.refresh()).toBe('ended')
    expect(await manager.refresh()).toBe('renewed')
  })
})

// ##### Signing out #####

describe('signOut', () => {
  it('ends the session on the server, then here, then tells the other tabs', async () => {
    const { manager, deps } = setup({ status: 'authenticated', token: 't1' })
    const order: string[] = []
    deps.requestLogout.mockImplementation(async () => {
      order.push('server')
    })
    deps.clearAuth.mockImplementation(() => order.push('here'))
    deps.announceSignedOut.mockImplementation(() => order.push('tabs'))

    await manager.signOut()

    expect(order).toEqual(['server', 'here', 'tabs'])
  })

  it('signs out here even when the server cannot be reached', async () => {
    const { manager, setLogout, deps } = setup({ status: 'authenticated', token: 't1' })
    setLogout(() => Promise.reject(new Error('offline')))

    await manager.signOut()

    expect(deps.clearAuth).toHaveBeenCalledTimes(1)
    expect(deps.announceSignedOut).toHaveBeenCalledTimes(1)
  })

  it('does not make the person wait for a server that does not answer', async () => {
    const { manager, setLogout, deps } = setup({ status: 'authenticated', token: 't1' })
    setLogout(() => new Promise(() => {}))
    // The wait stand-in answers at once, which is what a timeout firing looks like.

    await manager.signOut()

    expect(deps.wait).toHaveBeenCalledWith(LOGOUT_WAIT_MS)
    expect(deps.clearAuth).toHaveBeenCalledTimes(1)
  })
})

describe('another tab signing out', () => {
  it('clears this tab without asking the server again', () => {
    const { manager, deps, state } = setup({ status: 'authenticated', token: 't1' })

    manager.handleRemoteSignOut()

    expect(state().status).toBe('anonymous')
    expect(deps.requestLogout).not.toHaveBeenCalled()
    expect(deps.announceSignedOut).not.toHaveBeenCalled()
  })
})

// ##### Who the tab is #####
// The refresh cookie belongs to the browser and is shared by every tab. When one tab signs in as someone
// else, the cookie becomes theirs, and the next renewal in another tab returns their identity.

describe('a renewal that belongs to someone else', () => {
  it('signs this tab out instead of showing it as that person', async () => {
    const { manager, answer, advance, state, deps } = setup({ status: 'authenticated', token: 't1', userId: 'alice' })
    advance(60 * 60_000)
    answer(renewed('t2', undefined, 'bob'))

    expect(await manager.refresh()).toBe('ended')

    expect(deps.applyAuth).not.toHaveBeenCalled()
    expect(deps.clearAuth).toHaveBeenCalledTimes(1)
    expect(state()).toMatchObject({ status: 'anonymous', token: null })
  })

  it('hands out no token to the request that asked for one', async () => {
    const { manager, answer, advance } = setup({ status: 'authenticated', token: 't1', userId: 'alice' })
    advance(60 * 60_000)
    answer(renewed('t2', undefined, 'bob'))

    expect(await manager.getAccessToken()).toBeNull()
  })

  it('accepts a renewal for the same person', async () => {
    const { manager, answer, advance, state } = setup({ status: 'authenticated', token: 't1', userId: 'alice' })
    advance(60 * 60_000)
    answer(renewed('t2', undefined, 'alice'))

    expect(await manager.refresh()).toBe('renewed')
    expect(state()).toMatchObject({ status: 'authenticated', token: 't2' })
  })

  it('takes whoever the cookie belongs to when nobody is signed in yet, as on a page load', async () => {
    const { manager, answer, state } = setup()
    answer(renewed('t1', undefined, 'bob'))

    await manager.start()

    expect(state()).toMatchObject({ status: 'authenticated', token: 't1' })
  })
})

describe('a start-up that goes wrong in an unexpected way', () => {
  it('shows the sign-in page instead of waiting for ever', async () => {
    const { deps, state } = setup()
    deps.runExclusively = () => Promise.reject(new Error('locks are not available here'))
    const manager = createSessionManager(deps)

    await manager.start()

    expect(state().status).toBe('anonymous')
  })
})
