import { create } from 'zustand'
import { persist } from 'zustand/middleware'
import type { AuthResponse } from '../types'

interface AuthState {
  token: string | null
  user: {
    userId: string
    userName: string
    displayName: string
  } | null
  setAuth: (response: AuthResponse) => void
  clearAuth: () => void
}

export const useAuthStore = create<AuthState>()(
  persist(
    (set) => ({
      token: null,
      user: null,
      setAuth: (response: AuthResponse) => {
        set({
          token: response.token,
          user: {
            userId: response.userId,
            userName: response.userName,
            displayName: response.displayName
          }
        })
      },
      clearAuth: () => {
        set({ token: null, user: null })
      },
    }),
    { name: 'chatnet-auth' }
  )
)
