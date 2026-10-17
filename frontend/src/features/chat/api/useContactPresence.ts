import { useQuery } from '@tanstack/react-query'
import { fetchPresence } from '../../presence/api/presenceApi'
import { waitForConnection } from '../store/waitForConnection'

// Loads who is online into the presence store. It waits for the live connection first, like the other
// loads: a change pushed between the load and the connection being ready would otherwise be missed. The
// key starts with 'presence' so a reconnect can reload it, because the changes pushed while the
// connection was down were lost.
export function useContactPresence() {
  return useQuery({
    queryKey: ['presence'],
    queryFn: async ({ signal }) => {
      await waitForConnection(signal)
      return fetchPresence(signal)
    },
  })
}
