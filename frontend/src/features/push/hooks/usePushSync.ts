import { useEffect } from 'react'
import { onSignOut } from '../../auth/session'
import { useAuthStore } from '../../auth/store/authStore'
import { releaseBrowser, syncPush } from '../pushController'
import { usePushKey } from './usePushKey'

// Keeps this browser's alert registration right while a person is signed in: registered again every time the page loads (which
// renews it on the server), made again when the server's key changed, and taken away when the browser is subscribed for someone
// who did not ask. And when the person signs out, the browser is taken off the server first, while the session still works.
export function usePushSync() {
  const userId = useAuthStore((state) => state.user?.userId)
  const key = usePushKey(userId !== undefined)

  useEffect(() => onSignOut(releaseBrowser), [])

  useEffect(() => {
    if (userId === undefined || !key.isSuccess) return

    // Offline, or the server is down: the next load does it again.
    void syncPush(userId, key.data).catch(() => undefined)
  }, [userId, key.isSuccess, key.data])
}
