import { type AlertOptions, alertOptions } from '../../lib/alertLook'
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

// What an alert needs of the browser, so the choice between the ways of showing one can be tested without a browser.
interface WorkerRegistration {
  showNotification: (title: string, options: AlertOptions) => Promise<void>
  getNotifications: (filter: { tag: string }) => Promise<readonly { close: () => void }[]>
}

interface ShownAlert {
  close: () => void
  onclick: ((event: Event) => unknown) | null
}

export interface AlertHost {
  registration: () => Promise<WorkerRegistration | undefined>
  create: (title: string, options: AlertOptions) => ShownAlert
  focus: () => void
  now: () => number
}

// Shows an alert, and returns what takes it down again, for one that should not outlive what it announces (a ringing call).
//
// Through the service worker when there is one: it is the only way on Chrome for Android, which refuses `new Notification` and throws, and
// it works everywhere else too. A click then goes to the worker, which opens the conversation (sw/push.ts). Without a worker the page
// shows it itself, and its click comes back here. When neither can, nothing is shown: the bell and the unread counts still say everything.
export function raiseAlert(host: AlertHost, text: AlertText, onOpen: () => void): () => void {
  let withdrawn = false
  let close = () => {}

  const options = alertOptions(
    { body: text.body, tag: text.tag, ringing: text.persistent === true, conversationId: text.conversationId, messageId: text.messageId },
    host.now(),
  )

  void (async () => {
    try {
      const registration = await host.registration()
      if (registration) {
        await registration.showNotification(text.title, options)
        close = () => void registration.getNotifications({ tag: text.tag }).then((shown) => shown.forEach((one) => one.close()))
        if (withdrawn) close()
        return
      }
    } catch {
      // The worker could not show it: the page tries itself.
    }

    try {
      const alert = host.create(text.title, options)
      alert.onclick = () => {
        alert.close()
        host.focus()
        onOpen()
      }
      close = () => alert.close()
      if (withdrawn) close()
    } catch {
      // Nothing here can show an alert.
    }
  })()

  return () => {
    withdrawn = true
    close()
  }
}

const browserHost: AlertHost = {
  registration: async () => (typeof navigator !== 'undefined' && 'serviceWorker' in navigator ? navigator.serviceWorker.getRegistration() : undefined),
  create: (title, options) => new Notification(title, options as NotificationOptions),
  focus: () => window.focus(),
  now: Date.now,
}

// Shows the alert if the user allowed alerts, or returns null when they did not.
export function showAlert(text: AlertText, onOpen: () => void): (() => void) | null {
  if (alertPermission() !== 'granted') return null

  return raiseAlert(browserHost, text, onOpen)
}
