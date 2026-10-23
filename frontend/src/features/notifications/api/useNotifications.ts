import { useQuery } from '@tanstack/react-query'
import { fetchNotifications } from './notificationsApi'

// Loads the newest page of the bell's list. The fetch puts the result in the notification store, which is what
// components read; the query is here for its loading and error state and for refetching. The key is known to
// the connection code, which refetches after a reconnect.
//
// The list is never treated as fresh, so opening the panel and coming back to the window both ask again: the
// pushes keep the list current, and this is what corrects the count they can only adjust.
export function useNotifications() {
  return useQuery({
    queryKey: ['notifications'],
    queryFn: ({ signal }) => fetchNotifications(signal),
    staleTime: 0,
  })
}
