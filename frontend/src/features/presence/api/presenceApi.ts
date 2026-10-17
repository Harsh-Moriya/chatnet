import { api } from '../../../lib/api'
import { usePresenceStore } from '../store/presenceStore'
import type { Presence } from '../types'

// The presence of everyone the signed-in user shares an active conversation with. The answer goes into
// the presence store itself, the same way the chat's loads fill the chat store, so whoever asked, a
// component, a refetch or a reconnect, the data enters in one place and by one rule.
export async function fetchPresence(signal?: AbortSignal): Promise<Presence[]> {
  const { data } = await api.get<Presence[]>('/presence', { signal })
  usePresenceStore.getState().seed(data)
  return data
}
