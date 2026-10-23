import { Bell } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { unreadLabel } from '../../chat/readState'
import { useNotifications } from '../api/useNotifications'
import { useNotificationStore } from '../store/notificationStore'

interface NotificationBellProps {
  open: boolean
  panelId: string
  onToggle: () => void
}

// The button in the sidebar's header, with the number of notifications that are unread. It also keeps the first
// page loaded, so the number is right before anyone opens the panel.
export function NotificationBell({ open, panelId, onToggle }: NotificationBellProps) {
  useNotifications()
  const unread = useNotificationStore((state) => state.unreadCount)
  const label = unreadLabel(unread)

  return (
    <div className="relative">
      <Button
        variant="ghost"
        size="icon"
        aria-label={unread > 0 ? `Notifications, ${label} unread` : 'Notifications'}
        aria-expanded={open}
        aria-controls={panelId}
        onClick={onToggle}
      >
        <Bell className="h-5 w-5" />
      </Button>

      {unread > 0 && (
        <span
          className="pointer-events-none absolute -right-0.5 -top-0.5 inline-flex h-4 min-w-4 items-center justify-center rounded-full bg-blue-600 px-1 text-[10px] font-semibold leading-none text-white"
          aria-hidden="true"
        >
          {label}
        </span>
      )}
    </div>
  )
}
