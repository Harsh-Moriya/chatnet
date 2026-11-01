import type { ConversationType } from '../chat/types'

// These mirror the JSON of GET /api/search/messages (see section 6.3 of the architecture overview).

// A piece of a snippet: text that matched the search, or the text around it. The pieces are drawn one by one, so nothing in
// a message is ever read as markup.
export interface SnippetPart {
  text: string
  match: boolean
}

// One message that matched. `conversationName` is as the signed-in user sees the conversation (a direct chat is named after
// the other person). `snippet` is empty for a message that matched by the name of a file; `fileName` is that file's name,
// null when the text matched.
export interface SearchHit {
  messageId: string
  conversationId: string
  conversationName: string
  conversationType: ConversationType
  senderId: string
  senderDisplayName: string
  createdAt: string
  snippet: SnippetPart[]
  fileName: string | null
}
