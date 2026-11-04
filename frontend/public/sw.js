// The service worker of ChatNET. The browser starts it when the server pushes an alert and the page is closed, so it is the only
// part of the application that runs then. It has two jobs: show the alert, and take the person to the conversation it is about
// when they click it. It keeps nothing and calls no server; everything it needs is in the push.
//
// It is a plain script and not part of the build: a service worker is fetched from a fixed address (/sw.js) by the browser
// itself, and the page has to be able to ask for that address at any time. The test in src/features/push/sw.test.ts runs this
// file against a stand-in for the worker scope.

const OPEN_CONVERSATION = 'open-conversation'
const ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

// Take over at once when a new version is installed, so a fix to this file does not wait for every tab to be closed.
self.addEventListener('install', () => {
  self.skipWaiting()
})

self.addEventListener('activate', (event) => {
  event.waitUntil(self.clients.claim())
})

self.addEventListener('push', (event) => {
  event.waitUntil(showAlert(readAlert(event)))
})

self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  event.waitUntil(openConversation(event.notification.data))
})

function isId(value) {
  return typeof value === 'string' && ID.test(value)
}

function text(value) {
  return typeof value === 'string' ? value : ''
}

// What the server pushed, or null when it cannot be read. The ids are checked because they become an address.
function readAlert(event) {
  let data
  try {
    data = event.data ? event.data.json() : null
  } catch {
    return null
  }

  if (typeof data !== 'object' || data === null || text(data.title) === '') return null

  return {
    title: text(data.title),
    body: text(data.body),
    tag: text(data.tag),
    conversationId: isId(data.conversationId) ? data.conversationId : null,
    messageId: isId(data.messageId) ? data.messageId : null,
  }
}

async function showAlert(alert) {
  // The person is looking at the application right now: it shows what happened itself, and an alert on top of it would only be
  // in the way. A browser lets a worker show nothing for a push only in this case.
  const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true })
  if (windows.some((window) => window.visibilityState === 'visible' && window.focused)) return

  // A push that cannot be read still gets an alert: browsers withdraw the permission of a site that is pushed to and shows nothing.
  if (alert === null) {
    await self.registration.showNotification('ChatNET', { body: 'Something new is waiting for you.', tag: 'chatnet' })
    return
  }

  await self.registration.showNotification(alert.title, {
    body: alert.body,
    // Alerts with the same tag replace each other, so a conversation shows one at a time.
    tag: alert.tag === '' ? undefined : alert.tag,
    data: { conversationId: alert.conversationId, messageId: alert.messageId },
  })
}

async function openConversation(data) {
  const conversationId = data && isId(data.conversationId) ? data.conversationId : null
  const messageId = data && isId(data.messageId) ? data.messageId : null

  const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true })
  const open = windows.find((window) => new URL(window.url).origin === self.location.origin)

  // The application is already open somewhere: bring it forward and let it move to the conversation without a reload.
  if (open) {
    await open.focus()
    if (conversationId) open.postMessage({ type: OPEN_CONVERSATION, conversationId, messageId })
    return
  }

  await self.clients.openWindow(conversationId ? `/chat/${conversationId}` : '/')
}
