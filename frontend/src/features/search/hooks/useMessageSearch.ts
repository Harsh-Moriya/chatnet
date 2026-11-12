import { keepPreviousData, useInfiniteQuery } from '@tanstack/react-query'
import { useMemo } from 'react'
import { useAuthStore } from '../../auth/store/authStore'
import { parseQuery } from '../query/parse'
import { searchLocal } from '../search'
import { indexedDbLocalStore } from '../store/indexedDbLocalStore'
import type { ScanPosition } from '../store/types'
import { useSyncStatus } from '../sync/status'

// Searches the messages kept on this device. The query is parsed once per text; a page is read from the store when asked for, and the
// pages are kept while the next ones load so the list does not flash empty on every change of the text. The search is run again when
// the store has more to search, which is when another conversation's older messages have been read in, so what is on screen improves
// while the older history is still being read. A message that arrives after the search shows up when the same search is run again.
export function useMessageSearch(term: string | null) {
  const userId = useAuthStore((state) => state.user?.userId)
  const conversationsStillReading = useSyncStatus((state) => state.historyPending)
  const query = useMemo(() => (term === null ? null : parseQuery(term)), [term])

  const result = useInfiniteQuery({
    queryKey: ['local-search', userId, term, conversationsStillReading],
    queryFn: ({ pageParam }) => searchLocal(indexedDbLocalStore, userId ?? '', query!, pageParam),
    enabled: userId !== undefined && query !== null,
    initialPageParam: null as ScanPosition | null,
    getNextPageParam: (last) => last.next ?? undefined,
    placeholderData: keepPreviousData,
    staleTime: 0,
    gcTime: 30_000,
  })

  return { ...result, hasTerms: query !== null }
}
