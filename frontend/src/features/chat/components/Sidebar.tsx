import { useEffect, useState } from 'react'
import { LogOut, SquarePen } from 'lucide-react'
import { Avatar } from '../../../components/ui/Avatar'
import { Button } from '../../../components/ui/Button'
import { useAuth } from '../../auth/hooks/useAuth'
import { session } from '../../auth/session'
import { NotificationBell } from '../../notifications/components/NotificationBell'
import { NotificationPanel } from '../../notifications/components/NotificationPanel'
import { ConnectionBanner } from './ConnectionBanner'
import { ConversationList } from './ConversationList'
import { NewConversationDialog } from './NewConversationDialog'

const NOTIFICATION_PANEL_ID = 'notification-panel'

export function Sidebar() {
  const { user } = useAuth()
  const [dialogOpen, setDialogOpen] = useState(false)
  const [panelOpen, setPanelOpen] = useState(false)

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
        <div className="flex min-w-0 items-center gap-3">
          <Avatar name={user?.displayName ?? ''} />
          <div className="min-w-0">
            <p className="truncate text-sm font-semibold text-slate-900">{user?.displayName}</p>
            <p className="truncate text-xs text-slate-500">@{user?.userName}</p>
          </div>
        </div>

        <div className="flex shrink-0 items-center gap-1">
          <NotificationBell
            open={panelOpen}
            panelId={NOTIFICATION_PANEL_ID}
            onToggle={() => setPanelOpen((open) => !open)}
          />
          <Button variant="ghost" size="icon" aria-label="New conversation" onClick={() => setDialogOpen(true)}>
            <SquarePen className="h-5 w-5" />
          </Button>
          <Button variant="ghost" size="icon" aria-label="Sign out" onClick={() => void session.signOut()}>
            <LogOut className="h-5 w-5" />
          </Button>
        </div>
      </header>

      <ConnectionBanner />
      <ConversationList onNewConversation={() => setDialogOpen(true)} />

      {panelOpen && <NotificationPanel id={NOTIFICATION_PANEL_ID} onClose={() => setPanelOpen(false)} />}

      <NewConversationDialog open={dialogOpen} onClose={() => setDialogOpen(false)} />
    </>
  )
}
