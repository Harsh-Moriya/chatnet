import { ChatConnectionProvider } from './ChatConnectionProvider'
import { ChatLayout } from './ChatLayout'

// The route element for /chat. The connection lives here, above the routes for the individual
// conversations, so opening another conversation does not reconnect.
export function ChatPage() {
  return (
    <ChatConnectionProvider>
      <ChatLayout />
    </ChatConnectionProvider>
  )
}
