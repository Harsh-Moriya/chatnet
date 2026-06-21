// Matches the AuthResponse record returned by both /api/auth/register and /api/auth/login.
export interface AuthResponse {
  token: string
  userId: string
  userName: string
  displayName: string
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
