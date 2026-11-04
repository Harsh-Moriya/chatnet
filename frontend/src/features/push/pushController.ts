import { requestAlertPermission } from '../notifications/browserAlert'
import { registerBrowser, removeBrowser } from './api/pushApi'
import { currentSubscription, pushPermission, pushSupported, subscribeBrowser, subscriptionState } from './browserPush'
import { toRegistration } from './pushKeys'
import { type PushFacts, planSync } from './pushPlan'
import { rememberWantsAlerts, wantsAlerts } from './pushPreference'

// Does what the person asked for, and what the page has to do about push when it loads. The rules are in pushPlan.ts; this joins
// them to the browser and the server.

// What is true now, for the rules. "publicKey" is null when the server has no push keys.
export async function readFacts(userId: string, publicKey: string | null): Promise<PushFacts> {
  const supported = pushSupported()

  return {
    supported,
    serverHasPush: publicKey !== null,
    permission: supported ? pushPermission() : 'default',
    wantsAlerts: wantsAlerts(userId),
    subscription: supported && publicKey !== null ? subscriptionState(await currentSubscription(), publicKey) : 'none',
  }
}

// Tells the server that this browser is registered, which is also what keeps its registration from being taken for unused.
async function register(subscription: PushSubscription): Promise<boolean> {
  const registration = toRegistration(subscription.toJSON())
  if (registration === null) {
    await subscription.unsubscribe()
    return false
  }

  await registerBrowser(registration)
  return true
}

// Run when the page loads for a signed-in person, once the server has said whether it has push keys. A failure (offline, the
// server down) is for the caller to ignore: the next load does it again.
export async function syncPush(userId: string, publicKey: string | null): Promise<void> {
  switch (planSync(await readFacts(userId, publicKey))) {
    case 'nothing':
      return
    case 'register': {
      const subscription = await currentSubscription()
      if (subscription) await register(subscription)
      return
    }
    case 'subscribe':
    case 'replace':
      // The same work: a subscription made with the current key, and the server told.
      await register(await subscribeBrowser(publicKey!))
      return
    case 'drop':
      await (await currentSubscription())?.unsubscribe()
      return
  }
}

export type TurnOnResult = 'on' | 'blocked' | 'dismissed' | 'unsupported'

// The person turned alerts on. The permission is asked for first and straight away, because a browser only shows its prompt for a
// request that comes from the click that started this.
export async function turnOnPush(userId: string, publicKey: string): Promise<TurnOnResult> {
  const permission = await requestAlertPermission()
  if (permission === 'unsupported') return 'unsupported'
  if (permission === 'denied') return 'blocked'
  if (permission !== 'granted') return 'dismissed'

  const subscription = await subscribeBrowser(publicKey)
  try {
    if (!(await register(subscription))) throw new Error('The browser gave a subscription without its keys.')
  } catch (error) {
    // Not registered with the server: a subscription that nothing would ever be pushed to is not left behind.
    await subscription.unsubscribe()
    throw error
  }

  rememberWantsAlerts(userId, true)
  return 'on'
}

// The person turned alerts off in this browser.
export async function turnOffPush(userId: string): Promise<void> {
  rememberWantsAlerts(userId, false)
  await releaseBrowser()
}

// The person is signing out, or the browser is being handed to someone else: this browser is taken off the server and the
// subscription ended, so alerts for this person stop appearing on a screen that is no longer theirs. The choice is kept, so alerts
// come back when they sign in here again. Never fails: whatever the server could not be told, the push service will say when it is
// next pushed to (the subscription is gone), and the server removes the registration then.
export async function releaseBrowser(): Promise<void> {
  try {
    if (!pushSupported()) return

    const subscription = await currentSubscription()
    if (subscription === null) return

    try {
      await removeBrowser(subscription.endpoint)
    } catch {
      // Ended locally below either way.
    }

    await subscription.unsubscribe()
  } catch {
    // Nothing more can be done from here.
  }
}
