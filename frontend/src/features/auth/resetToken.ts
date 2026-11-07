// The reset link in the email is /reset-password#token=<token>. The token is in the fragment, which the browser never sends to
// a server, so it is not in any request line, proxy log or Referer header; the page reads it here and sends it in a request body.

// 32 random bytes in base64url, as the server makes them. Anything else cannot be a token, so the page does not offer a form for it.
const TOKEN_PATTERN = /^[A-Za-z0-9_-]{43}$/

// The token in a location's hash ("#token=..."), or null when there is none or it cannot be one.
export function readResetToken(hash: string): string | null {
  const params = new URLSearchParams(hash.startsWith('#') ? hash.slice(1) : hash)
  const token = params.get('token')

  return token !== null && TOKEN_PATTERN.test(token) ? token : null
}
