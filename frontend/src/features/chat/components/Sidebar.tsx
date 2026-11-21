import { Suspense, lazy, useEffect, useState } from 'react'
import { LogOut } from 'lucide-react'
import { Avatar } from '../../../components/ui/Avatar'
import { Button } from '../../../components/ui/Button'
import { cn } from '../../../lib/cn'
import { useAuth } from '../../auth/hooks/useAuth'
import { useSignOut } from '../../encryption/hooks/useSignOut'
import { NotificationBell } from '../../notifications/components/NotificationBell'
import { NotificationPanel } from '../../notifications/components/NotificationPanel'
import { totalUnread } from '../../notifications/title'
import { SearchBox } from '../../search/components/SearchBox'
import { SearchResults } from '../../search/components/SearchResults'
import type { SidebarView } from '../sidebarTabs'
import { useChatStore } from '../store/chatStore'
import { ConnectionBanner } from './ConnectionBanner'
import { ConversationList } from './ConversationList'
import { NewConversationPanel } from './NewConversationPanel'
import { SidebarBar } from './SidebarBar'

// Opened by a click and not needed to show the list, so it is fetched when it is first wanted and not with the page.
const ProfileDialog = lazy(() =>
  import('../../profile/components/ProfileDialog').then((module) => ({ default: module.ProfileDialog })),
)

const NOTIFICATION_PANEL_ID = 'notification-panel'

export function Sidebar() {
  const { user } = useAuth()
  const [panelOpen, setPanelOpen] = useState(false)
  const [profileOpen, setProfileOpen] = useState(false)
  const [profileMounted, setProfileMounted] = useState(false)
  const { requestSignOut, signOutDialog } = useSignOut()
  // The panel shown, and what was typed in each, kept here and not in the panels: all three stay mounted, so a panel is as it was left when it
  // comes back (on a phone, when the list returns after a conversation was open).
  const [view, setView] = useState<SidebarView>('chats')
  const [filterInput, setFilterInput] = useState('')
  const [searchInput, setSearchInput] = useState('')
  const [seed, setSeed] = useState<{ term: string; round: number }>()
  const unread = useChatStore((state) => totalUnread(state.conversations))
  const searching = searchInput.trim() !== ''

  function openProfile() {
    setProfileMounted(true)
    setProfileOpen(true)
  }

  // A panel chosen with the mouse or a tap has its box ready to type in; one reached with the arrow keys keeps the focus on the bar.
  function show(next: SidebarView, focusBox: boolean) {
    setView(next)
    if (focusBox) window.setTimeout(() => document.querySelector<HTMLInputElement>(`#sidebar-panel-${next} input`)?.focus(), 0)
  }

  // From a filter that found nothing: the same words, as a search for people.
  // A conversation was made and opened: the list comes back showing everything (a filter left in the box would hide it) and the search
  // carried over from the filter is spent.
  function conversationMade() {
    setFilterInput('')
    setSeed(undefined)
    setView('chats')
  }

  function findSomeone() {
    setSeed((current) => ({ term: filterInput.trim(), round: (current?.round ?? 0) + 1 }))
    show('new', true)
  }

  useEffect(() => {
    if (!panelOpen) return

    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setPanelOpen(false)
    }
    document.addEventListener('keydown', closeOnEscape)
    return () => document.removeEventListener('keydown', closeOnEscape)
  }, [panelOpen])

  const panel = (tab: SidebarView) => cn('min-h-0 flex-1 flex-col', view === tab ? 'flex' : 'hidden')

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
            <p className="truncate text-sm font-semibold text-fg">{user?.displayName}</p>
            <p className="truncate text-xs text-fg-muted">@{user?.userName}</p>
          </div>
        </button>

        <div className="flex shrink-0 items-center gap-1">
          <NotificationBell
            open={panelOpen}
            panelId={NOTIFICATION_PANEL_ID}
            onToggle={() => setPanelOpen((open) => !open)}
          />
          <Button variant="ghost" size="icon" aria-label="Sign out" onClick={() => void requestSignOut()}>
            <LogOut className="h-5 w-5" />
          </Button>
        </div>
      </header>

      {/* Relative, so the notification panel covers the panels and not the bar under them. */}
      <div className="relative flex min-h-0 flex-1 flex-col">
        <ConnectionBanner />

        <div id="sidebar-panel-chats" role="tabpanel" aria-labelledby="sidebar-tab-chats" className={panel('chats')}>
          <SearchBox label="Filter conversations" value={filterInput} onChange={setFilterInput} />
          <ConversationList filter={filterInput} onNewConversation={findSomeone} />
        </div>

        <div id="sidebar-panel-new" role="tabpanel" aria-labelledby="sidebar-tab-new" className={panel('new')}>
          <NewConversationPanel seed={seed} onDone={conversationMade} />
        </div>

        <div id="sidebar-panel-search" role="tabpanel" aria-labelledby="sidebar-tab-search" className={panel('search')}>
          <SearchBox label="Search messages" value={searchInput} onChange={setSearchInput} />
          {searching ? (
            <SearchResults input={searchInput} />
          ) : (
            <p className="p-8 text-center text-sm text-fg-muted">Search the messages kept on this device.</p>
          )}
        </div>

        {panelOpen && <NotificationPanel id={NOTIFICATION_PANEL_ID} onClose={() => setPanelOpen(false)} />}
      </div>

      <SidebarBar view={view} unread={unread} onChange={(next, source) => show(next, source === 'click')} />

      {profileMounted && (
        <Suspense fallback={null}>
          <ProfileDialog open={profileOpen} onClose={() => setProfileOpen(false)} />
        </Suspense>
      )}

      {signOutDialog}
    </>
  )
}
