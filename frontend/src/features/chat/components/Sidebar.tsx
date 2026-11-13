import { Suspense, lazy, useEffect, useState } from 'react'
import { LogOut, SquarePen } from 'lucide-react'
import { Avatar } from '../../../components/ui/Avatar'
import { Button } from '../../../components/ui/Button'
import { useAuth } from '../../auth/hooks/useAuth'
import { session } from '../../auth/session'
import { signOutDeletesKeys } from '../../encryption/signOutCheck'
import { NotificationBell } from '../../notifications/components/NotificationBell'
import { NotificationPanel } from '../../notifications/components/NotificationPanel'
import { SearchBox } from '../../search/components/SearchBox'
import { SearchResults } from '../../search/components/SearchResults'
import { ConnectionBanner } from './ConnectionBanner'
import { ConversationList } from './ConversationList'

// Opened by a click and not needed to show the list, so it is fetched when it is first wanted and not with the page.
const NewConversationDialog = lazy(() =>
  import('./NewConversationDialog').then((module) => ({ default: module.NewConversationDialog })),
)

// Likewise: opened by a click.
const ProfileDialog = lazy(() =>
  import('../../profile/components/ProfileDialog').then((module) => ({ default: module.ProfileDialog })),
)

// Only asked for when signing out would delete encryption keys.
const SignOutDialog = lazy(() =>
  import('../../encryption/components/SignOutDialog').then((module) => ({ default: module.SignOutDialog })),
)

const NOTIFICATION_PANEL_ID = 'notification-panel'

export function Sidebar() {
  const { user } = useAuth()
  const [dialogOpen, setDialogOpen] = useState(false)
  // Mounted from the first click on, so closing it returns focus to the button as the browser's dialog does.
  const [dialogMounted, setDialogMounted] = useState(false)
  const [panelOpen, setPanelOpen] = useState(false)
  const [profileOpen, setProfileOpen] = useState(false)
  const [profileMounted, setProfileMounted] = useState(false)
  const [signOutOpen, setSignOutOpen] = useState(false)
  const [signOutMounted, setSignOutMounted] = useState(false)
  // Kept here, not in the results, so the search is still there after a result is opened (on a phone, when the list comes back).
  const [searchInput, setSearchInput] = useState('')
  const searching = searchInput.trim() !== ''

  function openProfile() {
    setProfileMounted(true)
    setProfileOpen(true)
  }

  // Signs out at once, unless this browser holds encryption keys, which signing out deletes: then the person is asked first.
  async function requestSignOut() {
    if (user && (await signOutDeletesKeys(user.userId))) {
      setSignOutMounted(true)
      setSignOutOpen(true)
      return
    }

    await session.signOut()
  }

  function openDialog() {
    setDialogMounted(true)
    setDialogOpen(true)
  }

  useEffect(() => {
    if (!panelOpen) return

    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setPanelOpen(false)
    }
    document.addEventListener('keydown', closeOnEscape)
    return () => document.removeEventListener('keydown', closeOnEscape)
  }, [panelOpen])

  return (
    <>
      <header className="flex h-16 shrink-0 items-center justify-between gap-2 border-b border-line px-4">
        <button
          type="button"
          aria-label="Your profile"
          onClick={openProfile}
          className="flex min-w-0 items-center gap-3 rounded-lg text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
        >
          <Avatar name={user?.displayName ?? ''} src={user?.avatarUrl} />
          <div className="min-w-0">
            <p className="truncate text-sm font-semibold text-slate-900">{user?.displayName}</p>
            <p className="truncate text-xs text-slate-500">@{user?.userName}</p>
          </div>
        </button>

        <div className="flex shrink-0 items-center gap-1">
          <NotificationBell
            open={panelOpen}
            panelId={NOTIFICATION_PANEL_ID}
            onToggle={() => setPanelOpen((open) => !open)}
          />
          <Button variant="ghost" size="icon" aria-label="New conversation" onClick={openDialog}>
            <SquarePen className="h-5 w-5" />
          </Button>
          <Button variant="ghost" size="icon" aria-label="Sign out" onClick={() => void requestSignOut()}>
            <LogOut className="h-5 w-5" />
          </Button>
        </div>
      </header>

      <ConnectionBanner />
      <SearchBox value={searchInput} onChange={setSearchInput} />
      {searching ? <SearchResults input={searchInput} /> : <ConversationList onNewConversation={openDialog} />}

      {panelOpen && <NotificationPanel id={NOTIFICATION_PANEL_ID} onClose={() => setPanelOpen(false)} />}

      {dialogMounted && (
        <Suspense fallback={null}>
          <NewConversationDialog open={dialogOpen} onClose={() => setDialogOpen(false)} />
        </Suspense>
      )}

      {profileMounted && (
        <Suspense fallback={null}>
          <ProfileDialog open={profileOpen} onClose={() => setProfileOpen(false)} />
        </Suspense>
      )}

      {signOutMounted && (
        <Suspense fallback={null}>
          <SignOutDialog open={signOutOpen} onClose={() => setSignOutOpen(false)} />
        </Suspense>
      )}
    </>
  )
}
