// What the page does about push, decided from facts about the browser and the person. Pure, so the rules can be tested without a
// browser; pushController.ts gathers the facts and does what is decided.

// Does this browser have a push subscription, and was it made with the key the server has now? A server that was given new keys
// leaves the subscriptions made with the old ones useless, and the browser has to make a new one.
export type SubscriptionState = 'none' | 'current' | 'stale'

export interface PushFacts {
  // The browser has service workers, a push manager and notifications.
  supported: boolean
  // The server has push keys (it answered the request for the key).
  serverHasPush: boolean
  permission: NotificationPermission
  // The signed-in person turned alerts on in this browser. Kept per person: a browser is shared by whoever signs in on it.
  wantsAlerts: boolean
  subscription: SubscriptionState
}

// What to do when the page loads for a signed-in person. There is no click to ask for permission with, so permission is only ever
// used when it has been given.
export type SyncStep =
  | 'nothing'
  // The browser is subscribed with the right key: tell the server again, which keeps the registration from being taken for unused.
  | 'register'
  | 'subscribe'
  // Subscribed with a key the server no longer has: drop that subscription and make a new one.
  | 'replace'
  // The browser is subscribed but this person did not ask for it (someone else did, on a browser that was shared): unsubscribe, so
  // alerts for the other person stop appearing on this screen.
  | 'drop'

export function planSync(facts: PushFacts): SyncStep {
  if (!facts.supported || !facts.serverHasPush) return 'nothing'

  if (!facts.wantsAlerts) return facts.subscription === 'none' ? 'nothing' : 'drop'

  // The person turned alerts on, and then took the permission back in the browser's settings. Nothing can be shown, and the
  // browser has already ended the subscription.
  if (facts.permission !== 'granted') return 'nothing'

  switch (facts.subscription) {
    case 'none':
      return 'subscribe'
    case 'stale':
      return 'replace'
    case 'current':
      return 'register'
  }
}

// What the switch in the profile shows.
export type SwitchState =
  // The browser cannot do it.
  | 'unsupported'
  // The server has no push keys; the switch is not shown.
  | 'unavailable'
  // The person said no in the browser's settings, which only they can undo.
  | 'blocked'
  | 'off'
  | 'on'

export function switchState(facts: PushFacts): SwitchState {
  if (!facts.supported) return 'unsupported'
  if (!facts.serverHasPush) return 'unavailable'
  if (facts.permission === 'denied') return 'blocked'

  return facts.wantsAlerts && facts.permission === 'granted' && facts.subscription === 'current' ? 'on' : 'off'
}
