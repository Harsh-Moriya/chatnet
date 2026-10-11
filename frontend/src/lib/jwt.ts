// Reads when a token expires, without checking its signature. The server is the one that decides
// whether a token is good; the client only needs to know when there is no point presenting it any more,
// so that an expired session is ended instead of retried for ever.
//
// A token that cannot be read counts as expired: a broken session should end, not linger.
export function isTokenExpired(token: string | null, nowMs: number = Date.now()): boolean {
  const expiresAt = readExpiry(token)
  return expiresAt === null || expiresAt * 1000 <= nowMs
}

// The "exp" claim, in seconds since 1970, or null.
function readExpiry(token: string | null): number | null {
  const payload = token?.split('.')[1]
  if (!payload) return null

  try {
    // base64url uses - and _ where base64 uses + and /, and leaves out the = padding.
    const claims: unknown = JSON.parse(atob(payload.replaceAll('-', '+').replaceAll('_', '/')))
    const exp = typeof claims === 'object' && claims !== null ? (claims as { exp?: unknown }).exp : undefined
    return typeof exp === 'number' ? exp : null
  } catch {
    return null
  }
}
