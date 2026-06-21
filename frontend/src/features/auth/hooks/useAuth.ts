import { useAuthStore } from '../store/authStore'

// Components use this hook rather than calling useAuthStore directly so the
// store implementation can change without touching any component code.
export function useAuth() {
  const { token, user } = useAuthStore()

  return {
    user,
    token,
    isAuthenticated: token !== null
  }
}
