import { Suspense, lazy } from 'react'
import { Routes, Route, Navigate } from 'react-router-dom'
import { AuthPage } from './features/auth/components/AuthPage'
import { ChatPage } from './features/chat/components/ChatPage'
import { ConversationView } from './features/chat/components/ConversationView'
import { EmptyState } from './features/chat/components/EmptyState'
import { ProtectedRoute } from './components/ProtectedRoute'
import { SessionLoading } from './components/SessionLoading'

// Opened rarely, from a link, so they load when first visited and the main bundle stays under Vite's size warning.
const ForgotPasswordPage = lazy(() =>
  import('./features/auth/components/ForgotPasswordPage').then((module) => ({ default: module.ForgotPasswordPage })),
)
const ResetPasswordPage = lazy(() =>
  import('./features/auth/components/ResetPasswordPage').then((module) => ({ default: module.ResetPasswordPage })),
)

export default function App() {
  return (
    <Routes>
      <Route path="/" element={<AuthPage />} />

      {/* Public: a person who forgot their password is by definition not signed in, and the reset page has to open for one who is. */}
      <Route
        path="/forgot-password"
        element={
          <Suspense fallback={<SessionLoading />}>
            <ForgotPasswordPage />
          </Suspense>
        }
      />
      <Route
        path="/reset-password"
        element={
          <Suspense fallback={<SessionLoading />}>
            <ResetPasswordPage />
          </Suspense>
        }
      />

      {/* ChatPage holds the sidebar and the connection; the child route fills the right-hand pane. */}
      <Route
        path="/chat"
        element={
          <ProtectedRoute>
            <ChatPage />
          </ProtectedRoute>
        }
      >
        <Route index element={<EmptyState />} />
        <Route path=":conversationId" element={<ConversationView />} />
      </Route>

      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}
