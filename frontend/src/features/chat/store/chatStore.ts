import { create } from 'zustand'
import { compareTimestamps } from '../../../lib/time'
import type {
  ConnectionStatus,
  Conversation,
  Message,
  PendingMessage,
  TypingUser,
} from '../types'

// The live picture of what the signed-in user is looking at. Two sources feed it, and neither
// replaces the other:
//   - REST (through TanStack Query) loads the conversation list and a conversation's history.
//   - SignalR pushes new messages, new conversations and typing events as they happen.
// Both go through the actions below, which merge instead of overwrite, so a push that arrives
// while a request is in flight (or a request that returns something a push already delivered)
// never loses or duplicates anything.
//
// Components read from here only. The store holds no server logic and never calls the network.

interface ChatState {
  connection: ConnectionStatus
  conversations: Record<string, Conversation>
  // Confirmed messages per conversation, oldest first.
  messages: Record<string, Message[]>
  // Messages this tab sent that the server has not confirmed, in the order they were sent.
  pending: Record<string, PendingMessage[]>
  typing: Record<string, TypingUser[]>

  setConnection: (status: ConnectionStatus) => void
  seedConversations: (conversations: Conversation[]) => void
  addConversation: (conversation: Conversation) => void
  seedMessages: (conversationId: string, messages: Message[]) => void
  receiveMessage: (message: Message) => void
  addPending: (message: PendingMessage) => void
  failPending: (conversationId: string, clientMessageId: string, error: string) => void
  retryPending: (conversationId: string, clientMessageId: string) => void
  removePending: (conversationId: string, clientMessageId: string) => void
  setTyping: (conversationId: string, user: TypingUser, isTyping: boolean) => void
  reset: () => void
}

type Data = Pick<ChatState, 'connection' | 'conversations' | 'messages' | 'pending' | 'typing'>

const emptyData = (): Data => ({
  connection: 'connecting',
  conversations: {},
  messages: {},
  pending: {},
  typing: {},
})

// Ties are broken by id so two messages with the same timestamp always sort the same way, whatever
// order they arrived in.
function byTime(a: Message, b: Message): number {
  return compareTimestamps(a.createdAt, b.createdAt) || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0)
}

// Of two copies of the same conversation, the one with the later activity is the fresher.
function fresher(existing: Conversation | undefined, incoming: Conversation): Conversation {
  return existing && compareTimestamps(existing.updatedAt, incoming.updatedAt) > 0 ? existing : incoming
}

