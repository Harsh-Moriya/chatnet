// What the service worker does when the server pushes an alert and when the person clicks it. The browser starts the worker for a push when
// the page is closed, so this is the only part of the application that runs then. It keeps nothing and calls no server; everything it needs
// is in the push.

const OPEN_CONVERSATION = 'open-conversation'
const ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

// The parts of a window the worker uses, and of the worker's scope: what the browser gives it, stated here so that this file is the same
// under the types of a page and of a worker, and so that a test can stand in for them.
export interface WindowLike {
  url: string
  visibilityState: string
  focused: boolean
  focus: () => Promise<unknown>
  postMessage: (message: unknown, transfer?: Transferable[]) => void
}

export interface PushScope {
  location: { origin: string }
  clients: {
    matchAll: (options: { type: 'window'; includeUncontrolled: boolean }) => Promise<readonly WindowLike[]>
    openWindow: (url: string) => Promise<unknown>
  }
  registration: {
    showNotification: (
      title: string,
      options?: { body?: string; tag?: string; data?: unknown; requireInteraction?: boolean; icon?: string; badge?: string },
    ) => Promise<void>
  }
}

export interface PushEventLike {
  data: { json: () => unknown } | null
}

export interface ClickEventLike {
  notification: { data: unknown; close: () => void }
}

interface Alert {
  title: string
  body: string
  tag: string
  // What kind of alert it is ("message", "mention", "invitation", "call", "missed-call"); empty when the push does not say.
  type: string
  conversationId: string | null
  messageId: string | null
}

function isId(value: unknown): value is string {
  return typeof value === 'string' && ID.test(value)
}

function text(value: unknown): string {
  return typeof value === 'string' ? value : ''
}

// What the server pushed, or null when it cannot be read. The ids are checked because they become an address.
export function readAlert(event: PushEventLike): Alert | null {
  let data: unknown
  try {
    data = event.data ? event.data.json() : null
  } catch {
    return null
  }

  if (typeof data !== 'object' || data === null) return null
  const fields = data as Record<string, unknown>
  if (text(fields.title) === '') return null

  return {
    title: text(fields.title),
    body: text(fields.body),
    tag: text(fields.tag),
    type: text(fields.type),
    conversationId: isId(fields.conversationId) ? fields.conversationId : null,
    messageId: isId(fields.messageId) ? fields.messageId : null,
  }
}

// The picture beside an alert, and the small single-colour one an Android phone shows in its status bar (white on transparent).
const LOOK = { icon: '/icons/icon-192.png', badge: '/icons/badge-96.png' }

export async function showAlert(scope: PushScope, alert: Alert | null): Promise<void> {
  // The person is looking at the application right now: it shows what happened itself, and an alert on top of it would only be
  // in the way. A browser lets a worker show nothing for a push only in this case.
  const windows = await scope.clients.matchAll({ type: 'window', includeUncontrolled: true })
  if (windows.some((window) => window.visibilityState === 'visible' && window.focused)) return

  // A push that cannot be read still gets an alert: browsers withdraw the permission of a site that is pushed to and shows nothing.
  if (alert === null) {
    await scope.registration.showNotification('ChatNET', { body: 'Something new is waiting for you.', tag: 'chatnet', ...LOOK })
    return
  }

  await scope.registration.showNotification(alert.title, {
    body: alert.body,
    // Alerts with the same tag replace each other, so a conversation shows one at a time.
    tag: alert.tag === '' ? undefined : alert.tag,
    data: { conversationId: alert.conversationId, messageId: alert.messageId },
    ...LOOK,
    // A call that is ringing stays on the screen until it is dealt with, as an incoming call does on a phone.
    ...(alert.type === 'call' ? { requireInteraction: true } : {}),
  })
}

export async function openConversation(scope: PushScope, data: unknown): Promise<void> {
  const fields = typeof data === 'object' && data !== null ? (data as Record<string, unknown>) : {}
  const conversationId = isId(fields.conversationId) ? fields.conversationId : null
  const messageId = isId(fields.messageId) ? fields.messageId : null

  const windows = await scope.clients.matchAll({ type: 'window', includeUncontrolled: true })
  const open = windows.find((window) => new URL(window.url).origin === scope.location.origin)

  // The application is already open somewhere: bring it forward and let it move to the conversation without a reload.
  if (open) {
    await open.focus()
    if (conversationId) open.postMessage({ type: OPEN_CONVERSATION, conversationId, messageId })
    return
  }

  await scope.clients.openWindow(conversationId ? `/chat/${conversationId}` : '/')
}
