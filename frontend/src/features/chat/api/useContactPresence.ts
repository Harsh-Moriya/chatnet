import { useQuery } from '@tanstack/react-query'
import { fetchPresence } from '../../presence/api/presenceApi'
import { conversationsKey } from '../../presence/conversationsKey'
import { waitForConnection } from '../store/waitForConnection'
import { useChatStore } from '../store/chatStore'

// Loads who is online into the presence store. It waits for the live connection first, like the other
// loads: a change pushed between the load and the connection being ready would otherwise be missed. The
// first part of the key is 'presence' so a reconnect can reload it, because the changes pushed while the
// connection was down were lost.
//
// The pushes only carry changes, so nothing would tell this browser whether someone it has just started
// a conversation with is online. The key therefore includes which conversations the user is in: one that
// appears later (the user created it, or was added to it; the store gets it either way) gives a new
// key and so a new load. Until the user is in some conversation there is nobody to ask about.
export function useContactPresence() {
  const conversations = useChatStore((state) => conversationsKey(Object.keys(state.conversations)))

  return useQuery({
    queryKey: ['presence', conversations],
    enabled: conversations !== '',
    queryFn: async ({ signal }) => {
      await waitForConnection(signal)
      return fetchPresence(signal)
    },
  })
}
