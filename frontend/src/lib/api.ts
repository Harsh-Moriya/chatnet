import axios from 'axios'
import { useAuthStore } from '../features/auth/store/authStore'

// Single axios instance used by every TanStack Query hook in the app.
// baseURL defaults to /api which Vite proxies to the backend in development.
// In production the reverse proxy handles the same routing.
export const api = axios.create({
  baseURL: import.meta.env.VITE_API_URL ?? '/api',
})

// Attach the stored JWT to every outgoing request automatically.
// getState() reads the Zustand store outside of React — safe in module scope
// because Zustand stores are plain objects, not hooks.
api.interceptors.request.use((config) => {
  const token = useAuthStore.getState().token
  if (token) {
    config.headers.Authorization = `Bearer ${token}`
  }
  return config
})
