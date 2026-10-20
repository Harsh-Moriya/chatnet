import { Navigate } from 'react-router-dom'
import { useAuth } from '../features/auth/hooks/useAuth'
import { SessionLoading } from './SessionLoading'

interface Props {
  children: React.ReactNode
}

// Guards routes that require authentication. Redirects to the login page (/)
// if no valid session exists. Because useAuth reads from Zustand, a logout
// immediately triggers a re-render and redirect across all protected routes.
//
// While the server is still being asked whether the browser has a working session (right after the
// page loads) the answer is not known, so neither the page nor a redirect is shown yet.
export function ProtectedRoute({ children }: Props) {
  const { isAuthenticated, isResolving } = useAuth()

  if (isResolving) {
    return <SessionLoading />
  }

  if (!isAuthenticated) {
    return <Navigate to="/" replace />
  }

  return <>{children}</>
}
