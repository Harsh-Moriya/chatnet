import { create } from 'zustand'
import { compareTimestamps } from '../../../lib/time'
import { afterDeletion, isNewer, isPreviewOf, latest, withChangedPreview } from '../messageChanges'
import type {
  ConnectionStatus,
  Conversation,
  Member,
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
  // The people in a conversation and how far each has read, for the conversations that were opened.
  members: Record<string, Member[]>

  setConnection: (status: ConnectionStatus) => void
  seedConversations: (conversations: Conversation[]) => void
  addConversation: (conversation: Conversation) => void
  seedMessages: (conversationId: string, messages: Message[]) => void
  clearMessages: (conversationId: string) => void
  seedMembers: (conversationId: string, members: Member[]) => void
  // selfId is the signed-in user. Without it the message is stored but unread counts are left alone.
  receiveMessage: (message: Message, selfId?: string) => void
  // A message already held was edited or deleted: puts the later version in its place, and fixes the sidebar preview and
  // the unread count that depended on it. A message that is not held is ignored (loading its page brings it as it is),
  // and so is a copy that is not newer than the one held. selfId is the signed-in user.
  replaceMessage: (message: Message, selfId?: string) => void
  // A message's attachment links stop working after a while; this puts in the fresh link the server gave.
  setAttachmentUrl: (conversationId: string, messageId: string, attachmentId: string, url: string) => void
  // A member read the conversation up to readAt. selfId is the signed-in user, whose own count it lowers.
  applyRead: (conversationId: string, userId: string, readAt: string, selfId?: string) => void
  // The signed-in user muted or unmuted a conversation, in this tab or another.
  setMuted: (conversationId: string, isMuted: boolean) => void
  addPending: (message: PendingMessage) => void
  failPending: (conversationId: string, clientMessageId: string, error: string) => void
  retryPending: (conversationId: string, clientMessageId: string) => void
  removePending: (conversationId: string, clientMessageId: string) => void
  setTyping: (conversationId: string, user: TypingUser, isTyping: boolean) => void
  reset: () => void
}

type Data = Pick<
  ChatState,
  'connection' | 'conversations' | 'messages' | 'pending' | 'typing' | 'members'
>

const emptyData = (): Data => ({
  connection: 'connecting',
  conversations: {},
  messages: {},
  pending: {},
  typing: {},
  members: {},
})

// Ties are broken by id so two messages with the same timestamp always sort the same way, whatever
// order they arrived in.
function byTime(a: Message, b: Message): number {
  return compareTimestamps(a.createdAt, b.createdAt) || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0)
}

// The patch for a change to a message that is not held (its conversation was never opened, or the message is older
// than the pages loaded). The sidebar shows the newest message of every conversation, so when this is it the preview
// follows, and a deleted message does not stay readable there. A copy that is no change at all (an ordinary push of the
// message) does nothing. The preview carries no version, so an edit can never bring back a preview that already says
// deleted: a deletion is final.
function previewChangeOf(state: Data, message: Message, selfId: string | undefined): Partial<Data> | null {
  if (!message.isDeleted && message.editCount === 0) return null

  const id = message.conversationId
  const conversation = state.conversations[id]
  const shown = conversation?.lastMessage
  if (!conversation || !shown || !isPreviewOf(shown, message)) return null
  if (!message.isDeleted && shown.content === null) return null

  let next = withChangedPreview(conversation, message)
  if (message.isDeleted && shown.content !== null && selfId !== undefined) {
    next = afterDeletion(next, undefined, message, selfId)
  }

  return next === conversation ? null : { conversations: { ...state.conversations, [id]: next } }
}

// The patch that puts a later version of a message that is held in its place: the message itself, the sidebar preview if it
// shows that message, and the unread count if the message was deleted. Null when nothing would change: the copy that
// arrived is not newer than the one held, or the message is not held and the sidebar does not show it either.
function changeOf(state: Data, message: Message, selfId: string | undefined): Partial<Data> | null {
  const id = message.conversationId
  const list = state.messages[id]
  const held = list?.find((m) => m.id === message.id)
  if (!list || !held) return previewChangeOf(state, message, selfId)
  if (!isNewer(message, held)) return null

  // The id this tab invented for its own copy stays, as it does when a page is loaded.
  const kept: Message = { ...message, clientMessageId: message.clientMessageId ?? held.clientMessageId }
  const patch: Partial<Data> = {
    messages: { ...state.messages, [id]: list.map((m) => (m.id === held.id ? kept : m)) },
  }

  const conversation = state.conversations[id]
  if (conversation) {
    let next = withChangedPreview(conversation, kept)
    if (kept.isDeleted && !held.isDeleted && selfId !== undefined) next = afterDeletion(next, list, kept, selfId)
    if (next !== conversation) patch.conversations = { ...state.conversations, [id]: next }
  }

  return patch
}

