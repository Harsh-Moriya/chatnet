import { keepPreviousData, useInfiniteQuery } from '@tanstack/react-query'
import { nextSearchCursor } from '../searchRules'
import { searchMessages } from './searchApi'

// Runs only for a term that is long enough (see searchTermFrom). Keeping the previous results while the next ones load stops
// the list from flashing empty on every change of the term. The results are not patched by pushes: a message that arrives
// after the search shows up when the same search is run again, which the short staleTime allows soon enough.
export function useMessageSearch(term: string | null) {
  return useInfiniteQuery({
    queryKey: ['search', term],
    queryFn: ({ pageParam, signal }) => searchMessages(term ?? '', pageParam, signal),
    enabled: term !== null,
    initialPageParam: undefined as string | undefined,
    getNextPageParam: nextSearchCursor,
    staleTime: 30_000,
    placeholderData: keepPreviousData,
  })
}
