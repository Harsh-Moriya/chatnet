import { api } from '../../../lib/api'
import { getApiError } from '../../../lib/errors'
import { newClientMessageId } from '../../../lib/uuid'
import { useChatStore } from '../store/chatStore'
import { waitForConnection } from '../store/waitForConnection'
import type { Conversation, Message, UserSummary } from '../types'

// Every REST call the chat makes. Plain functions, so they can be tested without React; the hooks
// in this folder only hand them to TanStack Query. REST loads data and confirms changes, SignalR
// only pushes (see the architecture overview), so nothing here touches the hub.
//
// Functions that bring server data into the store do so themselves. That makes the request
// function the one place where REST data enters the store, whether the request was started by a
// component, a refetch or a reconnect.

// A send that gets no answer in this time is shown as failed instead of sending for ever.
const SEND_TIMEOUT_MS = 15_000

const chat = () => useChatStore.getState()

// ##### Loading #####
// These two wait for the live connection first (see waitForConnection).

export async function fetchConversations(signal?: AbortSignal): Promise<Conversation[]> {
  await waitForConnection(signal)

  const { data } = await api.get<Conversation[]>('/conversations', { signal })
  chat().seedConversations(data)
  return data
}

export async function fetchMessages(conversationId: string, signal?: AbortSignal): Promise<Message[]> {
  await waitForConnection(signal)

  const { data } = await api.get<Message[]>(`/conversations/${conversationId}/messages`, { signal })
  chat().seedMessages(conversationId, data)
  return data
}

// Search results are not pushed over the connection, so there is nothing to wait for.
export async function searchUsers(query: string, signal?: AbortSignal): Promise<UserSummary[]> {
  const { data } = await api.get<UserSummary[]>('/users', { params: { query }, signal })
  return data
}

// ##### Starting conversations #####
// The server also announces a new conversation over SignalR, to this tab as well as the others.
// Adding it here as well means the sidebar does not depend on that push arriving first; adding the
// same conversation twice is harmless.

export async function createDirect(otherUserId: string): Promise<Conversation> {
  const { data } = await api.post<Conversation>('/conversations/direct', { otherUserId })
  chat().addConversation(data)
  return data
}

export async function createGroup(name: string, memberIds: string[]): Promise<Conversation> {
  const { data } = await api.post<Conversation>('/conversations/group', { name, memberIds })
  chat().addConversation(data)
  return data
}

// ##### Sending #####
// The message appears at once, marked as sending. The server's copy replaces it when the response
// arrives or when the push does, whichever is first; both carry the id invented here.

export async function sendMessage(conversationId: string, text: string): Promise<void> {
  const content = text.trim()
  if (content === '') return

  const clientMessageId = newClientMessageId()
  chat().addPending({
    clientMessageId,
    conversationId,
    content,
    createdAt: new Date().toISOString(),
    status: 'sending',
    error: null,
  })

  await post(conversationId, clientMessageId, content)
}

// Sends a failed message again with the same id. If the first attempt did reach the server and only
// its answer was lost, the push of that attempt still matches this message and confirms it. Only a
// failed message can be retried, so a double click cannot send it twice.
export async function retryMessage(conversationId: string, clientMessageId: string): Promise<void> {
  const failed = chat().pending[conversationId]?.find((p) => p.clientMessageId === clientMessageId)
  if (failed?.status !== 'failed') return

  chat().retryPending(conversationId, clientMessageId)
  await post(conversationId, clientMessageId, failed.content)
}

export function dismissMessage(conversationId: string, clientMessageId: string): void {
  chat().removePending(conversationId, clientMessageId)
}

async function post(conversationId: string, clientMessageId: string, content: string): Promise<void> {
  try {
    const { data } = await api.post<Message>(
      `/conversations/${conversationId}/messages`,
      { content, clientMessageId },
      { timeout: SEND_TIMEOUT_MS },
    )
    chat().receiveMessage(data)
  } catch (error) {
    // A message the push already confirmed is no longer unconfirmed, and failing an unknown id
    // does nothing, so a lost response cannot turn a delivered message into a failed one.
    chat().failPending(conversationId, clientMessageId, getApiError(error))
  }
}
