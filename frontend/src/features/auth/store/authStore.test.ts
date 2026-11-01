import { beforeEach, describe, expect, it } from 'vitest'
import { useAuthStore } from './authStore'
import type { AuthResponse } from '../types'

const response: AuthResponse = {
  token: 'jwt',
  userId: 'u1',
  userName: 'alice',
  displayName: 'Alice',
  accessTokenExpiresAt: '2026-10-20T12:00:00.000Z',
}

const store = () => useAuthStore.getState()

beforeEach(() => {
  useAuthStore.setState({ status: 'unknown', token: null, tokenExpiresAt: null, user: null })
})

describe('the auth store', () => {
  it('has no picture until the profile says so, and a renewed session does not take it away', () => {
    store().setAuth(response)
    expect(store().user?.avatarUrl).toBeNull()

    store().updateProfile({ id: 'u1', displayName: 'Alice', avatarUrl: '/api/avatars/users/u1/f1' })
    store().setAuth({ ...response, token: 'renewed' })

    expect(store().user?.avatarUrl).toBe('/api/avatars/users/u1/f1')
  })

  it('does not hand the picture of one person to the next person who signs in', () => {
    store().setAuth(response)
    store().updateProfile({ id: 'u1', displayName: 'Alice', avatarUrl: '/api/avatars/users/u1/f1' })

    store().setAuth({ ...response, userId: 'u2', userName: 'bob', displayName: 'Bob' })

    expect(store().user).toMatchObject({ userId: 'u2', avatarUrl: null })
  })

  it('takes a new name and picture for the signed-in user and ignores everyone else', () => {
    store().setAuth(response)

    store().updateProfile({ id: 'someone-else', displayName: 'Other', avatarUrl: 'x' })
    expect(store().user).toMatchObject({ displayName: 'Alice', avatarUrl: null })

    store().updateProfile({ id: 'u1', displayName: 'Alice B', avatarUrl: 'y' })
    expect(store().user).toMatchObject({ displayName: 'Alice B', avatarUrl: 'y' })
  })

  it('says nothing is signed in when nobody is, whatever the profile says', () => {
    store().updateProfile({ id: 'u1', displayName: 'Alice', avatarUrl: null })

    expect(store().user).toBeNull()
  })

  it('does not know yet whether anyone is signed in when the page loads', () => {
    expect(store().status).toBe('unknown')
    expect(store().token).toBeNull()
  })

  it('keeps the user and the token, and when the token ends as a number of milliseconds', () => {
    store().setAuth(response)

    expect(store()).toMatchObject({
      status: 'authenticated',
      token: 'jwt',
      tokenExpiresAt: Date.parse('2026-10-20T12:00:00.000Z'),
      user: { userId: 'u1', userName: 'alice', displayName: 'Alice' },
    })
  })

  it('forgets everything on sign-out, and then knows that nobody is signed in', () => {
    store().setAuth(response)

    store().clearAuth()

    expect(store()).toMatchObject({ status: 'anonymous', token: null, tokenExpiresAt: null, user: null })
  })

  // The access token is a short-lived credential, but a stored one is still readable by any script on the
  // page. Nothing about the session is written to browser storage.
  it('writes nothing to browser storage', () => {
    const written: string[] = []
    const original = globalThis.localStorage
    Object.defineProperty(globalThis, 'localStorage', {
      configurable: true,
      value: { setItem: (key: string) => written.push(key), getItem: () => null, removeItem: () => {} },
    })

    try {
      store().setAuth(response)
      store().clearAuth()
    } finally {
      Object.defineProperty(globalThis, 'localStorage', { configurable: true, value: original })
    }

    expect(written).toEqual([])
  })
})
