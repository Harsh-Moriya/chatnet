import { Routes, Route, Navigate } from 'react-router-dom'
import { AuthPage } from './features/auth/components/AuthPage'
import { ProtectedRoute } from './components/ProtectedRoute'

export default function App() {
  return (
    <Routes>
      <Route path="/auth" element={<AuthPage />} />

      <Route
        path="/"
        element={
          <ProtectedRoute>
            <p className="p-4 text-gray-500">Chat shell — coming soon</p>
          </ProtectedRoute>
        }
      />

      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}
