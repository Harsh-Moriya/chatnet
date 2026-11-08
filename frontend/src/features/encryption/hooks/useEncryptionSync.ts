import { useEffect } from 'react'
import { onSignOut } from '../../auth/session'
import { releaseDevice } from '../release'
import { useEncryptionStore } from '../store'

// While a person is signed in: when they sign out, this browser stops being a device and forgets the keys, taken off the server first
// while the session still works. And when the chat goes away for any reason (signing out, a session that ended), what is held in
// memory goes with it; the copy in IndexedDB stays when a session merely expires, so the next sign-in finds the keys.
export function useEncryptionSync() {
  useEffect(() => onSignOut(releaseDevice), [])

  useEffect(
    () => () => {
      useEncryptionStore.getState().reset()
    },
    [],
  )
}
