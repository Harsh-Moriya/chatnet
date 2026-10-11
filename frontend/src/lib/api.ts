import axios from 'axios'
import { useAuthStore } from '../features/auth/store/authStore'

// Single axios instance used by every TanStack Query hook in the app.
// baseURL defaults to /api which Vite proxies to the backend in development.
// In production the reverse proxy handles the same routing.
export const api = axios.create({
  baseURL: import.meta.env.VITE_API_URL ?? '/api',
})

// Attach the stored JWT to every outgoing request automatically.
// getState() reads the Zustand store outside of React; safe in module scope
// because Zustand stores are plain objects, not hooks.
api.interceptors.request.use((config) => {
  const token = useAuthStore.getState().token
  if (token) {
    config.headers.Authorization = `Bearer ${token}`
  }
  return config
})

// A 401 on a request that carried a token means the token expired or is no
// longer valid. Clearing the store flips isAuthenticated, and ProtectedRoute
// redirects to the login page. The error is still rethrown so callers can react.
// A 401 from a failed login has no stored token, so it is left alone.
api.interceptors.response.use(
  (response) => response,
  (error) => {
    if (axios.isAxiosError(error) && error.response?.status === 401) {
      const { token, clearAuth } = useAuthStore.getState()
      if (token) clearAuth()
    }
    return Promise.reject(error)
  }
)