export const useChatStore = create<ChatState>()((set) => ({
  ...emptyData(),

  setConnection: (connection) => set({ connection }),

  seedConversations: (conversations) =>
    set((state) => {
      const next = { ...state.conversations }
      for (const incoming of conversations) next[incoming.id] = fresher(next[incoming.id], incoming)
      return { conversations: next }
    }),

  addConversation: (conversation) =>
    set((state) => ({
      conversations: {
        ...state.conversations,
        [conversation.id]: fresher(state.conversations[conversation.id], conversation),
      },
    })),

  // Merges by id, with the loaded copy winning, and re-sorts. A message pushed while the request
  // was in flight is kept; a message in both is kept once. History never carries the id a tab
  // invented for its own message, so the one already known is kept: it is what the list uses to
  // recognise the message as the same element it showed before.
  seedMessages: (conversationId, loaded) =>
    set((state) => {
      const merged = new Map<string, Message>()
      for (const message of state.messages[conversationId] ?? []) merged.set(message.id, message)
      for (const message of loaded) {
        const clientMessageId = message.clientMessageId ?? merged.get(message.id)?.clientMessageId ?? null
        merged.set(message.id, { ...message, clientMessageId })
      }

      return { messages: { ...state.messages, [conversationId]: [...merged.values()].sort(byTime) } }
    }),

  // Called for the push of every message and for the response to this tab's own send, so the same
  // message normally arrives twice. Everything here is idempotent: when nothing would change, the
  // state object is returned as is, which tells Zustand not to notify anyone.
  receiveMessage: (message) =>
    set((state) => {
      const patch: Partial<Data> = {}
      const id = message.conversationId

      const known = state.messages[id] ?? []
      if (!known.some((m) => m.id === message.id)) {
        patch.messages = { ...state.messages, [id]: [...known, message].sort(byTime) }
      }

      // The server's copy replaces the unconfirmed one. Matching is on the id this tab invented,
      // never on the text, so someone else saying the same thing cannot confirm it.
      const waiting = state.pending[id]
      if (message.clientMessageId && waiting?.some((p) => p.clientMessageId === message.clientMessageId)) {
        patch.pending = {
          ...state.pending,
          [id]: waiting.filter((p) => p.clientMessageId !== message.clientMessageId),
        }
      }

      // The sidebar shows the newest message, but a message that arrives late must not replace
      // a newer preview. A conversation the sidebar does not know is not invented from a message.
      const conversation = state.conversations[id]
      if (
        conversation &&
        (!conversation.lastMessage || compareTimestamps(message.createdAt, conversation.lastMessage.createdAt) > 0)
      ) {
        patch.conversations = {
          ...state.conversations,
          [id]: {
            ...conversation,
            updatedAt:
              compareTimestamps(message.createdAt, conversation.updatedAt) > 0
                ? message.createdAt
                : conversation.updatedAt,
            lastMessage: { senderId: message.senderId, content: message.content, createdAt: message.createdAt },
          },
        }
      }

      // Someone who just sent a message is no longer typing.
      const typers = state.typing[id]
      if (typers?.some((u) => u.userId === message.senderId)) {
        patch.typing = { ...state.typing, [id]: typers.filter((u) => u.userId !== message.senderId) }
      }

      return Object.keys(patch).length > 0 ? patch : state
    }),

  addPending: (message) =>
    set((state) => ({
      pending: {
        ...state.pending,
        [message.conversationId]: [...(state.pending[message.conversationId] ?? []), message],
      },
    })),

  failPending: (conversationId, clientMessageId, error) =>
    set((state) =>
      updatePending(state, conversationId, clientMessageId, (p) => ({ ...p, status: 'failed', error })),
    ),

  retryPending: (conversationId, clientMessageId) =>
    set((state) =>
      updatePending(state, conversationId, clientMessageId, (p) => ({ ...p, status: 'sending', error: null })),
    ),

  removePending: (conversationId, clientMessageId) =>
    set((state) => {
      const list = state.pending[conversationId]
      if (!list?.some((p) => p.clientMessageId === clientMessageId)) return state

      return {
        pending: { ...state.pending, [conversationId]: list.filter((p) => p.clientMessageId !== clientMessageId) },
      }
    }),

  setTyping: (conversationId, user, isTyping) =>
    set((state) => {
      const current = state.typing[conversationId] ?? []
      const present = current.some((u) => u.userId === user.userId)

      if (isTyping === present) return state

      return {
        typing: {
          ...state.typing,
          [conversationId]: isTyping ? [...current, user] : current.filter((u) => u.userId !== user.userId),
        },
      }
    }),

  reset: () => set(emptyData()),
}))

function updatePending(
  state: ChatState,
  conversationId: string,
  clientMessageId: string,
  change: (message: PendingMessage) => PendingMessage,
): ChatState | Partial<ChatState> {
  const list = state.pending[conversationId]
  if (!list?.some((p) => p.clientMessageId === clientMessageId)) return state

  return {
    pending: {
      ...state.pending,
      [conversationId]: list.map((p) => (p.clientMessageId === clientMessageId ? change(p) : p)),
    },
  }
}

// Most recent activity first, the order the sidebar uses. Kept out of the store because a selector
// that builds a new array on every call makes React re-render forever; components memoise this on
// the `conversations` record instead.
export function sortedConversations(conversations: Record<string, Conversation>): Conversation[] {
  return Object.values(conversations).sort(
    (a, b) => compareTimestamps(b.updatedAt, a.updatedAt) || (a.id < b.id ? -1 : 1),
  )
}
