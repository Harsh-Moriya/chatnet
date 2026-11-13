import { snippetOf, type SnippetPart } from './query/snippet'
import { queryHolds } from './query/match'
import type { Query } from './query/parse'
import { SEARCH_PAGE_SIZE } from './searchRules'
import type { LocalStore, ScanPosition, StoredText } from './store/types'

// A message that matched. The conversation's name is not part of a hit: the list of conversations already holds it. `snippet` is empty for
// a message that matched by the name of a file; `fileName` is that file's name, null when the text matched.
export interface LocalHit {
  messageId: string
  conversationId: string
  senderId: string
  senderDisplayName: string
  createdAt: string
  snippet: SnippetPart[]
  fileName: string | null
}

export interface SearchPage {
  hits: LocalHit[]
  // Where to continue to read more hits, or null when every message has been tested.
  next: ScanPosition | null
}

// How a message matched: by its text, or by the name of one of its files.
function hitOf(message: StoredText, query: Query): LocalHit | null {
  const base = {
    messageId: message.id,
    conversationId: message.conversationId,
    senderId: message.senderId,
    senderDisplayName: message.senderName,
    createdAt: message.createdAt,
  }

  if (queryHolds(message.words, query)) return { ...base, snippet: snippetOf(message.text, query), fileName: null }

  const file = message.files.find((candidate) => queryHolds(candidate.words, query))
  return file ? { ...base, snippet: [], fileName: file.name } : null
}

// One page of hits: the messages are tested newest first, and the scan stops as soon as there is a page, so a word that is common costs
// about a page of messages and one that is rare costs the whole store. Deleted messages and messages not opened yet are never hits.
export async function searchLocal(store: LocalStore, userId: string, query: Query, after: ScanPosition | null): Promise<SearchPage> {
  const hits: LocalHit[] = []

  const { last, exhausted } = await store.scan(userId, after, (message) => {
    if (message.state === 'text') {
      const hit = hitOf(message, query)
      if (hit) hits.push(hit)
    }

    return hits.length < SEARCH_PAGE_SIZE
  })

  return { hits, next: exhausted ? null : last }
}
