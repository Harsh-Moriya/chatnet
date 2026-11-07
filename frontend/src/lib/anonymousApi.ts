import axios from 'axios'

// For the calls a person makes without being signed in (asking for a password reset link, using one). A separate client from
// the one in api.ts, which attaches and renews an access token: these calls carry no token and no cookie, so they can neither
// renew nor end the session of whoever happens to be signed in on this browser.
export const anonymousApi = axios.create({
  baseURL: import.meta.env.VITE_API_URL ?? '/api',
})
