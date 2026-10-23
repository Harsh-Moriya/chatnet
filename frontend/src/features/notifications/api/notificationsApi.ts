import { api } from '../../../lib/api'
import { waitForConnection } from '../../chat/store/waitForConnection'
import { useNotificationStore } from '../store/notificationStore'
import type { NotificationList } from '../types'

// Every REST call the bell makes. As in the chat API, the functions that bring server data into the store do it
// themselves, so the store is fed from one place whoever asked, and a change the server confirmed is applied at
// once instead of waiting for its push (which reaches this tab too; applying it twice changes nothing).

const store = () => useNotificationStore.getState()

// The newest page. Waits for the live connection first, so a notification pushed while this loads is not lost
// in the gap between the two (see waitForConnection).
export async function fetchNotifications(signal?: AbortSignal): Promise<NotificationList> {
  await waitForConnection(signal)

  const { data } = await api.get<NotificationList>('/notifications', { signal })
  store().seedFirstPage(data)
  return data
}

// The page just older than the oldest one held. Nothing to ask for when none is held.
export async function fetchOlderNotifications(): Promise<void> {
  const oldest = store().items.at(-1)
  if (!oldest) return

  const { data } = await api.get<NotificationList>('/notifications', { params: { before: oldest.id } })
  store().appendOlder(data.items)
}

// The ones named, or all of them when none are named. The server only marks the user's own.
export async function markNotificationsRead(ids?: string[]): Promise<void> {
  await api.post('/notifications/read', { ids })

  if (ids) store().applyRead(ids)
  else store().applyAllRead()
}

export async function dismissNotification(id: string): Promise<void> {
  await api.delete(`/notifications/${id}`)
  store().applyDismissed(id)
}
