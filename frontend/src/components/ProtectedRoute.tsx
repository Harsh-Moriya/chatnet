import { Navigate } from 'react-router-dom'
import { useAuth } from '../features/auth/hooks/useAuth'

interface Props {
  children: React.ReactNode
}

// Guards routes that require authentication. Redirects to /auth if no valid
// session exists. Because useAuth reads from Zustand, a logout in any tab
// immediately triggers a re-render and redirect across all protected routes.
export function ProtectedRoute({ children }: Props) {
  const { isAuthenticated } = useAuth()

  if (!isAuthenticated) {
    return <Navigate to="/auth" replace />
  }

  return <>{children}</>
}
