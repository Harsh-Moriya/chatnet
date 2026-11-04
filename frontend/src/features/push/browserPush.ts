import { alertPermission } from '../notifications/browserAlert'
import { decodeKey, usesKey } from './pushKeys'
import type { SubscriptionState } from './pushPlan'

// The browser's side of push: the service worker, and the subscription the push manager holds for it. Thin and free of rules; what
// to do is decided in pushPlan.ts.

const WORKER = '/sw.js'

export function pushSupported(): boolean {
  return (
    typeof navigator !== 'undefined' &&
    'serviceWorker' in navigator &&
    typeof PushManager !== 'undefined' &&
    typeof Notification !== 'undefined'
  )
}

export function pushPermission(): NotificationPermission {
  const permission = alertPermission()
  return permission === 'unsupported' ? 'default' : permission
}

// The worker is registered when it is first needed, so a person who never turns alerts on never has one.
async function readyWorker(): Promise<ServiceWorkerRegistration> {
  await navigator.serviceWorker.register(WORKER)
  return navigator.serviceWorker.ready
}

// The subscription this browser holds, or null. Does not register the worker: a browser that has none has no subscription.
export async function currentSubscription(): Promise<PushSubscription | null> {
  const registration = await navigator.serviceWorker.getRegistration()
  return registration ? registration.pushManager.getSubscription() : null
}

export function subscriptionState(subscription: PushSubscription | null, publicKey: string): SubscriptionState {
  if (subscription === null) return 'none'
  return usesKey(subscription.options.applicationServerKey, publicKey) ? 'current' : 'stale'
}

// A subscription made with the server's key: the one the browser has when it was made with it, otherwise a new one. The push
// manager refuses a new subscription while one made with another key exists, so that one is ended first.
export async function subscribeBrowser(publicKey: string): Promise<PushSubscription> {
  const existing = await currentSubscription()
  if (existing && subscriptionState(existing, publicKey) === 'current') return existing
  if (existing) await existing.unsubscribe()

  const worker = await readyWorker()
  return worker.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: decodeKey(publicKey) })
}
