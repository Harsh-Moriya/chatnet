// What the service worker tells an open page. The worker handles the click on an alert; when the application is already open it
// does not reload it but sends this message, and the page moves to the conversation itself.

export interface OpenConversation {
  conversationId: string
  messageId: string | null
}

const ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

const isId = (value: unknown): value is string => typeof value === 'string' && ID.test(value)

// The request in a message from the worker, or null for anything else. The message is whatever was posted to the page, so it is
// checked: the ids become an address.
export function parseOpenConversation(data: unknown): OpenConversation | null {
  if (typeof data !== 'object' || data === null) return null

  const { type, conversationId, messageId } = data as Record<string, unknown>
  if (type !== 'open-conversation' || !isId(conversationId)) return null

  return { conversationId, messageId: isId(messageId) ? messageId : null }
}
