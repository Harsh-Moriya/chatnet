import { Routes, Route, Navigate } from 'react-router-dom'
import { AuthPage } from './features/auth/components/AuthPage'
import { ChatPage } from './features/chat/components/ChatPage'
import { ConversationView } from './features/chat/components/ConversationView'
import { EmptyState } from './features/chat/components/EmptyState'
import { ProtectedRoute } from './components/ProtectedRoute'

export default function App() {
  return (
    <Routes>
      <Route path="/" element={<AuthPage />} />

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
