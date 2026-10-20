// Matches the AuthResponse record returned by /api/auth/register, /login and /refresh. The refresh token is
// not in it: it travels in an httpOnly cookie that scripts cannot see.
export interface AuthResponse {
  token: string
  userId: string
  userName: string
  displayName: string
  // When the access token stops being accepted, as an ISO 8601 UTC time.
  accessTokenExpiresAt: string
}

export interface RegisterRequest {
  userName: string
  displayName: string
  email: string
  password: string
}

export interface LoginRequest {
  email: string
  password: string
}
