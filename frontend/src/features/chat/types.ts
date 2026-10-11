// These mirror the JSON the API sends (see section 6.3 of the architecture overview). Enums are
// strings, timestamps are ISO 8601 UTC strings, and IDs are opaque UUID strings.

export type ConversationType = 'Direct' | 'Group'

// A conversation as the signed-in user sees it: for a direct chat, `name` is the other person.
export interface Conversation {
  id: string
  type: ConversationType
  name: string
  avatarUrl: string | null
  updatedAt: string
  lastMessage: LastMessage | null
}

export interface LastMessage {
  senderId: string
  // Null when the last message was deleted.
  content: string | null
  createdAt: string
}

export interface Message {
  id: string
  conversationId: string
  senderId: string
  senderDisplayName: string
  // Null for a deleted message: it keeps its place in the timeline but has no text.
  content: string | null
  createdAt: string
  // Set on the response to your own send and on the push of it; null in history loads.
  clientMessageId: string | null
}

// A message this tab has sent that the server has not confirmed yet. It lives only in the
// browser; once the server's copy arrives it is replaced by it.
export interface PendingMessage {
  clientMessageId: string
  conversationId: string
  content: string
  // The browser's clock at the moment of sending. Used for display only.
  createdAt: string
  status: 'sending' | 'failed'
  error: string | null
}

export interface UserSummary {
  id: string
  userName: string
  displayName: string
  avatarUrl: string | null
}

export interface TypingUser {
  userId: string
  displayName: string
}

export type ConnectionStatus = 'connecting' | 'connected' | 'reconnecting' | 'disconnected'
