import { api } from '../../../lib/api'
import type { SearchHit } from '../types'

// One page of the messages the signed-in user may read that match `query`, newest first. `before` is the id of the last hit
// of the page before, none for the first page. Results are not pushed over the connection, so there is nothing to wait for.
export async function searchMessages(query: string, before?: string, signal?: AbortSignal): Promise<SearchHit[]> {
  const { data } = await api.get<SearchHit[]>('/search/messages', { params: { q: query, before }, signal })
  return data
}
