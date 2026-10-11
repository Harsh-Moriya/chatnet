import { Navigate } from 'react-router-dom'
import { useAuth } from '../features/auth/hooks/useAuth'

interface Props {
  children: React.ReactNode
}

// Mirror of ProtectedRoute for pages that only make sense when logged out.
// An authenticated user who lands on the login page is sent straight to the app.
export function GuestRoute({ children }: Props) {
  const { isAuthenticated } = useAuth()

  if (isAuthenticated) {
    return <Navigate to="/chat" replace />
  }

  return <>{children}</>
}
