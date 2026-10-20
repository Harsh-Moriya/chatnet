import { create } from 'zustand'
import type { AuthResponse } from '../types'
import type { SessionStatus } from '../sessionManager'

interface AuthState {
  // 'unknown' from the page load until the server has said whether the browser's refresh cookie still
  // works. Nothing is persisted: the access token lives in memory only, so a script that gets into the
  // page cannot find a long-lived credential in storage.
  status: SessionStatus
  token: string | null
  // When the access token stops being accepted, in milliseconds since 1970.
  tokenExpiresAt: number | null
  user: {
    userId: string
    userName: string
    displayName: string
  } | null
  setAuth: (response: AuthResponse) => void
  clearAuth: () => void
}

export const useAuthStore = create<AuthState>()((set) => ({
  status: 'unknown',
  token: null,
  tokenExpiresAt: null,
  user: null,
  setAuth: (response: AuthResponse) => {
    set({
      status: 'authenticated',
      token: response.token,
      tokenExpiresAt: Date.parse(response.accessTokenExpiresAt),
      user: {
        userId: response.userId,
        userName: response.userName,
        displayName: response.displayName
      }
    })
  },
  clearAuth: () => {
    set({ status: 'anonymous', token: null, tokenExpiresAt: null, user: null })
  },
}))
