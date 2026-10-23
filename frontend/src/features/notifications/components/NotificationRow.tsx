import { AtSign, UserPlus, X } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { formatListTime } from '../../../lib/format'
import { useChatStore } from '../../chat/store/chatStore'
import type { NotificationItem } from '../types'

interface NotificationRowProps {
  notification: NotificationItem
  now: Date
  onOpen: (notification: NotificationItem) => void
  onDismiss: (notification: NotificationItem) => void
}

export function NotificationRow({ notification, now, onOpen, onDismiss }: NotificationRowProps) {
  // The name comes from the sidebar's copy of the conversation; a selector that returns the text only re-renders
  // the row when that name changes.
  const conversationName = useChatStore((state) =>
    notification.conversationId ? state.conversations[notification.conversationId]?.name : undefined,
  )

  const mention = notification.type === 'Mention'
  const Icon = mention ? AtSign : UserPlus
  const where = conversationName ?? (mention ? 'a conversation' : 'a group')

  return (
    <li className="flex items-start gap-1 px-2 py-1">
      <button
        type="button"
        onClick={() => onOpen(notification)}
        className="flex min-w-0 flex-1 items-start gap-3 rounded-lg px-2 py-2 text-left transition-colors hover:bg-slate-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
      >
        <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-blue-50 text-blue-600">
          <Icon className="h-4 w-4" aria-hidden="true" />
        </span>

        <span className="min-w-0 flex-1">
          <span className="block text-sm text-slate-900">
            <span className="font-semibold">{notification.actorDisplayName}</span>
            {mention ? ' mentioned you in ' : ' added you to '}
            <span className="font-semibold">{where}</span>
          </span>
          {notification.messagePreview && (
            <span className="block truncate text-sm text-slate-500">{notification.messagePreview}</span>
          )}
          <time className="block text-xs text-slate-500" dateTime={notification.createdAt}>
            {formatListTime(notification.createdAt, now)}
          </time>
        </span>

        {!notification.isRead && (
          <span className="mt-2 h-2 w-2 shrink-0 rounded-full bg-blue-600" role="img" aria-label="Unread" />
        )}
      </button>

      <Button
        variant="ghost"
        size="icon"
        className="mt-1 h-8 w-8"
        aria-label="Dismiss notification"
        onClick={() => onDismiss(notification)}
      >
        <X className="h-4 w-4" />
      </Button>
    </li>
  )
}
