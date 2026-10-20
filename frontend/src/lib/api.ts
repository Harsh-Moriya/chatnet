import axios, { type InternalAxiosRequestConfig } from 'axios'
import { session } from '../features/auth/session'
import { canRetryAfterRenewal, type RetryableRequest } from './unauthorizedRetry'

// Single axios instance used by every TanStack Query hook in the app.
// baseURL defaults to /api which Vite proxies to the backend in development.
// In production the reverse proxy handles the same routing.
//
// withCredentials lets the sign-in and registration replies set the refresh cookie when the API is on a
// different origin than the page. The cookie is only ever sent to /api/auth, so no other call carries it.
export const api = axios.create({
  baseURL: import.meta.env.VITE_API_URL ?? '/api',
  withCredentials: true,
})

type Request = InternalAxiosRequestConfig & RetryableRequest

// Attaches the access token to every outgoing request. The token only lives for minutes, so the session
// manager hands over a current one, renewing the session first when the old one is about to end. When
// nobody is signed in (the sign-in request itself) there is no token and nothing is attached.
api.interceptors.request.use(async (config) => {
  const token = await session.getAccessToken()
  if (token) {
    config.headers.Authorization = `Bearer ${token}`
  }
  return config
})

// A 401 on a request that carried a token: renew the session and send the request once more. If the
// server will not renew it, the session manager has signed the user out, which flips the store, and
// ProtectedRoute redirects to the sign-in page. The error is still rethrown so callers can react.
api.interceptors.response.use(
  (response) => response,
  async (error: unknown) => {
    if (axios.isAxiosError(error) && error.config && canRetryAfterRenewal(error.response?.status, error.config as Request)) {
      const request = error.config as Request
      request.retriedAfterRenewal = true

      if ((await session.refresh()) === 'renewed') return api(request)
    }

    return Promise.reject(error)
  },
)
