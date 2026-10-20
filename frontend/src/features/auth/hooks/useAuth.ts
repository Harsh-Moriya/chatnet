import { useAuthStore } from '../store/authStore'

// Components use this hook rather than calling useAuthStore directly so the
// store implementation can change without touching any component code.
export function useAuth() {
  const { token, user, status } = useAuthStore()

  return {
    user,
    token,
    isAuthenticated: status === 'authenticated',
    // True until the server has answered whether the browser still has a working session.
    isResolving: status === 'unknown',
  }
}
