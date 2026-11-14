// Where the browser fetches the service worker from. A fixed address at the root, so that it controls the whole site.
export const WORKER = '/sw.js'

let registration: Promise<ServiceWorkerRegistration | null> | null = null

// Registers the service worker, once. It does so for every page, not only for a person who turned alerts on, because the worker also
// plays encrypted video, and it can only answer for a page it controls. A browser without service workers, or one that refuses, gets
// null: what needs the worker has a way without it.
export function registerWorker(): Promise<ServiceWorkerRegistration | null> {
  registration ??=
    typeof navigator !== 'undefined' && 'serviceWorker' in navigator
      ? navigator.serviceWorker.register(WORKER).catch(() => null)
      : Promise.resolve(null)

  return registration
}
