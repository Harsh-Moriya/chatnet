import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { searchUsers } from './chatApi'

// The server needs at least two characters. Keeping the previous results while the next ones load
// stops the list from flashing empty on every keystroke.
export function useUserSearch(term: string) {
  const query = term.trim()

  return useQuery({
    queryKey: ['users', query],
    queryFn: ({ signal }) => searchUsers(query, signal),
    enabled: query.length >= 2,
    staleTime: 30_000,
    placeholderData: keepPreviousData,
  })
}
