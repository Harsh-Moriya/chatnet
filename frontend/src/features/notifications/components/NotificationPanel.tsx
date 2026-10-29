import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { AlertCircle, BellRing, CheckCheck, LoaderCircle, X } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { getApiError } from '../../../lib/errors'
import { useNow } from '../../../lib/useNow'
import { dismissNotification, fetchOlderNotifications, markNotificationsRead } from '../api/notificationsApi'
import { useNotifications } from '../api/useNotifications'
import { conversationPath, focusState } from '../navigation'
import { useNotificationStore } from '../store/notificationStore'
import type { NotificationItem } from '../types'
import { useAlertPermission } from '../useAlertPermission'
import { NotificationRow } from './NotificationRow'

interface NotificationPanelProps {
  id: string
  onClose: () => void
}

// The list behind the bell. It covers the sidebar below the header, so it needs no room of its own, and it is
// only mounted while open: opening it asks the server for the newest page again, so what it shows is current.
export function NotificationPanel({ id, onClose }: NotificationPanelProps) {
  const navigate = useNavigate()
  const now = useNow()
  const query = useNotifications()
  const { permission, request } = useAlertPermission()

  const items = useNotificationStore((state) => state.items)
  const loaded = useNotificationStore((state) => state.loaded)
  const hasMore = useNotificationStore((state) => state.hasMore)
  const unread = useNotificationStore((state) => state.unreadCount)

  // The last thing that went wrong, shown above the list until something works.
  const [problem, setProblem] = useState<string | null>(null)
  const [loadingMore, setLoadingMore] = useState(false)

  async function attempt(action: () => Promise<void>) {
    try {
      await action()
      setProblem(null)
    } catch (error) {
      setProblem(getApiError(error))
    }
  }

  function open(notification: NotificationItem) {
    if (!notification.isRead) void attempt(() => markNotificationsRead([notification.id]))

    if (notification.conversationId) {
      navigate(conversationPath(notification.conversationId), { state: focusState(notification.messageId) })
      onClose()
    }
  }

  async function loadMore() {
    setLoadingMore(true)
    await attempt(fetchOlderNotifications)
    setLoadingMore(false)
  }

  return (
    <section id={id} aria-label="Notifications" className="absolute inset-x-0 bottom-0 top-16 z-20 flex flex-col bg-white">
      <div className="flex shrink-0 items-center justify-between gap-2 border-b border-line px-4 py-2">
        <h2 className="text-sm font-semibold text-slate-900">Notifications</h2>
        <div className="flex items-center gap-1">
          <Button
            variant="ghost"
            size="sm"
            disabled={unread === 0}
            onClick={() => void attempt(() => markNotificationsRead())}
          >
            <CheckCheck className="h-3.5 w-3.5" />
            Mark all read
          </Button>
          <Button variant="ghost" size="icon" aria-label="Close notifications" onClick={onClose}>
            <X className="h-4 w-4" />
          </Button>
        </div>
      </div>

      {permission === 'default' && (
        <div className="flex shrink-0 items-center justify-between gap-3 border-b border-line bg-slate-50 px-4 py-2">
          <p className="text-xs text-slate-500">Get an alert when something arrives while you are away.</p>
          <Button variant="ghost" size="sm" onClick={() => void request()}>
            <BellRing className="h-3.5 w-3.5" />
            Turn on alerts
          </Button>
        </div>
      )}

      {permission === 'denied' && (
        <p className="shrink-0 border-b border-line bg-slate-50 px-4 py-2 text-xs text-slate-500">
          Alerts are blocked for this site. Allow notifications in your browser settings to turn them on.
        </p>
      )}

      {problem && (
        <div
          className="m-3 flex shrink-0 items-start gap-2 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-600"
          role="alert"
        >
          <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
          <span>{problem}</span>
        </div>
      )}

      <div className="flex-1 overflow-y-auto">
        {!loaded && query.isPending && (
          <div className="flex items-center justify-center gap-2 p-8 text-sm text-slate-500">
            <LoaderCircle className="h-4 w-4 animate-spin" />
            Loading…
          </div>
        )}

        {!loaded && query.isError && (
          <div className="flex flex-col items-center gap-3 p-8 text-center">
            <p className="text-sm text-slate-500">{getApiError(query.error)}</p>
            <Button variant="ghost" size="sm" onClick={() => void query.refetch()}>
              Try again
            </Button>
          </div>
        )}

        {loaded && items.length === 0 && (
          <p className="p-8 text-center text-sm text-slate-500">You are all caught up.</p>
        )}

        {items.length > 0 && (
          <ul className="divide-y divide-line-subtle">
            {items.map((notification) => (
              <NotificationRow
                key={notification.id}
                notification={notification}
                now={now}
                onOpen={open}
                onDismiss={(n) => void attempt(() => dismissNotification(n.id))}
              />
            ))}
          </ul>
        )}

        {hasMore && (
          <div className="flex justify-center p-3">
            <Button variant="ghost" size="sm" isLoading={loadingMore} onClick={() => void loadMore()}>
              Load more
            </Button>
          </div>
        )}
      </div>
    </section>
  )
}
