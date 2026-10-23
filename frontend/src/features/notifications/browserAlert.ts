import type { AlertText, Viewing } from './alerts'

// The browser side of alerts: permission, and showing one. Kept thin and free of rules; what deserves an alert is
// decided in alerts.ts.

export type AlertPermission = NotificationPermission | 'unsupported'

export function alertPermission(): AlertPermission {
  return typeof Notification === 'undefined' ? 'unsupported' : Notification.permission
}

// Must be called from a click or a key press: a browser ignores a request that does not come from the user.
export async function requestAlertPermission(): Promise<AlertPermission> {
  if (typeof Notification === 'undefined') return 'unsupported'

  return Notification.requestPermission()
}

// Whether the user is looking at the page right now: in front of them, in a window that has the focus.
export function isLooking(): boolean {
  return document.visibilityState === 'visible' && document.hasFocus()
}

export function currentView(openConversationId: string | null): Viewing {
  return { looking: isLooking(), openConversationId }
}

// Shows the alert if the user allowed alerts. Clicking it brings the window forward and calls onOpen.
export function showAlert(text: AlertText, onOpen: () => void): void {
  if (alertPermission() !== 'granted') return

  try {
    const alert = new Notification(text.title, { body: text.body, tag: text.tag })
    alert.onclick = () => {
      alert.close()
      window.focus()
      onOpen()
    }
  } catch {
    // Some browsers (Chrome on Android) only show a notification through a service worker and throw here.
    // The bell and the unread counts still show everything.
  }
}
