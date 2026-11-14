import { type ClickEventLike, type PushEventLike, type PushScope, openConversation, readAlert, showAlert } from './push'

// The service worker of ChatNET: which events it listens for and what each one does. It is installed into a scope that is handed in, so a
// test can give it a stand-in; the real one is main.ts, which hands it the worker's own scope.
//
// The worker is a classic script at a fixed address (/sw.js), fetched by the browser itself, which is why it is built apart from the page
// (see vite.sw.config.ts) and is not one of its chunks.

export interface WaitableEvent {
  waitUntil: (work: Promise<unknown>) => void
}

export interface WorkerScope extends PushScope {
  addEventListener: <E>(type: string, listener: (event: E) => void) => void
  skipWaiting: () => unknown
  clients: PushScope['clients'] & { claim: () => Promise<void> }
}

export function installWorker(scope: WorkerScope): void {
  // Take over at once when a new version is installed, so a fix to the worker does not wait for every tab to be closed.
  scope.addEventListener('install', () => {
    void scope.skipWaiting()
  })

  scope.addEventListener<WaitableEvent>('activate', (event) => {
    event.waitUntil(scope.clients.claim())
  })

  scope.addEventListener<PushEventLike & WaitableEvent>('push', (event) => {
    event.waitUntil(showAlert(scope, readAlert(event)))
  })

  scope.addEventListener<ClickEventLike & WaitableEvent>('notificationclick', (event) => {
    event.notification.close()
    event.waitUntil(openConversation(scope, event.notification.data))
  })
}
