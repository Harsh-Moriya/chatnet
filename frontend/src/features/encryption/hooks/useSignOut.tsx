import { useState, type ReactNode } from 'react'
import { useAuth } from '../../auth/hooks/useAuth'
import { session } from '../../auth/session'
import { LazySignOutDialog } from '../components/LazySignOutDialog'
import { signOutDeletesKeys } from '../signOutCheck'

// One way to sign out for every screen that offers it. A browser that holds this person's keys is asked first, because signing out
// deletes them here; any other signs out at once. `signOutDialog` is rendered wherever the button is.
export function useSignOut(): { requestSignOut: () => Promise<void>; signOutDialog: ReactNode } {
  const { user } = useAuth()
  const [open, setOpen] = useState(false)
  // Mounted from the first request on, so closing it returns focus to the button.
  const [mounted, setMounted] = useState(false)

  async function requestSignOut() {
    if (user && (await signOutDeletesKeys(user.userId))) {
      setMounted(true)
      setOpen(true)
      return
    }

    await session.signOut()
  }

  const signOutDialog = mounted ? <LazySignOutDialog open={open} onClose={() => setOpen(false)} /> : null

  return { requestSignOut, signOutDialog }
}
