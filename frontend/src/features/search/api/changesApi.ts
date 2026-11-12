import axios from 'axios'
import { api } from '../../../lib/api'
import type { MessageWire } from '../../chat/types'
import type { ChangesWire } from '../sync/engine'

// The two requests the local store reads the server with.

// What changed in the conversation since the point: see GetMessageChangesHandler on the server.
export async function fetchChanges(conversationId: string, since: number, signal?: AbortSignal): Promise<ChangesWire> {
  const { data } = await api.get<ChangesWire>(`/conversations/${conversationId}/changes`, { params: { since }, signal })
  return data
}

// A page of the history as the server sends it (sealed). The same endpoint the chat reads, without the chat's waiting for its live
// connection and without putting the page in the chat's store: it is read for the local store alone.
export async function fetchHistoryPage(conversationId: string, before: string | null, signal?: AbortSignal): Promise<MessageWire[]> {
  const { data } = await api.get<MessageWire[]>(`/conversations/${conversationId}/messages`, {
    params: before === null ? {} : { before },
    signal,
  })
  return data
}

// The server's answer when the point asked from is past the newest change it has (see MessageCodes on the server).
export const CURSOR_AHEAD = 'changes-cursor-ahead'

export function isCursorAhead(error: unknown): boolean {
  if (!axios.isAxiosError(error) || error.response?.status !== 409) return false

  return (error.response.data as { code?: unknown } | undefined)?.code === CURSOR_AHEAD
}
