import axios from 'axios'

// Single axios instance used by every TanStack Query hook in the app.
// baseURL defaults to /api which Vite proxies to the backend in development.
// In production the reverse proxy handles the same routing.
export const api = axios.create({
  baseURL: import.meta.env.VITE_API_URL ?? '/api',
})

// Attach the stored JWT to every outgoing request automatically.
// This interceptor runs before the request leaves the browser, so every hook
// that calls api.post/get/etc gets auth headers without any per-call setup.
api.interceptors.request.use((config) => {
  const token = localStorage.getItem('token')
  if (token) {
    config.headers.Authorization = `Bearer ${token}`
  }
  return config
})