// Of two copies of the same conversation, the one with the later activity is the fresher.
function fresher(existing: Conversation | undefined, incoming: Conversation): Conversation {
  return existing && compareTimestamps(existing.updatedAt, incoming.updatedAt) > 0 ? existing : incoming
}

// The member list with one member's read position moved forward to readAt. The list itself is
// returned when nothing would change (an unknown member, or a position already at or past readAt),
// so callers can tell that nothing happened.
function withRead(members: Member[] | undefined, userId: string, readAt: string): Member[] | undefined {
  const index = members?.findIndex((m) => m.userId === userId) ?? -1
  if (!members || index < 0) return members

  const current = members[index].lastReadAt
  if (current !== null && compareTimestamps(current, readAt) >= 0) return members

  return members.map((m, i) => (i === index ? { ...m, lastReadAt: readAt } : m))
}

// The conversation after the signed-in user's read position moved to readAt: what is still unread
// is the messages from others after that point, counted from the messages held. Reading up to the
// newest message leaves none. The count never goes up: a read can arrive after a later one (two
// tabs, or pushes out of order), and must not make read messages unread again. The same object is
// returned when the count would not change.
function afterOwnRead(
  conversation: Conversation,
  messages: Message[] | undefined,
  readAt: string,
  selfId: string,
): Conversation {
  const unread = Math.min(
    conversation.unreadCount,
    (messages ?? []).filter((m) => m.senderId !== selfId && compareTimestamps(m.createdAt, readAt) > 0).length,
  )

  return unread === conversation.unreadCount ? conversation : { ...conversation, unreadCount: unread }
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
  // was in flight is kept; a message in both is kept once. The id a tab invented for its own message
  // is kept when the loaded copy lacks it: it is what the list uses to recognise the message as the same
  // element it showed before.
  //
  // A loaded message that carries the id of one of this tab's unconfirmed messages is that message: the
  // server stored it and only the answer was lost (or arrives late). It is no longer unconfirmed, so the
  // pending copy goes, whether the history was loaded by a reload or by catching up after a reconnect.
  seedMessages: (conversationId, loaded) =>
    set((state) => {
      const merged = new Map<string, Message>()
      for (const message of state.messages[conversationId] ?? []) merged.set(message.id, message)
      for (const message of loaded) {
        const held = merged.get(message.id)
        const clientMessageId = message.clientMessageId ?? held?.clientMessageId ?? null

        // A page requested before an edit can arrive after the push of it: the later version is kept.
        merged.set(message.id, { ...latest(held, message), clientMessageId })
      }

      const stored = new Set(loaded.map((m) => m.clientMessageId).filter((id): id is string => id !== null))
      const waiting = state.pending[conversationId]
      const pending = waiting?.some((p) => stored.has(p.clientMessageId))
        ? { ...state.pending, [conversationId]: waiting.filter((p) => !stored.has(p.clientMessageId)) }
        : state.pending

      return { messages: { ...state.messages, [conversationId]: [...merged.values()].sort(byTime) }, pending }
    }),

  // Forgets a conversation's confirmed messages, so it counts as not loaded and its history is
  // fetched afresh. Unconfirmed messages stay: they are this tab's own and not on the server yet.
  clearMessages: (conversationId) =>
    set((state) => {
      if (!(conversationId in state.messages)) return state

      const next = { ...state.messages }
      delete next[conversationId]
      return { messages: next }
    }),

  // The server's list wins for who is in the conversation. A read position that already arrived by
  // push while the list was being loaded is kept when it is later than the one in the list.
  seedMembers: (conversationId, loaded) =>
    set((state) => {
      const held = state.members[conversationId] ?? []

      const merged = loaded.map((incoming) => {
        const known = held.find((m) => m.userId === incoming.userId)?.lastReadAt ?? null
        const keepKnown = known !== null && (incoming.lastReadAt === null || compareTimestamps(known, incoming.lastReadAt) > 0)
        return keepKnown ? { ...incoming, lastReadAt: known } : incoming
      })

      return { members: { ...state.members, [conversationId]: merged } }
    }),

  // Called for the push of every message and for the response to this tab's own send, so the same
  // message normally arrives twice. Everything here is idempotent: when nothing would change, the
  // state object is returned as is, which tells Zustand not to notify anyone.
  //
  // Read state follows from who sent it: a message from someone else is one more unread message the
  // first time it is seen, and a message from the signed-in user (which the server treats as
  // reading) clears what came before it. Either way the sender has read up to their own message.
  receiveMessage: (message, selfId) =>
    set((held) => {
      // A copy of a message that is already here but is a later version (an edit the push of it was missed) replaces it.
      const change = changeOf(held, message, selfId)
      const state = change ? { ...held, ...change } : held

      const patch: Partial<Data> = {}
      const id = message.conversationId

      const known = state.messages[id] ?? []
      const isNew = !known.some((m) => m.id === message.id)
      if (isNew) {
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
      let next = conversation
      if (
        next &&
        (!next.lastMessage || compareTimestamps(message.createdAt, next.lastMessage.createdAt) > 0)
      ) {
        next = {
          ...next,
          updatedAt:
            compareTimestamps(message.createdAt, next.updatedAt) > 0 ? message.createdAt : next.updatedAt,
          lastMessage: {
            senderId: message.senderId,
            content: message.content,
            createdAt: message.createdAt,
            attachmentCount: message.attachments.length,
          },
        }
      }

      if (next && selfId !== undefined) {
        if (message.senderId === selfId) {
          next = afterOwnRead(next, patch.messages?.[id] ?? known, message.createdAt, selfId)
        } else if (isNew) {
          next = { ...next, unreadCount: next.unreadCount + 1 }
        }
      }

      if (next !== conversation) patch.conversations = { ...state.conversations, [id]: next! }

      const members = withRead(state.members[id], message.senderId, message.createdAt)
      if (members !== state.members[id]) patch.members = { ...state.members, [id]: members! }

      // Someone who just sent a message is no longer typing.
      const typers = state.typing[id]
      if (typers?.some((u) => u.userId === message.senderId)) {
        patch.typing = { ...state.typing, [id]: typers.filter((u) => u.userId !== message.senderId) }
      }

      if (change) return { ...change, ...patch }

      return Object.keys(patch).length > 0 ? patch : state
    }),

  replaceMessage: (message, selfId) =>
    set((state) => changeOf(state, message, selfId) ?? state),

  setAttachmentUrl: (conversationId, messageId, attachmentId, url) =>
    set((state) => {
      const list = state.messages[conversationId]
      const message = list?.find((m) => m.id === messageId)
      const attachment = message?.attachments.find((a) => a.id === attachmentId)
      if (!list || !message || !attachment || attachment.url === url) return state

      const changed: Message = {
        ...message,
        attachments: message.attachments.map((a) => (a.id === attachmentId ? { ...a, url } : a)),
      }
      return {
        messages: { ...state.messages, [conversationId]: list.map((m) => (m.id === messageId ? changed : m)) },
      }
    }),

  // The same rules apply whoever the reader is: their position moves forward only. For the
  // signed-in user it also lowers their own unread count, which is how a read in another tab
  // reaches this one.
  applyRead: (conversationId, userId, readAt, selfId) =>
    set((state) => {
      const patch: Partial<Data> = {}

      const members = withRead(state.members[conversationId], userId, readAt)
      if (members !== state.members[conversationId]) patch.members = { ...state.members, [conversationId]: members! }

      const conversation = state.conversations[conversationId]
      if (conversation && selfId !== undefined && userId === selfId) {
        const next = afterOwnRead(conversation, state.messages[conversationId], readAt, selfId)
        if (next !== conversation) patch.conversations = { ...state.conversations, [conversationId]: next }
      }

      return Object.keys(patch).length > 0 ? patch : state
    }),

  // A conversation the sidebar does not know is left alone: its copy will carry the choice when it is loaded.
  setMuted: (conversationId, isMuted) =>
    set((state) => {
      const conversation = state.conversations[conversationId]
      if (!conversation || conversation.isMuted === isMuted) return state

      return { conversations: { ...state.conversations, [conversationId]: { ...conversation, isMuted } } }
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
