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
    // The address of the picture, which a session response does not carry. Null until the profile has been loaded (see
    // useProfileSync), and when there is none.
    avatarUrl: string | null
  } | null
  setAuth: (response: AuthResponse) => void
  // Takes the name and picture the server says the signed-in user has now. Ignored for anyone else.
  updateProfile: (profile: { id: string; displayName: string; avatarUrl: string | null }) => void
  clearAuth: () => void
}

export const useAuthStore = create<AuthState>()((set) => ({
  status: 'unknown',
  token: null,
  tokenExpiresAt: null,
  user: null,
  setAuth: (response: AuthResponse) => {
    set((state) => ({
      status: 'authenticated',
      token: response.token,
      tokenExpiresAt: Date.parse(response.accessTokenExpiresAt),
      user: {
        userId: response.userId,
        userName: response.userName,
        displayName: response.displayName,
        // The token is renewed every few minutes, and a renewal must not take the picture away until the profile is
        // loaded again. A different person signing in has none of the last one's.
        avatarUrl: state.user?.userId === response.userId ? state.user.avatarUrl : null,
      },
    }))
  },
  updateProfile: (profile) =>
    set((state) => {
      const user = state.user
      if (!user || user.userId !== profile.id) return state
      if (user.displayName === profile.displayName && user.avatarUrl === profile.avatarUrl) return state

      return { user: { ...user, displayName: profile.displayName, avatarUrl: profile.avatarUrl } }
    }),
  clearAuth: () => {
    set({ status: 'anonymous', token: null, tokenExpiresAt: null, user: null })
  },
}))
